#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RULES_FILE="$REPO_ROOT/deploy/provisioning/grafana/provisioning/alerting/access-rules.yaml"

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
    handle.write("groups:\n  - name: access\n    rules:\n")
    for uid, expression in rules.items():
        handle.write(f"      - alert: {uid}\n        expr: {expression}\n")

REJECTED = "ledger_mcp_rejected_tokens_total"
GRANTS = "ledger_oauth_grants_created_total"
REUSE = "ledger_oauth_refresh_token_reuse_total"

burst = "ledger-mcp-rejected-tokens-burst"
grant = "ledger-oauth-grant-created"
reuse = "ledger-oauth-refresh-token-reused"

assert set(rules) == {burst, grant, reuse}, sorted(rules)


def selector_of(name, label):
    return f'{name}{{reason="{label}"}}' if label else name


def counter(name, before, after, label=None):
    return (selector_of(name, label), f"{before}+0x4 {after}+0x6")


def steady(name, value, label=None):
    return (selector_of(name, label), f"{value}+0x24")


cases = [
    ("eleven rejections within ten minutes fire the burst rule", "10m", [counter(REJECTED, 0, 11, "invalid")], {burst}),
    ("ten rejections within ten minutes do not fire the burst rule", "10m", [counter(REJECTED, 0, 10, "invalid")], set()),
    ("rejections of different reasons are added together", "10m", [counter(REJECTED, 0, 6, "expired"), counter(REJECTED, 0, 5, "wrong_audience")], {burst}),
    ("a large count from long ago does not fire the burst rule", "20m", [steady(REJECTED, 40, "invalid")], set()),
    ("no rejections at zero does not fire the burst rule", "10m", [steady(REJECTED, 0, "invalid"), steady(REJECTED, 0, "expired"), steady(REJECTED, 0, "wrong_audience")], set()),
    ("one new grant fires only the grant rule", "10m", [counter(GRANTS, 0, 1)], {grant}),
    ("no new grant does not fire the grant rule", "10m", [steady(GRANTS, 0)], set()),
    ("grants from long ago do not fire the grant rule", "20m", [steady(GRANTS, 3)], set()),
    ("one refresh token reuse fires only the reuse rule", "10m", [counter(REUSE, 0, 1)], {reuse}),
    ("no refresh token reuse does not fire the reuse rule", "10m", [steady(REUSE, 0)], set()),
    ("a grant and a reuse fire their own rules and not the burst rule", "10m", [counter(GRANTS, 0, 2), counter(REUSE, 0, 1), counter(REJECTED, 0, 3, "invalid")], {grant, reuse}),
    ("no series at all fires nothing", "10m", [("up", "1+0x24")], set()),
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

echo "Evaluating the access alert expressions and thresholds with promtool"
if ! run_suite "$RULES_FILE"; then
  echo "access alert expressions did not behave as expected on the synthetic series" >&2
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
mutate_and_expect_failure "raising the burst threshold to 11" "params: [10]" "params: [11]" || mutation_status=1
mutate_and_expect_failure "lowering the burst threshold to 9" "params: [10]" "params: [9]" || mutation_status=1
mutate_and_expect_failure "raising the grant threshold to 1" "params: [0]" "params: [1]" || mutation_status=1
mutate_and_expect_failure "shortening the burst window to one minute" "ledger_mcp_rejected_tokens_total[10m]" "ledger_mcp_rejected_tokens_total[1m]" || mutation_status=1
mutate_and_expect_failure "pointing the grant rule at the reuse counter" "sum(increase(ledger_oauth_grants_created_total[10m]))" "sum(increase(ledger_oauth_refresh_token_reuse_total[10m]))" || mutation_status=1
mutate_and_expect_failure "pointing the reuse rule at the grant counter" "sum(increase(ledger_oauth_refresh_token_reuse_total[10m]))" "sum(increase(ledger_oauth_grants_created_total[10m]))" || mutation_status=1
mutate_and_expect_failure "dropping the sum over reasons from the burst rule" "sum(increase(ledger_mcp_rejected_tokens_total[10m]))" "increase(ledger_mcp_rejected_tokens_total{reason=\"invalid\"}[10m])" || mutation_status=1
exit "$mutation_status"
