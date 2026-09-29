#!/usr/bin/env bash
# Proves ledger-selfcheck reports the right PASS/FAIL/SKIP line for each
# targeted check when run against a stubbed host: a GOOD host first, then
# specific BAD hosts. Every host tool the script can reach (systemctl, ss,
# nft, runuser, psql, curl, jq, gh, pgrep, readlink) is stubbed so nothing
# real is ever touched. Only the targeted lines are asserted; the script
# also checks real files (e.g. /etc/ledger) that never exist on a developer
# machine, so their FAIL lines are expected noise and are ignored here.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
SELFCHECK="${REPO_ROOT}/deploy/bin/ledger-selfcheck"

FAILURES=0

check() {
  local description="$1" expected="$2" actual="$3"
  if [ "$actual" = "$expected" ]; then
    printf 'PASS: %s\n' "$description"
  else
    printf 'FAIL: %s (expected [%s], got [%s])\n' "$description" "$expected" "$actual"
    FAILURES=$((FAILURES + 1))
  fi
}

# Asserts that OUTPUT contains a line equal to "PREFIX - SUFFIX" exactly.
assert_line() {
  local description="$1" output="$2" line="$3"
  local found=0
  if grep -qxF "$line" <<< "$output"; then
    found=1
  fi
  check "$description" "1" "$found"
}

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

OWN_BIN="${WORKDIR}/own-bin"
mkdir -p "$OWN_BIN"

# shellcheck source=deploy/tests/lib/host-guard.sh
source "${SCRIPT_DIR}/lib/host-guard.sh"
GUARD_DIR="${WORKDIR}/guard"
mkdir -p "$GUARD_DIR"
host_guard_install "$GUARD_DIR"
# host_guard_install already prepended GUARD_DIR to PATH; put OWN_BIN ahead
# of it so our own working systemctl stub is found before the guard's
# always-refusing one.
export PATH="${OWN_BIN}:${PATH}"

# --- stub: systemctl -------------------------------------------------------
cat > "${OWN_BIN}/systemctl" <<'EOF_STUB'
#!/usr/bin/env bash
sub="$1"; shift || true
unit=""
for a in "$@"; do
  case "$a" in
    --*) ;;
    *) [[ -z "$unit" ]] && unit="$a" ;;
  esac
done
case "$sub" in
  is-active)
    for u in ${STUB_ACTIVE_UNITS:-}; do [[ "$u" == "$unit" ]] && exit 0; done
    exit 3 ;;
  is-enabled)
    for u in ${STUB_ENABLED_UNITS:-}; do [[ "$u" == "$unit" ]] && exit 0; done
    exit 1 ;;
  list-units)
    if [[ -n "${STUB_PG_SERVICE_NAME:-}" ]]; then
      printf '%s loaded active running PostgreSQL Cluster\n' "$STUB_PG_SERVICE_NAME"
    fi
    exit 0 ;;
  restart) exit 0 ;;
  *) exit 0 ;;
esac
EOF_STUB
chmod +x "${OWN_BIN}/systemctl"

# --- stub: runuser (ignores the user switch, just execs the command) ------
cat > "${OWN_BIN}/runuser" <<'EOF_STUB'
#!/usr/bin/env bash
while [[ $# -gt 0 && "$1" != "--" ]]; do shift; done
shift || true
exec "$@"
EOF_STUB
chmod +x "${OWN_BIN}/runuser"

# --- stub: psql -------------------------------------------------------------
cat > "${OWN_BIN}/psql" <<'EOF_STUB'
#!/usr/bin/env bash
args="$*"
case "$args" in
  *"SHOW listen_addresses"*)
    printf '%s' "${STUB_LISTEN_ADDRESSES:-}"
    exit 0 ;;
  *"pg_hba_file_rules"*)
    printf '%s' "${STUB_HBA_NONLOCAL:-0}"
    exit 0 ;;
  *"rolsuper"*)
    printf '%s' "${STUB_ROLSUPER:-f}"
    exit 0 ;;
  *"CREATE TABLE ledger_selfcheck_probe"*)
    printf '%s' "${STUB_CREATE_TABLE_OUTPUT:-ERROR:  42501: permission denied for schema public}"
    exit 1 ;;
  *"data_protection_keys"*)
    printf '%s' "${STUB_GRAFANA_READER_OUTPUT:-ERROR:  42501: permission denied for table data_protection_keys}"
    exit 1 ;;
  *"ledger_migrator"*)
    exit "${STUB_MIGRATOR_LOGIN_EXIT:-1}" ;;
  *) exit 0 ;;
esac
EOF_STUB
chmod +x "${OWN_BIN}/psql"

# --- stub: ss ----------------------------------------------------------------
cat > "${OWN_BIN}/ss" <<'EOF_STUB'
#!/usr/bin/env bash
printf '%s\n' "${STUB_SS_OUTPUT:-}"
EOF_STUB
chmod +x "${OWN_BIN}/ss"

# --- stub: nft -----------------------------------------------------------
cat > "${OWN_BIN}/nft" <<'EOF_STUB'
#!/usr/bin/env bash
case "$*" in
  "list tables")
    printf '%s\n' "${STUB_NFT_TABLES:-inet ledger_filter}" ;;
  "list chain inet ledger_filter input")
    printf '%s\n' "${STUB_NFT_CHAIN:-}" ;;
  *) exit 0 ;;
esac
EOF_STUB
chmod +x "${OWN_BIN}/nft"

# --- stub: gh / pgrep (secrets hygiene noise, not asserted) ----------------
cat > "${OWN_BIN}/gh" <<'EOF_STUB'
#!/usr/bin/env bash
exit "${STUB_GH_AUTH_EXIT:-1}"
EOF_STUB
chmod +x "${OWN_BIN}/gh"

cat > "${OWN_BIN}/pgrep" <<'EOF_STUB'
#!/usr/bin/env bash
exit 1
EOF_STUB
chmod +x "${OWN_BIN}/pgrep"

# --- stub: readlink (only intercepts /opt/ledger/current) ------------------
cat > "${OWN_BIN}/readlink" <<'EOF_STUB'
#!/usr/bin/env bash
if [[ "$*" == "-f /opt/ledger/current" ]]; then
  printf '%s' "${STUB_CURRENT_RELEASE_PATH:-}"
  exit 0
fi
exec /usr/bin/readlink "$@"
EOF_STUB
chmod +x "${OWN_BIN}/readlink"

# --- stub: jq (only intercepts the release manifest path) ------------------
cat > "${OWN_BIN}/jq" <<'EOF_STUB'
#!/usr/bin/env bash
for a in "$@"; do
  case "$a" in
    */release-manifest.json)
      printf '%s' "${STUB_MANIFEST_COMMIT:-}"
      exit 0 ;;
  esac
done
exec /usr/bin/jq "$@"
EOF_STUB
chmod +x "${OWN_BIN}/jq"

# --- stub: curl --------------------------------------------------------------
# Handles two shapes the script uses: plain GET (optionally with
# --write-out '%{http_code}' and --output /dev/null) and the
# --config - form used by grafana_admin_get, whose config text (read from
# stdin) is parsed for its url/user/header lines.
cat > "${OWN_BIN}/curl" <<'EOF_STUB'
#!/usr/bin/env bash
has_config=0
has_write_out=0
url=""
for a in "$@"; do
  case "$a" in
    --config) has_config=1 ;;
    --write-out) has_write_out=1 ;;
    http://*) url="$a" ;;
  esac
done

if [[ "$has_config" -eq 1 ]]; then
  cfg="$(cat)"
  cfg_url="$(sed -n 's/^url = "\(.*\)"$/\1/p' <<< "$cfg")"
  userpass="$(sed -n 's/^user = "\(.*\)"$/\1/p' <<< "$cfg")"
  user="${userpass%%:*}"
  pass="${userpass#*:}"
  path="${cfg_url#http://127.0.0.1:3000}"
  case "$path" in
    /api/org/users)
      if [[ "$user" == "${STUB_GRAFANA_ADMIN_USER:-}" && "$pass" == "${STUB_GRAFANA_ADMIN_PASS:-}" ]]; then
        printf '%s' "${STUB_GRAFANA_ORG_USERS:-[]}"
        exit 0
      fi
      exit 22 ;;
    /api/org)
      if [[ "$pass" == "admin" ]]; then
        if [[ "${STUB_GRAFANA_DEFAULT_PASSWORD_WORKS:-0}" == "1" ]]; then
          printf '{}'; exit 0
        fi
        exit 22
      fi
      if [[ "$user" == "${STUB_GRAFANA_ADMIN_USER:-}" && "$pass" == "${STUB_GRAFANA_ADMIN_PASS:-}" ]]; then
        printf '{}'; exit 0
      fi
      exit 22 ;;
    /api/admin/settings)
      if [[ "$user" == "${STUB_GRAFANA_ADMIN_USER:-}" && "$pass" == "${STUB_GRAFANA_ADMIN_PASS:-}" ]]; then
        printf '%s' "${STUB_GRAFANA_SETTINGS:-{\"auth_anonymous\":{\"enabled\":\"false\"},\"snapshots\":{\"enabled\":\"false\"}}}"
        exit 0
      fi
      exit 22 ;;
    /api/datasources/uid/*/health)
      if [[ "$user" == "${STUB_GRAFANA_ADMIN_USER:-}" && "$pass" == "${STUB_GRAFANA_ADMIN_PASS:-}" ]]; then
        printf '{"status":"OK"}'; exit 0
      fi
      exit 22 ;;
    *) exit 22 ;;
  esac
fi

case "$url" in
  */health)
    printf '%s' "${STUB_HEALTH_BODY:-Healthy}"
    exit "${STUB_HEALTH_EXIT:-0}" ;;
  */metrics)
    printf '%s' "${STUB_METRICS_BODY:-}"
    exit "${STUB_METRICS_EXIT:-0}" ;;
  */api/v1/status)
    if [[ "$has_write_out" -eq 1 ]]; then
      printf '%s' "${STUB_STATUS_CODE:-401}"
      exit 0
    fi
    exit 0 ;;
  */api/search)
    if [[ "$has_write_out" -eq 1 ]]; then
      printf '%s' "${STUB_GRAFANA_ANON_CODE:-401}"
      exit 0
    fi
    exit 0 ;;
  */api/v1/targets)
    dir="${STUB_PROM_TARGETS_DIR:-}"
    if [[ -z "$dir" ]]; then
      printf '%s' '{"data":{"activeTargets":[]}}'
      exit 0
    fi
    countfile="${dir}/counter"
    n=0
    [[ -f "$countfile" ]] && n="$(cat "$countfile")"
    n=$((n + 1))
    printf '%s' "$n" > "$countfile"
    maxn="${STUB_PROM_TARGETS_CALLS:-1}"
    [[ "$n" -gt "$maxn" ]] && n="$maxn"
    cat "${dir}/call-${n}.json"
    exit 0 ;;
  *) exit 0 ;;
esac
EOF_STUB
chmod +x "${OWN_BIN}/curl"

# --- baseline GOOD host environment -----------------------------------------
# A minimal, realistic ss -Hltnp listing: the app, prometheus, node_exporter,
# grafana, sshd and the loopback DNS stub resolver, nothing on 5432 and
# nothing owned by postgres.
GOOD_SS=$'LISTEN 0 4096 127.0.0.1:5081 0.0.0.0:* users:(("dotnet",pid=111,fd=9))\nLISTEN 0 4096 127.0.0.1:9090 0.0.0.0:* users:(("prometheus",pid=112,fd=9))\nLISTEN 0 4096 127.0.0.1:9100 0.0.0.0:* users:(("node_exporter",pid=113,fd=9))\nLISTEN 0 4096 127.0.0.1:5080 0.0.0.0:* users:(("dotnet",pid=111,fd=10))\nLISTEN 0 4096 127.0.0.1:3000 0.0.0.0:* users:(("grafana",pid=114,fd=9))\nLISTEN 0 4096 *:22 *:* users:(("sshd",pid=1,fd=3))\nLISTEN 0 4096 127.0.0.53%lo:53 0.0.0.0:* users:(("systemd-resolve",pid=2,fd=3))\nLISTEN 0 4096 127.0.0.54%lo:53 0.0.0.0:* users:(("systemd-resolve",pid=2,fd=3))'

good_nft_chain() {
  local i out=""
  for ((i = 0; i < 60; i++)); do
    out+="\t\tct state established,related accept comment \"filler-${i}\"\n"
  done
  printf 'table inet ledger_filter {\n\tchain input {\n\t\ttype filter hook input priority filter; policy drop;\n%b\t}\n}\n' "$out"
}
GOOD_NFT_CHAIN="$(good_nft_chain)"

setup_good_env() {
  unset STUB_LISTEN_ADDRESSES STUB_HBA_NONLOCAL STUB_CREATE_TABLE_OUTPUT \
    STUB_GRAFANA_READER_OUTPUT STUB_MIGRATOR_LOGIN_EXIT STUB_SS_OUTPUT \
    STUB_NFT_TABLES STUB_NFT_CHAIN STUB_HEALTH_BODY STUB_METRICS_BODY \
    STUB_STATUS_CODE STUB_GRAFANA_ANON_CODE STUB_PROM_TARGETS_DIR \
    STUB_PROM_TARGETS_CALLS STUB_CURRENT_RELEASE_PATH STUB_MANIFEST_COMMIT \
    STUB_GRAFANA_ADMIN_USER STUB_GRAFANA_ADMIN_PASS STUB_GRAFANA_ORG_USERS \
    STUB_GRAFANA_SETTINGS STUB_GRAFANA_DEFAULT_PASSWORD_WORKS
  export STUB_ACTIVE_UNITS="prometheus prometheus-node-exporter grafana-server ledger ledger-deploy-poll.timer ledger-backup.timer"
  export STUB_ENABLED_UNITS="ledger-deploy-poll.timer ledger-backup.timer"
  export STUB_PG_SERVICE_NAME="postgresql@16-main.service"
  export STUB_HBA_NONLOCAL="0"
  export STUB_ROLSUPER="f"
  export STUB_MIGRATOR_LOGIN_EXIT="1"
  export STUB_SS_OUTPUT="$GOOD_SS"
  export STUB_NFT_TABLES="inet ledger_filter"
  export STUB_NFT_CHAIN="$GOOD_NFT_CHAIN"
  export STUB_STATUS_CODE="401"
  export STUB_GRAFANA_ANON_CODE="401"
}

run_selfcheck() {
  "$SELFCHECK" "$@" 2>&1
}

# =====================================================================
# Listeners
# =====================================================================
setup_good_env
OUT="$(run_selfcheck --pre-deploy)"
assert_line "GOOD host: no TCP socket owned by postgres" "$OUT" \
  "PASS - no TCP socket is owned by postgres"
assert_line "GOOD host: port 9100 bound to loopback only" "$OUT" \
  "PASS - port 9100 is bound to loopback only"
assert_line "GOOD host: no unexpected listeners" "$OUT" \
  "PASS - no unexpected listeners besides sshd and the loopback DNS stub"

setup_good_env
export STUB_SS_OUTPUT="${GOOD_SS}"$'\n''LISTEN 0 4096 0.0.0.0:5432 0.0.0.0:* users:(("postgres",pid=200,fd=7))'
OUT="$(run_selfcheck --pre-deploy)"
assert_line "BAD host: a postgres TCP listener fails" "$OUT" \
  "FAIL - a TCP socket is owned by postgres; PostgreSQL must be socket-only"

setup_good_env
export STUB_SS_OUTPUT="${GOOD_SS//127.0.0.1:9100/0.0.0.0:9100}"
OUT="$(run_selfcheck --pre-deploy)"
assert_line "BAD host: port 9100 bound to 0.0.0.0 fails" "$OUT" \
  "FAIL - port 9100 is bound beyond loopback: LISTEN 0 4096 0.0.0.0:9100 0.0.0.0:* users:((\"node_exporter\",pid=113,fd=9))"

setup_good_env
export STUB_SS_OUTPUT="${GOOD_SS}"$'\n''LISTEN 0 4096 0.0.0.0:8080 0.0.0.0:* users:(("mystery",pid=999,fd=3))'
OUT="$(run_selfcheck --pre-deploy)"
FOUND=0
grep -qxF "FAIL - unexpected listener(s) found: LISTEN 0 4096 0.0.0.0:8080 0.0.0.0:* users:((\"mystery\",pid=999,fd=3))" <<< "$OUT" && FOUND=1
check "BAD host: an unexpected listener fails" "1" "$FOUND"

# =====================================================================
# Firewall
# =====================================================================
setup_good_env
OUT="$(run_selfcheck --pre-deploy)"
assert_line "GOOD host: nftables table exists" "$OUT" \
  "PASS - nftables table inet ledger_filter exists"
assert_line "GOOD host: policy drop present in a large chain listing passes" "$OUT" \
  "PASS - input chain policy is drop"

setup_good_env
export STUB_NFT_CHAIN='table inet ledger_filter {
	chain input {
		type filter hook input priority filter; policy accept;
	}
}'
OUT="$(run_selfcheck --pre-deploy)"
assert_line "BAD host: policy drop absent fails" "$OUT" \
  "FAIL - input chain policy is not drop"

# The reverse-proxy rule must match the configured address exactly: a rule
# for an address that merely shares its prefix does not count.
PROVISION_FIXTURE="$(mktemp -p "$WORKDIR")"
printf 'LEDGER_TRAEFIK_IP=192.0.2.8\nLEDGER_GRAFANA_DOMAIN=grafana.example.org\n' > "$PROVISION_FIXTURE"
export LEDGER_PROVISION_CONF="$PROVISION_FIXTURE"

setup_good_env
STUB_NFT_CHAIN="$(printf '%s\n\t\ttcp dport { 3000, 5080 } ip saddr 192.0.2.8 accept\n' "$GOOD_NFT_CHAIN")"
export STUB_NFT_CHAIN
OUT="$(run_selfcheck --pre-deploy)"
assert_line "GOOD host: the rule for the configured proxy address passes" "$OUT" \
  "PASS - a rule admits 5080/3000 only from the configured reverse proxy address"

setup_good_env
STUB_NFT_CHAIN="$(printf '%s\n\t\ttcp dport { 3000, 5080 } ip saddr 192.0.2.80 accept\n' "$GOOD_NFT_CHAIN")"
export STUB_NFT_CHAIN
OUT="$(run_selfcheck --pre-deploy)"
assert_line "BAD host: a rule for a prefix-sharing address does not count" "$OUT" \
  "FAIL - no rule found admitting 5080/3000 from the configured reverse proxy address"

# =====================================================================
# PostgreSQL
# =====================================================================
setup_good_env
export STUB_LISTEN_ADDRESSES="127.0.0.1"
OUT="$(run_selfcheck --pre-deploy)"
assert_line "BAD host: non-empty listen_addresses fails" "$OUT" \
  "FAIL - PostgreSQL listen_addresses is '127.0.0.1', expected empty"

setup_good_env
export STUB_CREATE_TABLE_OUTPUT="ERROR:  42501: permission denied for schema public"
OUT="$(run_selfcheck --pre-deploy)"
assert_line "GOOD host: verbose 42501 passes the CREATE TABLE probe" "$OUT" \
  "PASS - ledger_runtime cannot CREATE TABLE (42501 insufficient_privilege)"

setup_good_env
export STUB_GRAFANA_READER_OUTPUT="ERROR:  42501: permission denied for table data_protection_keys"
OUT="$(run_selfcheck --pre-deploy)"
assert_line "GOOD host: verbose 42501 passes the grafana_reader probe" "$OUT" \
  "PASS - grafana_reader cannot read data_protection_keys (42501 insufficient_privilege), proving the ledger-reporting peer mapping"

setup_good_env
export STUB_GRAFANA_READER_OUTPUT="ERROR:  42P01: relation \"public.data_protection_keys\" does not exist"
OUT="$(run_selfcheck --pre-deploy)"
assert_line "PRE-DEPLOY host: 42P01 grafana_reader probe is skipped" "$OUT" \
  "SKIP - grafana_reader read check: data_protection_keys does not exist until the first release migrates the schema"

# =====================================================================
# Prometheus targets
# =====================================================================
setup_good_env
PROM_DIR="${WORKDIR}/prom-unknown-then-up"
mkdir -p "$PROM_DIR"
cat > "${PROM_DIR}/call-1.json" <<'EOF_JSON'
{"data":{"activeTargets":[{"labels":{"job":"node"},"health":"unknown"}]}}
EOF_JSON
cat > "${PROM_DIR}/call-2.json" <<'EOF_JSON'
{"data":{"activeTargets":[{"labels":{"job":"node"},"health":"up"}]}}
EOF_JSON
export STUB_PROM_TARGETS_DIR="$PROM_DIR"
export STUB_PROM_TARGETS_CALLS=2
OUT="$(run_selfcheck --pre-deploy)"
assert_line "an unknown target that becomes up within the wait passes" "$OUT" \
  "PASS - every Prometheus target is up (the app target is skipped until a release is installed)"

setup_good_env
PROM_DIR2="${WORKDIR}/prom-down"
mkdir -p "$PROM_DIR2"
cat > "${PROM_DIR2}/call-1.json" <<'EOF_JSON'
{"data":{"activeTargets":[{"labels":{"job":"ledger"},"health":"down"}]}}
EOF_JSON
export STUB_PROM_TARGETS_DIR="$PROM_DIR2"
export STUB_PROM_TARGETS_CALLS=1
OUT="$(run_selfcheck)"
assert_line "a down target fails" "$OUT" \
  "FAIL - 1 Prometheus target(s) are not up"

setup_good_env
PROM_DIR3="${WORKDIR}/prom-pre-deploy-skip"
mkdir -p "$PROM_DIR3"
cat > "${PROM_DIR3}/call-1.json" <<'EOF_JSON'
{"data":{"activeTargets":[{"labels":{"job":"ledger"},"health":"down"},{"labels":{"job":"node"},"health":"up"}]}}
EOF_JSON
export STUB_PROM_TARGETS_DIR="$PROM_DIR3"
export STUB_PROM_TARGETS_CALLS=1
OUT="$(run_selfcheck --pre-deploy)"
assert_line "under --pre-deploy the ledger job is skipped" "$OUT" \
  "PASS - every Prometheus target is up (the app target is skipped until a release is installed)"

# =====================================================================
# Grafana anonymous access (check_grafana_public)
# =====================================================================
setup_good_env
export STUB_GRAFANA_ANON_CODE="401"
OUT="$(run_selfcheck --pre-deploy)"
assert_line "anonymous /api/search returning 401 passes" "$OUT" \
  "PASS - anonymous GET /api/search returns 401"

setup_good_env
export STUB_GRAFANA_ANON_CODE="301"
OUT="$(run_selfcheck --pre-deploy)"
assert_line "anonymous /api/search returning 301 fails" "$OUT" \
  "FAIL - anonymous GET /api/search returned 301, expected 401"

setup_good_env
export STUB_GRAFANA_ANON_CODE="200"
OUT="$(run_selfcheck --pre-deploy)"
assert_line "anonymous /api/search returning 200 fails" "$OUT" \
  "FAIL - anonymous GET /api/search returned 200, expected 401"

# =====================================================================
# Grafana admin (check_grafana_admin, --grafana-admin)
# =====================================================================
setup_good_env
export STUB_GRAFANA_ADMIN_USER="householdadmin"
export STUB_GRAFANA_ADMIN_PASS="Sup3rSecretPassphrase!"
export STUB_GRAFANA_ORG_USERS='[{"login":"householdadmin","role":"Admin"},{"login":"partner","role":"Viewer"}]'
export STUB_GRAFANA_DEFAULT_PASSWORD_WORKS="0"
OUT="$(printf '%s\n%s\n' "$STUB_GRAFANA_ADMIN_USER" "$STUB_GRAFANA_ADMIN_PASS" \
  | { "$SELFCHECK" --pre-deploy --grafana-admin 2>&1 || true; })"
assert_line "a non-default admin login passes" "$OUT" \
  "PASS - Grafana admin login is not the default 'admin'"
assert_line "the admin account rejecting the default password passes" "$OUT" \
  "PASS - the Grafana admin account does not accept the default password"
assert_line "every non-admin user having the Viewer role passes" "$OUT" \
  "PASS - every non-admin Grafana user has the Viewer role"

setup_good_env
export STUB_GRAFANA_ADMIN_USER="admin"
export STUB_GRAFANA_ADMIN_PASS="Sup3rSecretPassphrase!"
export STUB_GRAFANA_ORG_USERS='[{"login":"admin","role":"Admin"}]'
OUT="$(printf '%s\n%s\n' "$STUB_GRAFANA_ADMIN_USER" "$STUB_GRAFANA_ADMIN_PASS" \
  | { "$SELFCHECK" --pre-deploy --grafana-admin 2>&1 || true; })"
assert_line "an admin still named admin fails" "$OUT" \
  "FAIL - Grafana admin login is still the default 'admin'"

setup_good_env
export STUB_GRAFANA_ADMIN_USER="householdadmin"
export STUB_GRAFANA_ADMIN_PASS="Sup3rSecretPassphrase!"
export STUB_GRAFANA_ORG_USERS='[{"login":"householdadmin","role":"Admin"},{"login":"partner","role":"Editor"}]'
OUT="$(printf '%s\n%s\n' "$STUB_GRAFANA_ADMIN_USER" "$STUB_GRAFANA_ADMIN_PASS" \
  | { "$SELFCHECK" --pre-deploy --grafana-admin 2>&1 || true; })"
assert_line "a non-Viewer non-admin user fails" "$OUT" \
  "FAIL - at least one non-admin Grafana user does not have the Viewer role"

setup_good_env
export STUB_GRAFANA_ADMIN_USER="householdadmin"
export STUB_GRAFANA_ADMIN_PASS="Sup3rSecretPassphrase!"
export STUB_GRAFANA_ORG_USERS='[{"login":"householdadmin","role":"Admin"}]'
export STUB_GRAFANA_DEFAULT_PASSWORD_WORKS="1"
OUT="$(printf '%s\n%s\n' "$STUB_GRAFANA_ADMIN_USER" "$STUB_GRAFANA_ADMIN_PASS" \
  | { "$SELFCHECK" --pre-deploy --grafana-admin 2>&1 || true; })"
assert_line "the default password being accepted fails" "$OUT" \
  "FAIL - the Grafana admin account still accepts the default password"

# =====================================================================
# Build info parsed from /metrics
# =====================================================================
setup_good_env
export STUB_HEALTH_BODY="Healthy"
export STUB_METRICS_BODY='# HELP ledger_build_info The running build.
# TYPE ledger_build_info gauge
ledger_build_info{version="1.4.2",commit="abcdef0123456789abcdef0123456789abcdef01"} 1'
export STUB_CURRENT_RELEASE_PATH="/opt/ledger/releases/1.4.2"
export STUB_MANIFEST_COMMIT="abcdef0123456789abcdef0123456789abcdef01"
OUT="$(run_selfcheck)"
assert_line "the version after a HELP comment line is parsed correctly" "$OUT" \
  "PASS - ledger_build_info version matches the current release (1.4.2)"
assert_line "the commit after a HELP comment line is parsed correctly" "$OUT" \
  "PASS - the running build's commit matches the release manifest (abcdef012345)"

echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "All checks passed."
  exit 0
else
  echo "${FAILURES} check(s) failed."
  exit 1
fi
