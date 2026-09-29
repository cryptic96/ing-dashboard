#!/usr/bin/env bash
###
### Asserts, against the committed files under deploy/postgresql/, that the
### PostgreSQL configuration this project ships is socket-only and
### peer-only: no TCP listener is ever re-enabled, every pg_hba.conf line is
### a local/peer line mapping to the expected role, and pg_ident.conf never
### maps an OS user to a superuser role. Also self-tests each assertion
### function against deliberately broken fixtures so a silently-broken
### check can't pass by accident.
###
# The assertion functions are invoked by name through check() and
# check_fails(), which shellcheck cannot follow.
# shellcheck disable=SC2329
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=deploy/tests/lib/host-guard.sh
source "${SCRIPT_DIR}/lib/host-guard.sh"
HOST_GUARD_DIR="$(mktemp -d)"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$HOST_GUARD_DIR" "$WORK_DIR"' EXIT
host_guard_install "$HOST_GUARD_DIR"

DEPLOY_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
LEDGER_CONF="${DEPLOY_DIR}/postgresql/ledger.conf"
PG_HBA_CONF="${DEPLOY_DIR}/postgresql/pg_hba.conf"
PG_IDENT_CONF="${DEPLOY_DIR}/postgresql/pg_ident.conf"

FAILURES=0
TESTS_RUN=0

pass() {
  TESTS_RUN=$((TESTS_RUN + 1))
  echo "ok - $*"
}

failtest() {
  TESTS_RUN=$((TESTS_RUN + 1))
  FAILURES=$((FAILURES + 1))
  echo "not ok - $*"
}

check() {
  local label="$1"
  shift
  if "$@"; then
    pass "$label"
  else
    failtest "$label"
  fi
}

check_fails() {
  local label="$1"
  shift
  if "$@"; then
    failtest "$label (expected the assertion to reject this fixture, but it accepted it)"
  else
    pass "$label"
  fi
}

###
### --- assertion functions under test -------------------------------------
###

# Passes only if the file sets listen_addresses to an empty string exactly
# once, and contains no other active listen_addresses line (no second
# declaration re-enabling a TCP listener).
assert_socket_only_listen_addresses() {
  local file="$1"
  local active
  active="$(grep -vE '^\s*#' "$file" | grep -E '^\s*listen_addresses\s*=' || true)"
  local count
  count="$(printf '%s\n' "$active" | grep -c . || true)"
  [[ "$count" -eq 1 ]] || return 1
  printf '%s\n' "$active" | grep -qE "^\s*listen_addresses\s*=\s*''\s*(#.*)?$"
}

# Passes only if every non-comment, non-blank line in pg_hba.conf is a
# "local" line using "peer" authentication, and each database/role line
# maps to exactly the expected role with the expected map option.
assert_pg_hba_local_peer_only() {
  local file="$1"
  local line type db user method map

  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    [[ "$line" =~ ^# ]] && continue

    read -r type db user method map extra <<< "$line"
    [[ -n "${extra:-}" ]] && return 1
    [[ "$type" == "local" ]] || return 1
    [[ "$method" == "peer" ]] || return 1

    case "$user" in
      postgres)
        [[ "$db" == "all" ]] || return 1
        [[ -z "${map:-}" ]] || return 1
        ;;
      ledger_runtime)
        [[ "$db" == "ledger" ]] || return 1
        [[ "${map:-}" == "map=ledger_runtime_map" ]] || return 1
        ;;
      ledger_migrator)
        [[ "$db" == "ledger" ]] || return 1
        [[ -z "${map:-}" ]] || return 1
        ;;
      grafana_reader)
        [[ "$db" == "ledger" ]] || return 1
        [[ "${map:-}" == "map=grafana_reader_map" ]] || return 1
        ;;
      ledger_backup)
        [[ "$db" == "ledger" ]] || return 1
        [[ -z "${map:-}" ]] || return 1
        ;;
      *)
        return 1
        ;;
    esac
  done < "$file"

  return 0
}

# Passes only if pg_ident.conf's explicit maps are exactly the expected
# ledger_runtime_map (OS user "ledger" -> role ledger_runtime) and
# grafana_reader_map (Grafana service OS user -> role grafana_reader), and
# no line maps any OS user to a superuser-capable role such as "postgres".
assert_pg_ident_maps_expected_roles() {
  local file="$1"
  local line mapname pguser
  local seen_ledger_runtime=0 seen_grafana_reader=0

  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    [[ "$line" =~ ^# ]] && continue

    read -r mapname _ pguser <<< "$line"
    [[ "$pguser" == "postgres" ]] && return 1

    case "$mapname" in
      ledger_runtime_map)
        [[ "$pguser" == "ledger_runtime" ]] || return 1
        seen_ledger_runtime=1
        ;;
      grafana_reader_map)
        [[ "$pguser" == "grafana_reader" ]] || return 1
        seen_grafana_reader=1
        ;;
      *)
        return 1
        ;;
    esac
  done < "$file"

  [[ "$seen_ledger_runtime" -eq 1 ]] || return 1
  [[ "$seen_grafana_reader" -eq 1 ]] || return 1
  return 0
}

###
### --- assertions against the real committed files -------------------------
###

check "ledger.conf: listen_addresses is '' and set exactly once" \
  assert_socket_only_listen_addresses "$LEDGER_CONF"

check "pg_hba.conf: every rule is local/peer and maps to the expected role" \
  assert_pg_hba_local_peer_only "$PG_HBA_CONF"

check "pg_ident.conf: only the two expected role maps exist, none to a superuser" \
  assert_pg_ident_maps_expected_roles "$PG_IDENT_CONF"

###
### --- self-test: each assertion must reject a broken fixture -------------
###

bad_listen="${WORK_DIR}/ledger-bad.conf"
cp "$LEDGER_CONF" "$bad_listen"
printf "listen_addresses = '*'\n" >> "$bad_listen"
check_fails "self-test: rejects ledger.conf with a second listen_addresses line" \
  assert_socket_only_listen_addresses "$bad_listen"

bad_listen_nonempty="${WORK_DIR}/ledger-bad-nonempty.conf"
sed "s/listen_addresses = ''/listen_addresses = 'localhost'/" "$LEDGER_CONF" > "$bad_listen_nonempty"
check_fails "self-test: rejects ledger.conf with a non-empty listen_addresses" \
  assert_socket_only_listen_addresses "$bad_listen_nonempty"

bad_hba_host="${WORK_DIR}/pg_hba-bad-host.conf"
cp "$PG_HBA_CONF" "$bad_hba_host"
printf 'host     ledger    ledger_runtime  127.0.0.1/32  scram-sha-256\n' >> "$bad_hba_host"
check_fails "self-test: rejects pg_hba.conf with a host line" \
  assert_pg_hba_local_peer_only "$bad_hba_host"

bad_hba_trust="${WORK_DIR}/pg_hba-bad-trust.conf"
sed 's/local    all       postgres                 peer/local    all       postgres                 trust/' \
  "$PG_HBA_CONF" > "$bad_hba_trust"
check_fails "self-test: rejects pg_hba.conf with a trust method" \
  assert_pg_hba_local_peer_only "$bad_hba_trust"

bad_ident_superuser="${WORK_DIR}/pg_ident-bad-superuser.conf"
cp "$PG_IDENT_CONF" "$bad_ident_superuser"
printf 'ledger_runtime_map        ledger           postgres\n' >> "$bad_ident_superuser"
check_fails "self-test: rejects pg_ident.conf mapping an OS user to postgres" \
  assert_pg_ident_maps_expected_roles "$bad_ident_superuser"

bad_ident_extra_map="${WORK_DIR}/pg_ident-bad-extra.conf"
cp "$PG_IDENT_CONF" "$bad_ident_extra_map"
printf 'unexpected_map        someuser           somerole\n' >> "$bad_ident_extra_map"
check_fails "self-test: rejects pg_ident.conf with an unrecognised map name" \
  assert_pg_ident_maps_expected_roles "$bad_ident_extra_map"

echo "----"
echo "${TESTS_RUN} test(s) run, ${FAILURES} failure(s)"
if [[ "$FAILURES" -gt 0 ]]; then
  exit 1
fi
exit 0
