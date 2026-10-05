#!/usr/bin/env bash
# Proves the pure decision functions behind encrypted backups (recipient
# validation, filename shaping, grandfather-father-son retention selection)
# and the ledger-backup script's own success/failure metrics behavior. Stubs
# pg_dump and age on PATH so the full pipeline can be exercised without a
# database, a real age binary or root.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

# shellcheck source=deploy/lib/common.sh
source "${REPO_ROOT}/deploy/lib/common.sh"
# shellcheck source=deploy/lib/backup.sh
source "${REPO_ROOT}/deploy/lib/backup.sh"

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
host_guard_install "$WORKDIR"

# --- ledger_backup_validate_recipients -----------------------------------

RECIPIENTS_OK="${WORKDIR}/recipients-ok.txt"
printf '\nage1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p\n\n' > "$RECIPIENTS_OK"
(ledger_backup_validate_recipients "$RECIPIENTS_OK" >/dev/null 2>&1)
check "a file with one age1 key and blank lines validates" "0" "$?"

RECIPIENTS_EMPTY="${WORKDIR}/recipients-empty.txt"
: > "$RECIPIENTS_EMPTY"
STATUS=0
(ledger_backup_validate_recipients "$RECIPIENTS_EMPTY" >/dev/null 2>&1) || STATUS=$?
check "an empty recipients file is refused" "1" "$STATUS"

RECIPIENTS_JUNK="${WORKDIR}/recipients-junk.txt"
printf 'age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p\nnot-a-key\n' > "$RECIPIENTS_JUNK"
STATUS=0
(ledger_backup_validate_recipients "$RECIPIENTS_JUNK" >/dev/null 2>&1) || STATUS=$?
check "a recipients file with a non-key line is refused" "1" "$STATUS"

RECIPIENTS_IDENTITY="${WORKDIR}/recipients-identity.txt"
printf 'AGE-SECRET-KEY-1QGKR9RCVQXFF2GVQ2G8XSA9GYQ2Z8Y3M0DGKM2XR2K0R2K0R2KQ2KR9RC\n' > "$RECIPIENTS_IDENTITY"
STATUS=0
(ledger_backup_validate_recipients "$RECIPIENTS_IDENTITY" >/dev/null 2>&1) || STATUS=$?
check "a recipients file containing an identity is refused" "1" "$STATUS"

RECIPIENTS_PREFIXED_IDENTITY="${WORKDIR}/recipients-prefixed-identity.txt"
printf ' AGE-SECRET-KEY-1QGKR9RCVQXFF2GVQ2G8XSA9GYQ2Z8Y3M0DGKM2XR2K0R2K0R2KQ2KR9RC\n' > "$RECIPIENTS_PREFIXED_IDENTITY"
STATUS=0
IDENTITY_ERROR="$( (ledger_backup_validate_recipients "$RECIPIENTS_PREFIXED_IDENTITY" 2>&1 >/dev/null) || echo "exit=$?")"
check "an identity behind a stray leading character is refused" "1" \
  "$([[ "$IDENTITY_ERROR" == *"exit=1"* ]] && echo 1 || echo 0)"
check "the refusal never echoes the identity" "0" \
  "$([[ "$IDENTITY_ERROR" == *QGKR9RCVQXFF2GVQ2G8X* ]] && echo 1 || echo 0)"

JUNK_ERROR="$( (ledger_backup_validate_recipients "$RECIPIENTS_JUNK" 2>&1 >/dev/null) || true)"
check "a rejected line is named by number, not echoed" "0" \
  "$([[ "$JUNK_ERROR" == *not-a-key* ]] && echo 1 || echo 0)"

# --- ledger_backup_filename -----------------------------------------------

check "ledger_backup_filename nightly 1767225600" \
  "ledger-20260101T000000Z-nightly.dump.age" \
  "$(ledger_backup_filename nightly 1767225600)"

check "ledger_backup_filename pre-migration 1767225600" \
  "ledger-20260101T000000Z-pre-migration.dump.age" \
  "$(ledger_backup_filename pre-migration 1767225600)"

# --- ledger_backup_select_deletions (GFS retention) -----------------------

REFERENCE_EPOCH=1767225600

INPUT_FILE="${WORKDIR}/names.txt"
: > "$INPUT_FILE"

for i in $(seq 0 399); do
  epoch=$((REFERENCE_EPOCH - i * 86400 + 43200))
  ledger_backup_filename nightly "$epoch" >> "$INPUT_FILE"
done

# Three pre-migration backups, all older than the entire 400-day nightly
# range, so none of them can ever contend with a nightly backup for the same
# day/week/month slot. The newest of the three (450 days back) must be kept
# by the dedicated pre-migration rule; the other two (500, 550 days back)
# must be pruned like any other name outside the kept set.
NEWEST_PREMIGRATION="$(ledger_backup_filename pre-migration $((REFERENCE_EPOCH - 450 * 86400 + 21600)))"
MID_PREMIGRATION="$(ledger_backup_filename pre-migration $((REFERENCE_EPOCH - 500 * 86400 + 21600)))"
OLD_PREMIGRATION="$(ledger_backup_filename pre-migration $((REFERENCE_EPOCH - 550 * 86400 + 21600)))"
printf '%s\n' "$NEWEST_PREMIGRATION" "$MID_PREMIGRATION" "$OLD_PREMIGRATION" >> "$INPUT_FILE"

# A name that does not match the backup pattern at all must never be
# reported for deletion.
printf 'notes.txt\n' >> "$INPUT_FILE"

OUTPUT_FILE="${WORKDIR}/deletions.txt"
ledger_backup_select_deletions < "$INPUT_FILE" > "$OUTPUT_FILE"

TOTAL_BACKUP_NAMES=403
DELETED_COUNT="$(wc -l < "$OUTPUT_FILE" | tr -d ' ')"
KEPT_COUNT=$((TOTAL_BACKUP_NAMES - DELETED_COUNT))
check "retention keeps exactly 7 daily + 4 weekly + 12 monthly + 1 pre-migration names" "24" "$KEPT_COUNT"

check "notes.txt never appears in the deletion list" "" "$(grep -F 'notes.txt' "$OUTPUT_FILE" || true)"

check "the newest pre-migration backup is kept" "" "$(grep -F "$NEWEST_PREMIGRATION" "$OUTPUT_FILE" || true)"

check "an older pre-migration backup is deleted" "1" \
  "$(grep -c -F "$MID_PREMIGRATION" "$OUTPUT_FILE" || true)"

check "the oldest pre-migration backup is deleted" "1" \
  "$(grep -c -F "$OLD_PREMIGRATION" "$OUTPUT_FILE" || true)"

NEWEST_NIGHTLY="$(ledger_backup_filename nightly $((REFERENCE_EPOCH + 43200)))"
check "the single newest nightly backup is kept" "" "$(grep -F "$NEWEST_NIGHTLY" "$OUTPUT_FILE" || true)"

SHUFFLED_FILE="${WORKDIR}/names-shuffled.txt"
if command -v shuf >/dev/null 2>&1; then
  shuf "$INPUT_FILE" > "$SHUFFLED_FILE"
else
  tac "$INPUT_FILE" > "$SHUFFLED_FILE"
fi
SHUFFLED_OUTPUT="${WORKDIR}/deletions-shuffled.txt"
ledger_backup_select_deletions < "$SHUFFLED_FILE" > "$SHUFFLED_OUTPUT"
check "the deletion set is identical regardless of input order" \
  "$(sort "$OUTPUT_FILE")" \
  "$(sort "$SHUFFLED_OUTPUT")"

# --- ledger-backup end-to-end pipeline, with stubbed pg_dump and age ------

STUB_BIN="${WORKDIR}/stub-bin"
mkdir -p "$STUB_BIN"

cat > "${STUB_BIN}/pg_dump" <<'EOF_STUB'
#!/usr/bin/env bash
printf 'FAKE-DATABASE-DUMP-CONTENT\n'
EOF_STUB
chmod +x "${STUB_BIN}/pg_dump"

# Like the real age, the stub reads its whole input before it exits; a stub
# that exits first can close the pipe before pg_dump writes, which kills
# pg_dump with SIGPIPE and makes the success case fail at random.
cat > "${STUB_BIN}/age" <<'EOF_STUB'
#!/usr/bin/env bash
cat > /dev/null
if [ "${FAKE_AGE_FAIL:-0}" = "1" ]; then
  exit 1
fi
printf 'age-encryption.org/v1\nFAKE-CIPHERTEXT-STANZA\n'
EOF_STUB
chmod +x "${STUB_BIN}/age"

BACKUP_ROOT="${WORKDIR}/backups"
TEXTFILE_DIR="${WORKDIR}/textfile"
mkdir -p "$BACKUP_ROOT" "$TEXTFILE_DIR"

RECIPIENTS_FILE="${WORKDIR}/recipients.txt"
printf 'age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p\n' > "$RECIPIENTS_FILE"

run_ledger_backup() {
  PATH="${STUB_BIN}:${PATH}" \
    LEDGER_BACKUP_ROOT="$BACKUP_ROOT" \
    LEDGER_BACKUP_RECIPIENTS="$RECIPIENTS_FILE" \
    LEDGER_TEXTFILE_DIR="$TEXTFILE_DIR" \
    "${REPO_ROOT}/deploy/bin/ledger-backup" "$@"
}

STATUS=0
run_ledger_backup nightly || STATUS=$?
check "a successful backup run exits 0" "0" "$STATUS"

check "the encrypted backup file was written" "1" \
  "$(find "$BACKUP_ROOT" -maxdepth 1 -name 'ledger-*-nightly.dump.age' | wc -l | tr -d ' ')"

METRICS_FILE="${TEXTFILE_DIR}/ledger_backup_nightly.prom"
check "success metrics report last_run_success 1" "1" \
  "$(grep -oE 'ledger_backup_last_run_success\{reason="nightly"\} [0-9]+' "$METRICS_FILE" | awk '{print $2}')"

FIRST_SUCCESS_TS="$(grep -oE 'ledger_backup_last_success_timestamp_seconds\{reason="nightly"\} [0-9]+' "$METRICS_FILE" | awk '{print $2}')"
check "success metrics report a non-zero size" "1" \
  "$([ "$(grep -oE 'ledger_backup_last_size_bytes\{reason="nightly"\} [0-9]+' "$METRICS_FILE" | awk '{print $2}')" -gt 0 ] && echo 1 || echo 0)"

STATUS=0
FAKE_AGE_FAIL=1 run_ledger_backup nightly || STATUS=$?
check "a failed backup run exits non-zero" "1" "$([ "$STATUS" -ne 0 ] && echo 1 || echo 0)"

check "failed run reports last_run_success 0" "0" \
  "$(grep -oE 'ledger_backup_last_run_success\{reason="nightly"\} [0-9]+' "$METRICS_FILE" | awk '{print $2}')"

check "failed run keeps the prior success timestamp" "$FIRST_SUCCESS_TS" \
  "$(grep -oE 'ledger_backup_last_success_timestamp_seconds\{reason="nightly"\} [0-9]+' "$METRICS_FILE" | awk '{print $2}')"

check "a failed run leaves no extra backup file behind" "1" \
  "$(find "$BACKUP_ROOT" -maxdepth 1 -name 'ledger-*-nightly.dump.age' | wc -l | tr -d ' ')"

echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "All checks passed."
  exit 0
else
  echo "${FAILURES} check(s) failed."
  exit 1
fi
