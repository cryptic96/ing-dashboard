#!/usr/bin/env bash
# Exercises deploy/bin/ledger-restore end to end (drill and live restore)
# against stubbed psql, pg_restore, runuser, age, curl, sleep and systemctl,
# with a relocated backup root, so the real script's decisions can be
# observed without a database, a real age binary or root.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
RESTORE_BIN="${REPO_ROOT}/deploy/bin/ledger-restore"

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

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT
# shellcheck source=deploy/tests/lib/host-guard.sh
source "${SCRIPT_DIR}/lib/host-guard.sh"
mkdir -p "${WORKDIR}/host-guard"
host_guard_install "${WORKDIR}/host-guard"

SCRATCH_DB_NAME="ledger_restore_drill"

STUB_BIN="${WORKDIR}/stub-bin"
mkdir -p "$STUB_BIN"
CALL_LOG="${WORKDIR}/calls.log"
: > "$CALL_LOG"
CURL_HEALTH_COUNT_FILE="${WORKDIR}/curl-health-count"
PG_RESTORE_STDIN_CAPTURE="${WORKDIR}/pg-restore-stdin.txt"

cat > "${STUB_BIN}/runuser" <<'EOF_STUB'
#!/usr/bin/env bash
printf 'runuser %s\n' "$*" >> "$CALL_LOG"
shift 3
exec "$@"
EOF_STUB
chmod +x "${STUB_BIN}/runuser"

cat > "${STUB_BIN}/psql" <<EOF_STUB
#!/usr/bin/env bash
printf 'psql %s\n' "\$*" >> "\$CALL_LOG"
args=("\$@")
last="\${args[-1]}"
case "\$*" in
  *"SELECT string_agg"*"__EFMigrationsHistory"*)
    if [ "\$last" = "${SCRATCH_DB_NAME}" ]; then
      printf '%s\n' "\${FAKE_DRILL_HISTORY:-}"
    else
      printf '%s\n' "\${FAKE_LIVE_HISTORY:-}"
    fi
    ;;
  *"data_protection_canary"*)
    printf '%s\n' "\${FAKE_CANARY_COUNT:-1}"
    ;;
  *"n_live_tup"*)
    if [ "\$last" = "${SCRATCH_DB_NAME}" ]; then
      printf '%s\n' "\${FAKE_DRILL_COUNTS:-same-counts}"
    else
      printf '%s\n' "\${FAKE_LIVE_COUNTS:-same-counts}"
    fi
    ;;
  *)
    exit 0
    ;;
esac
EOF_STUB
chmod +x "${STUB_BIN}/psql"

cat > "${STUB_BIN}/pg_restore" <<'EOF_STUB'
#!/usr/bin/env bash
printf 'pg_restore %s\n' "$*" >> "$CALL_LOG"
cat > "${PG_RESTORE_STDIN_CAPTURE:-/dev/null}"
if [ "${FAKE_PG_RESTORE_FAIL:-0}" = "1" ]; then
  exit 1
fi
exit 0
EOF_STUB
chmod +x "${STUB_BIN}/pg_restore"

cat > "${STUB_BIN}/age" <<'EOF_STUB'
#!/usr/bin/env bash
printf 'age %s\n' "$*" >> "$CALL_LOG"
for a in "$@"; do
  if [ "$a" = "/dev/stdin" ]; then
    cat /dev/stdin > /dev/null
    break
  fi
done
printf 'FAKE-PLAINTEXT-DUMP-CONTENT\n'
EOF_STUB
chmod +x "${STUB_BIN}/age"

cat > "${STUB_BIN}/curl" <<'EOF_STUB'
#!/usr/bin/env bash
printf 'curl %s\n' "$*" >> "$CALL_LOG"
n=0
[ -f "$CURL_HEALTH_COUNT_FILE" ] && n="$(cat "$CURL_HEALTH_COUNT_FILE")"
n=$((n + 1))
printf '%s\n' "$n" > "$CURL_HEALTH_COUNT_FILE"
threshold="${FAKE_HEALTH_SUCCESS_AT:-1}"
if [ "$threshold" != "never" ] && [ "$n" -ge "$threshold" ]; then
  printf 'Healthy\n'
else
  printf 'Unhealthy\n'
fi
EOF_STUB
chmod +x "${STUB_BIN}/curl"

cat > "${STUB_BIN}/sleep" <<'EOF_STUB'
#!/usr/bin/env bash
exit 0
EOF_STUB
chmod +x "${STUB_BIN}/sleep"

cat > "${STUB_BIN}/systemctl" <<'EOF_STUB'
#!/usr/bin/env bash
printf 'systemctl %s\n' "$*" >> "$CALL_LOG"
exit 0
EOF_STUB
chmod +x "${STUB_BIN}/systemctl"

BACKUP_ROOT="${WORKDIR}/backups"
mkdir -p "$BACKUP_ROOT"
RELEASE_SQL_DIR="${WORKDIR}/release-sql"
mkdir -p "$RELEASE_SQL_DIR"
printf -- '-- synthetic bootstrap sql for tests\n' > "${RELEASE_SQL_DIR}/bootstrap-database.sql"

GOOD_BACKUP="${BACKUP_ROOT}/ledger-20260101T000000Z-nightly.dump.age"
printf 'FAKE-ENCRYPTED-BACKUP-CONTENT\n' > "$GOOD_BACKUP"

OUTSIDE_DIR="${WORKDIR}/outside"
mkdir -p "$OUTSIDE_DIR"
OUTSIDE_BACKUP="${OUTSIDE_DIR}/ledger-20260101T000000Z-nightly.dump.age"
printf 'FAKE-ENCRYPTED-BACKUP-CONTENT\n' > "$OUTSIDE_BACKUP"

# A synthetic age identity that is never committed to disk anywhere but the
# operator-controlled input this test constructs; only its unlikely-to-clash
# suffix is used to search recorded calls and files, so no real key material
# is ever needed.
FAKE_IDENTITY="AGE-SECRET-KEY-1QVYW6TESTSYNTHETICFAKEDONOTUSE0000000000000000000000"
IDENTITY_FILE="${WORKDIR}/identity.txt"
printf '%s\n' "$FAKE_IDENTITY" > "$IDENTITY_FILE"

reset_state() {
  : > "$CALL_LOG"
  rm -f "$CURL_HEALTH_COUNT_FILE" "$PG_RESTORE_STDIN_CAPTURE"
  unset FAKE_LIVE_HISTORY FAKE_DRILL_HISTORY FAKE_CANARY_COUNT \
    FAKE_LIVE_COUNTS FAKE_DRILL_COUNTS FAKE_PG_RESTORE_FAIL \
    FAKE_HEALTH_SUCCESS_AT LEDGER_HEALTH_TIMEOUT_SECONDS
}

run_restore() {
  PATH="${STUB_BIN}:${PATH}" \
    LEDGER_DEPLOY_ROOT="$WORKDIR" \
    LEDGER_BACKUP_ROOT="$BACKUP_ROOT" \
    LEDGER_RELEASE_SQL_DIR="$RELEASE_SQL_DIR" \
    LEDGER_OPS_URL="http://127.0.0.1:5081" \
    LEDGER_TEXTFILE_DIR="${WORKDIR}/textfile" \
    LEDGER_HEALTH_TIMEOUT_SECONDS="${LEDGER_HEALTH_TIMEOUT_SECONDS:-9}" \
    CALL_LOG="$CALL_LOG" \
    CURL_HEALTH_COUNT_FILE="$CURL_HEALTH_COUNT_FILE" \
    PG_RESTORE_STDIN_CAPTURE="$PG_RESTORE_STDIN_CAPTURE" \
    FAKE_LIVE_HISTORY="${FAKE_LIVE_HISTORY:-}" \
    FAKE_DRILL_HISTORY="${FAKE_DRILL_HISTORY:-}" \
    FAKE_CANARY_COUNT="${FAKE_CANARY_COUNT:-1}" \
    FAKE_LIVE_COUNTS="${FAKE_LIVE_COUNTS:-same-counts}" \
    FAKE_DRILL_COUNTS="${FAKE_DRILL_COUNTS:-same-counts}" \
    FAKE_PG_RESTORE_FAIL="${FAKE_PG_RESTORE_FAIL:-0}" \
    FAKE_HEALTH_SUCCESS_AT="${FAKE_HEALTH_SUCCESS_AT:-1}" \
    "$RESTORE_BIN" "$@"
}

# --- argument handling -----------------------------------------------------

reset_state
STATUS=0
run_restore < /dev/null > /dev/null 2>&1 || STATUS=$?
check "no arguments at all is refused" "1" "$STATUS"

reset_state
STATUS=0
run_restore --bogus < /dev/null > /dev/null 2>&1 || STATUS=$?
check "an unknown argument is refused" "1" "$STATUS"

reset_state
STATUS=0
run_restore --drill --backup "$GOOD_BACKUP" < /dev/null > /dev/null 2>&1 || STATUS=$?
check "a missing --identity is refused" "1" "$STATUS"

reset_state
STATUS=0
ERR="$(run_restore --drill --backup "$OUTSIDE_BACKUP" --identity "$IDENTITY_FILE" < /dev/null 2>&1 >/dev/null)" || STATUS=$?
check "a backup path outside the backup root is refused" "1" "$STATUS"
check "the refusal names the backup root, not a stub failure" "1" \
  "$([[ "$ERR" == *"must live directly under"* ]] && echo 1 || echo 0)"
check "no stub command runs for a rejected out-of-root backup" "" "$(cat "$CALL_LOG")"

# --- drill mode: PASS when stubbed live and drill answers match ------------

reset_state
FAKE_LIVE_HISTORY="20260101120000_Init" FAKE_DRILL_HISTORY="20260101120000_Init" \
  FAKE_CANARY_COUNT=1 FAKE_LIVE_COUNTS="public|t|5" FAKE_DRILL_COUNTS="public|t|5" \
  STATUS=0
run_restore --drill --backup "$GOOD_BACKUP" --identity "$IDENTITY_FILE" \
  < /dev/null > "${WORKDIR}/drill-pass.log" 2>&1 || STATUS=$?
check "a matching drill restore exits 0" "0" "$STATUS"
check "a matching drill restore reports PASS" "1" \
  "$(grep -c 'PASS: restore drill' "${WORKDIR}/drill-pass.log" || true)"
check "the scratch database is dropped as part of a passing drill" "1" \
  "$([ "$(grep -c "DROP DATABASE IF EXISTS ${SCRATCH_DB_NAME}" "$CALL_LOG")" -ge 2 ] && echo 1 || echo 0)"

# --- drill mode: FAIL when the migration history differs and is not a prefix

reset_state
FAKE_LIVE_HISTORY="20260101120000_Init,20260201120000_Later" FAKE_DRILL_HISTORY="20260301120000_Diverged" \
  STATUS=0
run_restore --drill --backup "$GOOD_BACKUP" --identity "$IDENTITY_FILE" \
  < /dev/null > "${WORKDIR}/drill-fail-history.log" 2>&1 || STATUS=$?
check "a diverging migration history exits non-zero" "1" "$([ "$STATUS" -ne 0 ] && echo 1 || echo 0)"
check "a diverging migration history reports FAIL" "1" \
  "$(grep -c 'FAIL: migration history' "${WORKDIR}/drill-fail-history.log" || true)"
check "the scratch database is still dropped after a comparison FAIL" "1" \
  "$([ "$(grep -c "DROP DATABASE IF EXISTS ${SCRATCH_DB_NAME}" "$CALL_LOG")" -ge 2 ] && echo 1 || echo 0)"

# --- drill mode: FAIL when the canary row count is wrong --------------------

reset_state
FAKE_LIVE_HISTORY="x" FAKE_DRILL_HISTORY="x" FAKE_CANARY_COUNT=0 \
  STATUS=0
run_restore --drill --backup "$GOOD_BACKUP" --identity "$IDENTITY_FILE" \
  < /dev/null > "${WORKDIR}/drill-fail-canary.log" 2>&1 || STATUS=$?
check "a wrong canary row count exits non-zero" "1" "$([ "$STATUS" -ne 0 ] && echo 1 || echo 0)"
check "a wrong canary row count reports FAIL" "1" \
  "$(grep -c 'FAIL: data_protection_canary' "${WORKDIR}/drill-fail-canary.log" || true)"
check "the scratch database is still dropped after a canary FAIL" "1" \
  "$([ "$(grep -c "DROP DATABASE IF EXISTS ${SCRATCH_DB_NAME}" "$CALL_LOG")" -ge 2 ] && echo 1 || echo 0)"

# --- drill mode: FAIL when table counts differ -----------------------------

reset_state
FAKE_LIVE_HISTORY="x" FAKE_DRILL_HISTORY="x" FAKE_CANARY_COUNT=1 \
  FAKE_LIVE_COUNTS="public|t|500" FAKE_DRILL_COUNTS="public|t|3" \
  STATUS=0
run_restore --drill --backup "$GOOD_BACKUP" --identity "$IDENTITY_FILE" \
  < /dev/null > "${WORKDIR}/drill-fail-counts.log" 2>&1 || STATUS=$?
check "differing table counts exit non-zero" "1" "$([ "$STATUS" -ne 0 ] && echo 1 || echo 0)"
check "differing table counts report FAIL" "1" \
  "$(grep -c 'FAIL: per-schema table counts' "${WORKDIR}/drill-fail-counts.log" || true)"
check "the scratch database is still dropped after a table-count FAIL" "1" \
  "$([ "$(grep -c "DROP DATABASE IF EXISTS ${SCRATCH_DB_NAME}" "$CALL_LOG")" -ge 2 ] && echo 1 || echo 0)"

# --- drill mode: FAIL when pg_restore itself errors, scratch DB still dropped

reset_state
FAKE_PG_RESTORE_FAIL=1 STATUS=0
run_restore --drill --backup "$GOOD_BACKUP" --identity "$IDENTITY_FILE" \
  < /dev/null > "${WORKDIR}/drill-fail-restore.log" 2>&1 || STATUS=$?
check "a pg_restore failure exits non-zero" "1" "$([ "$STATUS" -ne 0 ] && echo 1 || echo 0)"
check "a pg_restore failure reports FAIL" "1" \
  "$(grep -c 'FAIL: pg_restore' "${WORKDIR}/drill-fail-restore.log" || true)"
check "the scratch database is still dropped after a pg_restore FAIL" "1" \
  "$([ "$(grep -c "DROP DATABASE IF EXISTS ${SCRATCH_DB_NAME}" "$CALL_LOG")" -ge 2 ] && echo 1 || echo 0)"

# --- identity handling: piped via /dev/stdin, never on argv or disk --------

reset_state
FAKE_LIVE_HISTORY="x" FAKE_DRILL_HISTORY="x" STATUS=0
run_restore --drill --backup "$GOOD_BACKUP" --identity /dev/stdin \
  < "$IDENTITY_FILE" > "${WORKDIR}/drill-stdin-identity.log" 2>&1 || STATUS=$?
check "a drill restore via a stdin identity still succeeds" "0" "$STATUS"
check "the identity never appears in any recorded command's argv" "0" \
  "$(grep -c 'TESTSYNTHETICFAKE' "$CALL_LOG" || true)"
check "pg_restore receives only the stubbed plaintext, not the identity" "0" \
  "$(grep -c 'TESTSYNTHETICFAKE' "$PG_RESTORE_STDIN_CAPTURE" || true)"
check "pg_restore's stdin is the piped-through decrypted content" "1" \
  "$(grep -c 'FAKE-PLAINTEXT-DUMP-CONTENT' "$PG_RESTORE_STDIN_CAPTURE" || true)"
check "no file under the work directory other than the operator's own input holds the identity" "" \
  "$(grep -rlF "$FAKE_IDENTITY" "$WORKDIR" 2>/dev/null | grep -v -F "$IDENTITY_FILE" || true)"

# --- live mode: refuses without the typed confirmation ---------------------

reset_state
STATUS=0
printf 'not-ledger\n' | run_restore --live --backup "$GOOD_BACKUP" --identity "$IDENTITY_FILE" \
  > "${WORKDIR}/live-refused.log" 2>&1 || STATUS=$?
check "live restore without the typed confirmation exits non-zero" "1" "$([ "$STATUS" -ne 0 ] && echo 1 || echo 0)"
check "live restore without confirmation never stops the service" "" \
  "$(grep 'systemctl stop' "$CALL_LOG" || true)"
check "live restore without confirmation never renames the database" "" \
  "$(grep 'RENAME TO' "$CALL_LOG" || true)"

# --- live mode: with confirmation, keeps the old DB and restores as migrator

reset_state
FAKE_HEALTH_SUCCESS_AT=2 STATUS=0
printf 'ledger\n' | run_restore --live --backup "$GOOD_BACKUP" --identity "$IDENTITY_FILE" \
  > "${WORKDIR}/live-confirmed.log" 2>&1 || STATUS=$?
check "a confirmed live restore that becomes healthy exits 0" "0" "$STATUS"
check "the previous database is renamed, not dropped" "1" \
  "$(grep -c '^runuser.*RENAME TO ledger_pre_restore_[0-9]\{8\}T[0-9]\{6\}Z' "$CALL_LOG" || true)"
check "the live database is never dropped" "" \
  "$(grep -i 'DROP DATABASE.*[^_]ledger\b' "$CALL_LOG" || true)"
check "the database is recreated via the release bootstrap sql" "1" \
  "$(grep -c -- "^runuser.*-f ${RELEASE_SQL_DIR}/bootstrap-database.sql" "$CALL_LOG" || true)"
check "pg_restore runs under the ledger_migrator role against the live database" "1" \
  "$(grep -c -- '^runuser.*--role=ledger_migrator --exit-on-error --dbname=ledger' "$CALL_LOG" || true)"
check "the service is stopped before, and started after, the restore" "1" \
  "$(grep -q 'systemctl stop ledger.service' "$CALL_LOG" && grep -q 'systemctl start ledger.service' "$CALL_LOG" && echo 1 || echo 0)"
check "a confirmed, healthy live restore reports PASS" "1" \
  "$(grep -c 'PASS: live restore' "${WORKDIR}/live-confirmed.log" || true)"

# --- live mode: waits for the literal Healthy response, fails otherwise ----

reset_state
LEDGER_HEALTH_TIMEOUT_SECONDS=6 FAKE_HEALTH_SUCCESS_AT=never STATUS=0
printf 'ledger\n' | run_restore --live --backup "$GOOD_BACKUP" --identity "$IDENTITY_FILE" \
  > "${WORKDIR}/live-unhealthy.log" 2>&1 || STATUS=$?
check "a live restore that never reports Healthy exits non-zero" "1" "$([ "$STATUS" -ne 0 ] && echo 1 || echo 0)"
check "a live restore that never reports Healthy reports FAIL" "1" \
  "$(grep -c 'FAIL: the app did not report healthy' "${WORKDIR}/live-unhealthy.log" || true)"
check "an unhealthy live restore still keeps the previous database" "1" \
  "$(grep -c '^runuser.*RENAME TO ledger_pre_restore_[0-9]\{8\}T[0-9]\{6\}Z' "$CALL_LOG" || true)"

echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "All checks passed."
  exit 0
else
  echo "${FAILURES} check(s) failed."
  exit 1
fi
