#!/usr/bin/env bash
# Proves the pure decision functions used by the installer: semver
# comparison, pending-migration computation, atomic release activation, the
# rollback-vs-fail decision, release pruning, and that the reported metrics
# and email content are well-formed and secret-free. Needs no root, network,
# systemd or database: everything here is exercised against a relocated
# temporary root.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

export LEDGER_DEPLOY_ROOT
LEDGER_DEPLOY_ROOT="$(mktemp -d)"
trap 'rm -rf "${LEDGER_DEPLOY_ROOT}"' EXIT
# shellcheck source=deploy/tests/lib/host-guard.sh
source "${SCRIPT_DIR}/lib/host-guard.sh"
host_guard_install "$LEDGER_DEPLOY_ROOT"

# shellcheck source=deploy/lib/common.sh
source "${REPO_ROOT}/deploy/lib/common.sh"
# shellcheck source=deploy/lib/backend-tls.sh
source "${REPO_ROOT}/deploy/lib/backend-tls.sh"
# shellcheck source=deploy/lib/deploy.sh
source "${REPO_ROOT}/deploy/lib/deploy.sh"

FAILURES=0

check() {
  local description="$1"
  local expected="$2"
  local actual="$3"
  if [ "$actual" = "$expected" ]; then
    printf 'PASS: %s\n' "$description"
  else
    printf 'FAIL: %s (expected [%s], got [%s])\n' "$description" "$expected" "$actual"
    FAILURES=$((FAILURES + 1))
  fi
}

# --- ledger_semver_gt ---------------------------------------------------

check "1.10.0 > 1.9.0" "0" "$( ledger_semver_gt 1.10.0 1.9.0; echo $? )"
check "2.0.0 > 1.99.99" "0" "$( ledger_semver_gt 2.0.0 1.99.99; echo $? )"
check "1.2.3 is not greater than 1.2.3" "1" "$( ledger_semver_gt 1.2.3 1.2.3; echo $? )"

# --- ledger_pending_migrations ------------------------------------------

MIGRATIONS_DIR="$(mktemp -d)"

MANIFEST_ABC="${MIGRATIONS_DIR}/manifest-abc.json"
printf '{"version":"1.0.0","commit":"deadbeef","migrations":["a","b","c"]}' > "$MANIFEST_ABC"

APPLIED_AB="${MIGRATIONS_DIR}/applied-ab.txt"
printf 'a\nb\n' > "$APPLIED_AB"
check "pending migrations: [a,b,c] applied [a,b] yields c" "c" \
  "$(ledger_pending_migrations "$MANIFEST_ABC" "$APPLIED_AB")"

APPLIED_ABC="${MIGRATIONS_DIR}/applied-abc.txt"
printf 'a\nb\nc\n' > "$APPLIED_ABC"
check "pending migrations: [a,b,c] applied [a,b,c] yields nothing" "" \
  "$(ledger_pending_migrations "$MANIFEST_ABC" "$APPLIED_ABC")"

APPLIED_ABCD="${MIGRATIONS_DIR}/applied-abcd.txt"
printf 'a\nb\nc\nd\n' > "$APPLIED_ABCD"
UNKNOWN_STATUS=0
ledger_pending_migrations "$MANIFEST_ABC" "$APPLIED_ABCD" >/dev/null 2>&1 || UNKNOWN_STATUS=$?
check "pending migrations: database ahead of manifest signals the unknown-migration condition" "2" "$UNKNOWN_STATUS"

# --- ledger_activate_release ---------------------------------------------

ACTIVATE_ROOT="$(mktemp -d)"
mkdir -p "${ACTIVATE_ROOT}/releases/1.0.0" "${ACTIVATE_ROOT}/releases/1.1.0" "${ACTIVATE_ROOT}/state"
ln -s "${ACTIVATE_ROOT}/releases/1.0.0" "${ACTIVATE_ROOT}/current"

ledger_activate_release "1.1.0" "${ACTIVATE_ROOT}/releases" "${ACTIVATE_ROOT}/current" "${ACTIVATE_ROOT}/state"

check "activation repoints current at the new release" "1.1.0" \
  "$(basename "$(readlink -f "${ACTIVATE_ROOT}/current")")"
check "activation records the previous version" "1.0.0" \
  "$(cat "${ACTIVATE_ROOT}/state/previous")"
check "no staging directory remains after activation" "0" \
  "$(find "${ACTIVATE_ROOT}/releases" -maxdepth 1 -name '.staging-*' 2>/dev/null | grep -qc . && echo 1 || echo 0)"

# --- ledger_rollback_decision ---------------------------------------------

check "health failed, no migration ran yields rollback" "rollback" \
  "$(ledger_rollback_decision 0)"
check "health failed after a migration yields fail-without-rollback" "fail-without-rollback" \
  "$(ledger_rollback_decision 1)"

# --- ledger_prune_releases -------------------------------------------------

PRUNE_ROOT="$(mktemp -d)"
mkdir -p "${PRUNE_ROOT}/state"
for v in 1.0.0 1.1.0 1.2.0 1.3.0 1.4.0 1.5.0 1.6.0; do
  mkdir -p "${PRUNE_ROOT}/releases/${v}"
done
ln -s "${PRUNE_ROOT}/releases/1.6.0" "${PRUNE_ROOT}/current"
printf '1.5.0\n' > "${PRUNE_ROOT}/state/previous"

ledger_prune_releases "${PRUNE_ROOT}/releases" "${PRUNE_ROOT}/current" "${PRUNE_ROOT}/state" 5

REMAINING="$(find "${PRUNE_ROOT}/releases" -mindepth 1 -maxdepth 1 -printf '%f\n' | sort -V | tr '\n' ' ')"
check "prune with seven releases and keep 5 removes the two oldest" "1.2.0 1.3.0 1.4.0 1.5.0 1.6.0 " "$REMAINING"
check "prune never removes the active release" "1" \
  "$([ -d "${PRUNE_ROOT}/releases/1.6.0" ] && echo 1 || echo 0)"
check "prune never removes the previous release" "1" \
  "$([ -d "${PRUNE_ROOT}/releases/1.5.0" ] && echo 1 || echo 0)"

# --- ledger_rollback_release: refuses when applied migrations aren't a subset ---

ROLLBACK_ROOT="$(mktemp -d)"
mkdir -p "${ROLLBACK_ROOT}/releases/1.0.0" "${ROLLBACK_ROOT}/state"
printf '{"version":"1.0.0","commit":"deadbeef","migrations":["a"]}' > "${ROLLBACK_ROOT}/releases/1.0.0/release-manifest.json"
ln -s "${ROLLBACK_ROOT}/releases/1.0.0" "${ROLLBACK_ROOT}/current"

runuser() {
  # Stand in for the real runuser: the history table exists, and its
  # applied-migrations list is NOT a subset of the target release's
  # manifest, without needing root or a real database.
  case "$*" in
    *to_regclass*) printf 't\n' ;;
    *) printf 'a\nb\n' ;;
  esac
}

ROLLBACK_EXIT=0
# Run in a subshell: ledger_die() calls exit, and this call is expected to
# hit that path, which must only end the subshell, not this test script.
( ledger_rollback_release "1.0.0" "${ROLLBACK_ROOT}/releases" "${ROLLBACK_ROOT}/current" "${ROLLBACK_ROOT}/state" \
  "http://127.0.0.1:0" "1" >/dev/null 2>&1 ) || ROLLBACK_EXIT=$?
check "rollback refuses when applied migrations are not a subset of the target manifest" "1" \
  "$([ "$ROLLBACK_EXIT" -ne 0 ] && echo 1 || echo 0)"
check "refused rollback leaves the current link untouched" "${ROLLBACK_ROOT}/releases/1.0.0" \
  "$(readlink "${ROLLBACK_ROOT}/current")"
check "refused rollback never reaches systemctl" "" "$(host_guard_calls)"

unset -f runuser

# --- ledger_install_verified_release: the proxy certificate comes first ------------

# The release this install would activate migrates the database, so an app that
# cannot start would not be rolled back. The certificate pair must therefore
# exist, or the install must stop, before anything is unpacked, migrated,
# stopped or restarted. The artifact below does not exist, so the install ends
# at the unpack step and never reaches the host.
INSTALL_ROOT="$(mktemp -d -p "$LEDGER_DEPLOY_ROOT")"
mkdir -p "${INSTALL_ROOT}/releases" "${INSTALL_ROOT}/state"

( ledger_install_verified_release "v1.0.0" "1.0.0" "${INSTALL_ROOT}/missing.zip" "" \
  "${INSTALL_ROOT}/releases" "${INSTALL_ROOT}/current" "${INSTALL_ROOT}/state" 5 "http://127.0.0.1:0" 1 >/dev/null 2>&1 ) || true

BACKEND_DIR="${LEDGER_DEPLOY_ROOT}/etc/ledger"
check "an install with no certificate creates the pair before it unpacks anything" "yes" \
  "$([ -f "${BACKEND_DIR}/backend-tls.crt" ] && [ -f "${BACKEND_DIR}/backend-tls.key" ] && echo yes || echo no)"
check "the installer creates the public certificate with mode 644" "644" "$(stat -c '%a' "${BACKEND_DIR}/backend-tls.crt")"
check "the installer creates the private key with mode 640" "640" "$(stat -c '%a' "${BACKEND_DIR}/backend-tls.key")"
check "the failed install left no staging or release directory" "" "$(find "${INSTALL_ROOT}/releases" -mindepth 1)"
check "the failed install never reached systemctl" "" "$(host_guard_calls)"

BEFORE_FP="$(openssl x509 -in "${BACKEND_DIR}/backend-tls.crt" -noout -fingerprint -sha256)"
( ledger_install_verified_release "v1.0.0" "1.0.0" "${INSTALL_ROOT}/missing.zip" "" \
  "${INSTALL_ROOT}/releases" "${INSTALL_ROOT}/current" "${INSTALL_ROOT}/state" 5 "http://127.0.0.1:0" 1 >/dev/null 2>&1 ) || true
check "a second install keeps the existing certificate" "$BEFORE_FP" \
  "$(openssl x509 -in "${BACKEND_DIR}/backend-tls.crt" -noout -fingerprint -sha256)"

ORDER_LOG="${INSTALL_ROOT}/order.log"
: > "$ORDER_LOG"
(
  ledger_ensure_backend_tls() { echo ensure >> "$ORDER_LOG"; }
  unzip() { echo unzip >> "$ORDER_LOG"; return 1; }
  ledger_install_verified_release "v1.0.0" "1.0.0" "${INSTALL_ROOT}/missing.zip" "" \
    "${INSTALL_ROOT}/releases" "${INSTALL_ROOT}/current" "${INSTALL_ROOT}/state" 5 "http://127.0.0.1:0" 1 >/dev/null 2>&1
) || true
check "the certificate step runs before the unpack step" "ensure unzip" "$(tr '\n' ' ' < "$ORDER_LOG" | sed 's/ $//')"

HALF_DIR="${INSTALL_ROOT}/half"
mkdir -p "$HALF_DIR"
: > "${HALF_DIR}/backend-tls.key"
HALF_STATUS=0
( LEDGER_BACKEND_TLS_DIR="$HALF_DIR" ledger_install_verified_release "v1.0.0" "1.0.0" "${INSTALL_ROOT}/missing.zip" "" \
  "${INSTALL_ROOT}/releases" "${INSTALL_ROOT}/current" "${INSTALL_ROOT}/state" 5 "http://127.0.0.1:0" 1 >/dev/null 2>&1 ) || HALF_STATUS=$?
check "an install with half a pair stops" "1" "$HALF_STATUS"
check "an install with half a pair changes nothing" "" "$(find "${INSTALL_ROOT}/releases" "${INSTALL_ROOT}/state" -mindepth 1)"
check "an install with half a pair never reached systemctl" "" "$(host_guard_calls)"

# --- Deploy textfile metrics content ---------------------------------------

METRICS_ROOT="$(mktemp -d)"
export LEDGER_TEXTFILE_DIR="${METRICS_ROOT}"

ledger_write_deploy_poll_metrics "1" "1700000000"
ledger_write_deploy_run_metrics "success" "1700000100" "1.2.3"

METRICS_FILE="${METRICS_ROOT}/ledger_deploy.prom"

# node_exporter reads textfile metrics as its own service user, so the file
# must be world-readable even when the writer runs under a strict umask (the
# backup unit uses UMask=0077).
( umask 077; ledger_write_textfile_metrics "strict_umask" "ledger_test_metric 1" )
check "textfile metrics are readable by node_exporter under a strict umask" "644" \
  "$(stat -c '%a' "${METRICS_ROOT}/strict_umask.prom")"
check "the deploy metrics file is readable by node_exporter" "644" "$(stat -c '%a' "$METRICS_FILE")"

check "textfile has ledger_deploy_last_poll_timestamp_seconds" "1" \
  "$(grep -c '^ledger_deploy_last_poll_timestamp_seconds ' "$METRICS_FILE" || true)"
check "textfile has ledger_deploy_last_poll_success" "1" \
  "$(grep -c '^ledger_deploy_last_poll_success ' "$METRICS_FILE" || true)"
check "textfile has ledger_deploy_last_run_timestamp_seconds" "1" \
  "$(grep -c '^ledger_deploy_last_run_timestamp_seconds ' "$METRICS_FILE" || true)"
check "textfile has ledger_deploy_last_run_success" "1" \
  "$(grep -c '^ledger_deploy_last_run_success ' "$METRICS_FILE" || true)"
check "textfile has ledger_deploy_last_run_rolled_back" "1" \
  "$(grep -c '^ledger_deploy_last_run_rolled_back ' "$METRICS_FILE" || true)"
check "textfile has ledger_deploy_current_release_info" "1" \
  "$(grep -c '^ledger_deploy_current_release_info{' "$METRICS_FILE" || true)"
check "current_release_info version label matches the installed version" "1" \
  "$(grep -c '^ledger_deploy_current_release_info{version="1.2.3"} 1$' "$METRICS_FILE" || true)"
check "poll metrics survive being written before the run metrics" "1" \
  "$(grep -c '^ledger_deploy_last_poll_success 1$' "$METRICS_FILE" || true)"

# --- Email body content -----------------------------------------------------

for result in success rolled_back failed; do
  BODY="$(ledger_render_deploy_email_body "1.2.3" "$result" "2026-01-01T00:00:00Z" "2026-01-01T00:05:00Z")"
  check "email body for ${result} contains only version/result/timestamps" "0" \
    "$(printf '%s' "$BODY" | grep -ciE '/etc/|/var/|Host=|Username=|-----BEGIN' || true)"
  check "email body for ${result} names the result" "1" \
    "$(printf '%s' "$BODY" | grep -c "result: ${result}" || true)"
done

check "no check in this file reached the real systemctl" "" "$(host_guard_calls)"

if [ "$FAILURES" -ne 0 ]; then
  printf '%d check(s) failed\n' "$FAILURES" >&2
  exit 1
fi

printf 'All checks passed\n'
