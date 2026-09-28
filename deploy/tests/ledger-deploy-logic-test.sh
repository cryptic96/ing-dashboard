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

# shellcheck source=deploy/lib/common.sh
source "${REPO_ROOT}/deploy/lib/common.sh"
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
  # Stand in for the real runuser: writes an applied-migrations list that
  # is NOT a subset of the target release's manifest, without needing root
  # or a real database.
  printf 'a\nb\n'
}

ROLLBACK_EXIT=0
# Run in a subshell: ledger_die() calls exit, and this call is expected to
# hit that path, which must only end the subshell, not this test script.
( ledger_rollback_release "1.0.0" "${ROLLBACK_ROOT}/releases" "${ROLLBACK_ROOT}/current" "${ROLLBACK_ROOT}/state" \
  "http://127.0.0.1:0" "1" >/dev/null 2>&1 ) || ROLLBACK_EXIT=$?
check "rollback refuses when applied migrations are not a subset of the target manifest" "1" \
  "$([ "$ROLLBACK_EXIT" -ne 0 ] && echo 1 || echo 0)"

unset -f runuser

# --- Deploy textfile metrics content ---------------------------------------

METRICS_ROOT="$(mktemp -d)"
export LEDGER_TEXTFILE_DIR="${METRICS_ROOT}"

ledger_write_deploy_poll_metrics "1" "1700000000"
ledger_write_deploy_run_metrics "success" "1700000100" "1.2.3"

METRICS_FILE="${METRICS_ROOT}/ledger_deploy.prom"

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

if [ "$FAILURES" -ne 0 ]; then
  printf '%d check(s) failed\n' "$FAILURES" >&2
  exit 1
fi

printf 'All checks passed\n'
