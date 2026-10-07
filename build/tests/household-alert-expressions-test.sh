#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RULES_FILE="$REPO_ROOT/deploy/provisioning/grafana/provisioning/alerting/household-rules.yaml"

if [ -z "${LINT_COMPOSE:-}" ]; then
  LINT_COMPOSE="docker compose -f $REPO_ROOT/build/lint/compose.yaml run --rm --no-deps -T"
fi

WORK_DIR="$(mktemp -d)"
# shellcheck disable=SC2329
cleanup() {
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT

GENERATOR="$WORK_DIR/generate.py"

# Reads the Grafana rule file and writes a Prometheus rule file plus a promtool
# test file. Each Grafana rule's query expression and its threshold comparison
# (evaluator type and value of the threshold node) are carried over verbatim,
# so changing either in the Grafana file changes what is evaluated here.
cat >"$GENERATOR" <<'PYTHON'
import re
import sys

rules_path, out_dir = sys.argv[1], sys.argv[2]
text = open(rules_path, encoding="utf-8").read()
blocks = re.split(r"(?m)^      - uid: ", text)[1:]
operators = {"gt": ">", "lt": "<", "gte": ">=", "lte": "<="}

rules = {}
for block in blocks:
    uid = block.split("\n", 1)[0].strip()
    expr = re.search(r"(?m)^\s+expr: (.+)$", block).group(1).strip()
    evaluator_type = re.search(r"(?m)^\s+type: (gt|lt|gte|lte)$", block).group(1)
    threshold = re.search(r"(?m)^\s+params: \[([0-9.]+)\]$", block).group(1)
    rules[uid] = f"({expr}) {operators[evaluator_type]} {threshold}"

with open(f"{out_dir}/rules.yml", "w", encoding="utf-8") as handle:
    handle.write("groups:\n  - name: household\n    rules:\n")
    for uid, expression in rules.items():
        handle.write(f"      - alert: {uid}\n        expr: {expression}\n")

FAILING = "ledger_sync_failing"
DAYS = "ledger_bank_consent_days_until_expiry"
STATE = "ledger_bank_consent_state"
SUCCESS = "ledger_sync_last_success_timestamp_seconds"
CALLS = "ledger_sync_calls_remaining"
DRIFT = "ledger_balance_reconciliation_drift"

failing = "ledger-bank-sync-failing"
limited = "ledger-bank-sync-rate-limited"
rejected = "ledger-bank-consent-rejected"
stale = "ledger-bank-sync-stale"
band14 = "ledger-bank-consent-expiring-14d"
band7 = "ledger-bank-consent-expiring-7d"
expired = "ledger-bank-consent-expired"
drift = "ledger-balance-not-reconciled"

assert set(rules) == {failing, limited, rejected, stale, band14, band7, expired, drift}, sorted(rules)


def flat(value, length=40):
    return f"{value}+0x{length}"


def reason(name, value, connection="c1"):
    return (f'{FAILING}{{connection="{connection}",reason="{name}"}}', flat(value))


def days(value, connection="c1"):
    return (f'{DAYS}{{connection="{connection}"}}', flat(value))


def state(name, value, connection="c1"):
    return (f'{STATE}{{connection="{connection}",state="{name}"}}', flat(value))


def success(value, account="a1", length=40):
    return (f'{SUCCESS}{{account="{account}"}}', flat(value, length))


def calls(value, account="a1", length=40):
    return (f'{CALLS}{{account="{account}"}}', flat(value, length))


def drift_series(value, account="a1"):
    return (f'{DRIFT}{{account="{account}"}}', flat(value))


MINUTE = 60
STALE_LENGTH = 2300
cases = [
    ("transient failure fires only the failing rule", "30m", [reason("transient", 1)], {failing}),
    ("transient reason at zero does not fire", "30m", [reason("transient", 0), reason("rate_limited", 0)], set()),
    ("rate limited fires only the rate limited rule", "30m", [reason("rate_limited", 1)], {limited}),
    ("consent rejected fires only the rejected rule", "30m", [reason("consent_rejected", 1)], {rejected}),
    ("provider auth also fires the rejected rule", "30m", [reason("provider_auth", 1)], {rejected}),
    ("one failing connection among healthy ones fires", "30m", [reason("transient", 0, "c1"), reason("transient", 1, "c2")], {failing}),
    ("consent far away fires nothing", "30m", [days(30)], set()),
    ("consent at exactly 14 days is not yet in the band", "30m", [days(14.0)], set()),
    ("consent at 13.99 days is in the 14 day band only", "30m", [days(13.99)], {band14}),
    ("consent at exactly 7 days is still the 14 day band only", "30m", [days(7.0)], {band14}),
    ("consent at 6.99 days is in the 7 day band only", "30m", [days(6.99)], {band7}),
    ("consent at 0.5 days is in the 7 day band only", "30m", [days(0.5)], {band7}),
    ("consent at zero days is in neither band", "30m", [days(0)], set()),
    ("consent already past is in neither band", "30m", [days(-3)], set()),
    ("the soonest consent decides the band", "30m", [days(30, "c1"), days(10, "c2")], {band14}),
    ("the soonest consent decides the nearer band", "30m", [days(30, "c1"), days(6.99, "c2")], {band7}),
    ("expired consent state fires the expired rule", "30m", [state("expired", 1), state("linked", 0)], {expired}),
    ("linked consent state does not fire the expired rule", "30m", [state("expired", 0), state("linked", 1)], set()),
    ("balance drift at zero does not fire", "30m", [drift_series(0)], set()),
    ("balance drift at one fires", "30m", [drift_series(1)], {drift}),
    ("sync 35h59m ago is not stale", "2159m", [success(0, length=STALE_LENGTH), calls(5, length=STALE_LENGTH)], set()),
    ("sync 36h01m ago is stale", "2161m", [success(0, length=STALE_LENGTH), calls(5, length=STALE_LENGTH)], {stale}),
    ("sync exactly 36h ago is not yet stale", "2160m", [success(0, length=STALE_LENGTH)], set()),
    ("account that never succeeded is stale", "30m", [calls(5)], {stale}),
    ("account with a fresh success does not get the never succeeded fallback", "30m", [success(1500), calls(5)], set()),
    ("a never succeeded account is stale next to a healthy one", "30m", [success(1500, "a1"), calls(5, "a1"), calls(5, "a2")], {stale}),
    ("a stale account is stale next to a healthy one", "2161m", [success(0, "a1", STALE_LENGTH), success(129000, "a2", STALE_LENGTH), calls(5, "a1", STALE_LENGTH), calls(5, "a2", STALE_LENGTH)], {stale}),
    ("no series at all fires nothing", "30m", [(f'{CALLS}{{account="other"}}', "_x0")], set()),
]

with open(f"{out_dir}/tests.yml", "w", encoding="utf-8") as handle:
    handle.write("rule_files:\n  - rules.yml\n\nevaluation_interval: 1m\n\ntests:\n")
    for name, eval_time, series, firing in cases:
        handle.write(f"  - name: {name}\n    interval: 1m\n    input_series:\n")
        for selector, values in series:
            handle.write(f"      - series: '{selector}'\n        values: '{values}'\n")
        handle.write("    alert_rule_test:\n")
        for uid in sorted(rules):
            handle.write(f"      - eval_time: {eval_time}\n        alertname: {uid}\n")
            if uid in firing:
                handle.write("        exp_alerts:\n          - exp_labels: {}\n")
            else:
                handle.write("        exp_alerts: []\n")
PYTHON

run_suite() {
  local rules_file="$1" dir
  dir="$(mktemp -d "$WORK_DIR/suite.XXXXXX")"
  python3 "$GENERATOR" "$rules_file" "$dir"
  chmod -R a+rX "$dir"
  # shellcheck disable=SC2086
  $LINT_COMPOSE -v "$dir:/repo:ro" promtool test rules tests.yml
}

echo "Evaluating the household alert expressions and thresholds with promtool"
if ! run_suite "$RULES_FILE"; then
  echo "household alert expressions did not behave as expected on the synthetic series" >&2
  exit 1
fi

mutate_and_expect_failure() {
  local description="$1" original="$2" replacement="$3"
  local scratch="$WORK_DIR/mutated.yaml"
  python3 - "$RULES_FILE" "$scratch" "$original" "$replacement" <<'PYTHON'
import sys

source, target, original, replacement = sys.argv[1:5]
text = open(source, encoding="utf-8").read()
if text.count(original) < 1:
    sys.exit(f"mutation target not found: {original}")
open(target, "w", encoding="utf-8").write(text.replace(original, replacement, 1))
PYTHON
  if run_suite "$scratch" >"$WORK_DIR/mutation.log" 2>&1; then
    echo "self-test failed: the suite still passed after $description" >&2
    return 1
  fi
  echo "mutation detected: $description"
}

echo "Proving the suite fails when a rule is changed to something wrong"
mutation_status=0
mutate_and_expect_failure "widening the stale threshold to 48 hours" "params: [129600]" "params: [172800]" || mutation_status=1
mutate_and_expect_failure "tightening the stale threshold to 24 hours" "params: [129600]" "params: [86400]" || mutation_status=1
mutate_and_expect_failure "making the lower 14 day bound exclusive" ">= bool 7" "> bool 7" || mutation_status=1
mutate_and_expect_failure "moving the 14 day upper bound to 15" "< bool 14" "< bool 15" || mutation_status=1
mutate_and_expect_failure "making the 7 day lower bound inclusive of zero" "> bool 0" ">= bool 0" || mutation_status=1
mutate_and_expect_failure "pointing the failing rule at the wrong reason" 'reason="transient"' 'reason="rate_limited"' || mutation_status=1
mutate_and_expect_failure "dropping provider_auth from the rejected rule" "consent_rejected|provider_auth" "consent_rejected" || mutation_status=1
mutate_and_expect_failure "removing the never-succeeded fallback" "or (ledger_sync_calls_remaining * 0 + 1000000000)" "or (ledger_sync_calls_remaining * 0)" || mutation_status=1
mutate_and_expect_failure "raising the drift threshold to 1" "max(ledger_balance_reconciliation_drift)" "max(ledger_balance_reconciliation_drift) - 1" || mutation_status=1
mutate_and_expect_failure "querying a different consent state" 'state="expired"' 'state="linked"' || mutation_status=1
exit "$mutation_status"
