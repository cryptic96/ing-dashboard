#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$REPO_ROOT"

GRAFANA_INI="$REPO_ROOT/deploy/provisioning/grafana/grafana.ini"
PROVISIONING_DIR="$REPO_ROOT/deploy/provisioning/grafana/provisioning"
PROMETHEUS_YML="$REPO_ROOT/deploy/provisioning/prometheus/prometheus.yml"

# Asserts that, within the named ini section only, key = expected appears.
# Section-scoping is what makes this useful: [auth] disable_login_form and
# [auth.anonymous] enabled are different sections with the same-shaped
# lines, and a key from one must never satisfy an assertion about the
# other.
assert_ini_key() {
  local file="$1" section="$2" key="$3" expected="$4"
  awk -v section="[${section}]" -v key="$key" -v expected="$expected" '
    $0 == section { infile = 1; next }
    /^\[/ { infile = 0 }
    infile {
      line = $0
      sub(/^[ \t]+/, "", line)
      sub(/[ \t]+$/, "", line)
      split(line, parts, /[ \t]*=[ \t]*/)
      if (parts[1] == key && parts[2] == expected) { found = 1 }
    }
    END { exit !found }
  ' "$file"
}

run_promtool_check() {
  local mount_dir="$1" relative_file="$2"
  # shellcheck disable=SC2086
  if $LINT_COMPOSE -v "$mount_dir:/repo:ro" promtool check config "$relative_file" >/dev/null 2>&1; then
    return 0
  fi
  return 1
}

self_test_ini_assertion() {
  local failed=0
  local tmp
  tmp="$(mktemp -d)"
  local fixture="$tmp/grafana.ini"
  cat >"$fixture" <<'EOF'
[section.a]
enabled = true

[section.b]
enabled = false

[section.c]
enabled = true
EOF

  if ! assert_ini_key "$fixture" "section.b" "enabled" "false"; then
    echo "self-test failed: assert_ini_key rejected a matching key in its own section" >&2
    failed=1
  fi

  if assert_ini_key "$fixture" "section.a" "enabled" "false"; then
    echo "self-test failed: assert_ini_key accepted a value that only matched a neighbouring section" >&2
    failed=1
  fi

  if assert_ini_key "$fixture" "section.c" "enabled" "false"; then
    echo "self-test failed: assert_ini_key leaked a match across a section boundary" >&2
    failed=1
  fi

  rm -rf "$tmp"
  return "$failed"
}

self_test_promtool() {
  local failed=0
  local tmp
  tmp="$(mktemp -d)"
  cat >"$tmp/good.yml" <<'EOF'
global:
  scrape_interval: 30s
scrape_configs:
  - job_name: selftest
    static_configs:
      - targets: ['127.0.0.1:9999']
EOF
  cat >"$tmp/bad.yml" <<'EOF'
global:
  scrape_interval: not-a-duration
scrape_configs:
  - job_name: selftest
    static_configs:
      - targets: ['127.0.0.1:9999']
EOF
  chmod -R a+rX "$tmp"

  if ! run_promtool_check "$tmp" "good.yml"; then
    echo "self-test failed: promtool rejected a valid configuration" >&2
    failed=1
  fi

  if run_promtool_check "$tmp" "bad.yml"; then
    echo "self-test failed: promtool accepted an invalid configuration" >&2
    failed=1
  fi

  rm -rf "$tmp"
  return "$failed"
}

if ! self_test_ini_assertion; then
  echo "FAIL: 60-observability ini-assertion self-test did not behave as expected" >&2
  exit 1
fi

if ! self_test_promtool; then
  echo "FAIL: 60-observability promtool self-test did not behave as expected" >&2
  exit 1
fi

status=0

echo "Running promtool check config on the real prometheus.yml"
if ! run_promtool_check "$REPO_ROOT" "deploy/provisioning/prometheus/prometheus.yml"; then
  echo "promtool rejected deploy/provisioning/prometheus/prometheus.yml" >&2
  status=1
fi

echo "Asserting grafana.ini hardening keys"
# section|key|expected
hardening_keys=(
  "server|enforce_domain|true"
  "log|mode|console"
  "analytics|reporting_enabled|false"
  "analytics|check_for_updates|false"
  "analytics|check_for_plugin_updates|false"
  "analytics|feedback_links_enabled|false"
  "news|news_feed_enabled|false"
  "security|disable_gravatar|true"
  "security|cookie_secure|true"
  "security|cookie_samesite|strict"
  "security|strict_transport_security|true"
  "security|content_security_policy|true"
  "security|x_content_type_options|true"
  "security|allow_embedding|false"
  "security|disable_brute_force_login_protection|false"
  "snapshots|enabled|false"
  "snapshots|external_enabled|false"
  "public_dashboards|enabled|false"
  "auth|disable_login_form|false"
  "auth.basic|enabled|true"
  "auth.anonymous|enabled|false"
  "users|allow_sign_up|false"
  "users|allow_org_create|false"
  "users|auto_assign_org|true"
  "users|auto_assign_org_role|Viewer"
  "users|viewers_can_edit|false"
  "users|editors_can_admin|false"
  "unified_alerting|enabled|true"
  "paths|provisioning|/etc/grafana/provisioning"
)

for entry in "${hardening_keys[@]}"; do
  IFS='|' read -r section key expected <<<"$entry"
  if ! assert_ini_key "$GRAFANA_INI" "$section" "$key" "$expected"; then
    echo "grafana.ini: [$section] $key is not $expected" >&2
    status=1
  fi
done

if [ ! -d "$PROVISIONING_DIR" ]; then
  echo "missing $PROVISIONING_DIR" >&2
  exit 1
fi

if [ ! -f "$PROMETHEUS_YML" ]; then
  echo "missing $PROMETHEUS_YML" >&2
  exit 1
fi

ALERTING_DIR="$PROVISIONING_DIR/alerting"
HOUSEHOLD_RULES="$ALERTING_DIR/household-rules.yaml"
SYNC_METRICS_SOURCE="$REPO_ROOT/Ledger.Service/Metrics/SyncMetrics.cs"
ACCESS_RULES="$ALERTING_DIR/access-rules.yaml"
MCP_METRICS_SOURCE="$REPO_ROOT/Ledger.Service/Mcp/McpMetrics.cs"

echo "Asserting no alert rule file contains a template marker"
for rule_file in "$ALERTING_DIR"/*.yaml; do
  if grep -qF '{{' "$rule_file"; then
    echo "$rule_file contains a template marker; alert text must not interpolate anything" >&2
    status=1
  fi
done

echo "Asserting every metric the household rules query exists in the application's metrics code"
household_metrics="$(grep -oE 'ledger_[a-z_]+' "$HOUSEHOLD_RULES" | sort -u || true)"
if [ -z "$household_metrics" ]; then
  echo "no ledger_ metric names found in $HOUSEHOLD_RULES" >&2
  status=1
fi
for metric in $household_metrics; do
  if ! grep -qF "\"$metric\"" "$SYNC_METRICS_SOURCE"; then
    echo "household rules query $metric, which $SYNC_METRICS_SOURCE does not define" >&2
    status=1
  fi
done

echo "Asserting every metric the access rules query exists in the application's metrics code"
access_metrics="$(grep -oE 'ledger_[a-z_]+' "$ACCESS_RULES" | sort -u || true)"
if [ -z "$access_metrics" ]; then
  echo "no ledger_ metric names found in $ACCESS_RULES" >&2
  status=1
fi
for metric in $access_metrics; do
  if ! grep -qF "\"$metric\"" "$MCP_METRICS_SOURCE"; then
    echo "access rules query $metric, which $MCP_METRICS_SOURCE does not define" >&2
    status=1
  fi
done

# Boot the real, digest-pinned Grafana image with the repository's own
# grafana.ini, with the repository provisioning tree mounted read-only at
# /etc/grafana/provisioning exactly where the installer puts it, so the
# dashboard provider path resolves as it does on the host. The repository is
# mounted as compose.yaml already does. GF_SERVER_ENFORCE_DOMAIN is
# relaxed only for this ephemeral, loopback-published container so curl can
# reach it by IP; the shipped grafana.ini itself still has enforce_domain
# true, which is what the assertions above already checked.
GRAFANA_CID=""
# shellcheck disable=SC2329
cleanup_grafana() {
  if [ -n "$GRAFANA_CID" ]; then
    docker stop "$GRAFANA_CID" >/dev/null 2>&1 || true
  fi
}
trap cleanup_grafana EXIT


# The inner pipeline is wrapped in printf rather than assigned directly:
# tr receives SIGPIPE once head has read enough bytes, which — under
# pipefail, as a bare assignment's own exit status — would trip set -e even
# though the pipeline produced exactly the bytes it was asked for.
ADMIN_PASSWORD="$(printf '%s' "$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 32)")"

echo "Booting the digest-pinned Grafana image with the repository's provisioning"
# shellcheck disable=SC2086
GRAFANA_CID="$($LINT_COMPOSE -d \
  -p 127.0.0.1:0:3000 \
  -e GF_SECURITY_ADMIN_PASSWORD="$ADMIN_PASSWORD" \
  -e GF_SERVER_ENFORCE_DOMAIN=false \
  -e LEDGER_ALERT_EMAIL=alerts@example.com \
  -e GF_PATHS_CONFIG=/repo/deploy/provisioning/grafana/grafana.ini \
  -v "$PROVISIONING_DIR:/etc/grafana/provisioning:ro" \
  -e GF_PATHS_PROVISIONING=/etc/grafana/provisioning \
  --entrypoint /run.sh \
  grafana)"

GRAFANA_PORT=""
for _ in $(seq 1 30); do
  GRAFANA_PORT="$(docker port "$GRAFANA_CID" 3000/tcp 2>/dev/null | head -n1 | cut -d: -f2 || true)"
  [ -n "$GRAFANA_PORT" ] && break
  sleep 1
done

if [ -z "$GRAFANA_PORT" ]; then
  echo "could not determine the published port for the Grafana container" >&2
  exit 1
fi

BASE_URL="http://127.0.0.1:${GRAFANA_PORT}"

healthy=0
for _ in $(seq 1 60); do
  code="$(curl -s -o /dev/null -w '%{http_code}' "$BASE_URL/api/health" 2>/dev/null || true)"
  if [ "$code" = "200" ]; then
    healthy=1
    break
  fi
  sleep 1
done

if [ "$healthy" -ne 1 ]; then
  echo "Grafana never reported healthy at $BASE_URL/api/health" >&2
  docker logs "$GRAFANA_CID" 2>&1 | tail -n 100 >&2 || true
  exit 1
fi

echo "Asserting anonymous requests get 401"
for path in /api/search /api/dashboards/home /api/snapshots; do
  code="$(curl -s -o /dev/null -w '%{http_code}' "$BASE_URL$path")"
  if [ "$code" != "401" ]; then
    echo "anonymous GET $path returned $code, expected 401" >&2
    status=1
  fi
done

echo "Asserting provisioned datasources, alert rules and contact point"
datasources_json="$(curl -s -u "admin:${ADMIN_PASSWORD}" "$BASE_URL/api/datasources")"
for uid in ledger-reporting prometheus; do
  if ! grep -q "\"uid\":\"${uid}\"" <<<"$(tr -d ' \n' <<<"$datasources_json")"; then
    echo "datasource uid $uid not found in /api/datasources" >&2
    status=1
  fi
done

# Grafana's PostgreSQL datasource reads its default database from jsonData only;
# without it every panel fails with "no default database configured", even
# though the datasource itself provisions and lists fine.
reporting_ds_json="$(curl -s -u "admin:${ADMIN_PASSWORD}" "$BASE_URL/api/datasources/uid/ledger-reporting")"
reporting_json_data="$(grep -oE '"jsonData":\{[^}]*\}' <<<"$(tr -d ' \n' <<<"$reporting_ds_json")" || true)"
if ! grep -q '"database":"ledger"' <<<"$reporting_json_data"; then
  echo "datasource ledger-reporting has no jsonData.database \"ledger\"; Grafana cannot run its queries" >&2
  status=1
fi

alert_rules_json="$(curl -s -u "admin:${ADMIN_PASSWORD}" "$BASE_URL/api/v1/provisioning/alert-rules")"
alert_rule_uids="$(grep -oE '"uid":"ledger-[a-z0-9-]+"' <<<"$(tr -d ' \n' <<<"$alert_rules_json")" | sort -u)"
alert_rule_count="$(grep -c . <<<"$alert_rule_uids" || true)"
if [ "$alert_rule_count" -ne 19 ]; then
  echo "expected 19 provisioned alert rule uids, found $alert_rule_count" >&2
  status=1
fi

household_uids=(
  ledger-bank-sync-failing
  ledger-bank-sync-rate-limited
  ledger-bank-consent-rejected
  ledger-bank-sync-stale
  ledger-bank-consent-expiring-14d
  ledger-bank-consent-expiring-7d
  ledger-bank-consent-expired
  ledger-balance-not-reconciled
)
for uid in "${household_uids[@]}"; do
  if ! grep -qF "\"uid\":\"${uid}\"" <<<"$alert_rule_uids"; then
    echo "household alert rule uid $uid is not provisioned" >&2
    status=1
  fi
done

access_uids=(
  ledger-mcp-rejected-tokens-burst
  ledger-oauth-grant-created
  ledger-oauth-refresh-token-reused
)
for uid in "${access_uids[@]}"; do
  if ! grep -qF "\"uid\":\"${uid}\"" <<<"$alert_rule_uids"; then
    echo "access alert rule uid $uid is not provisioned" >&2
    status=1
  fi
done

policies_json="$(tr -d ' \n' <<<"$(curl -s -u "admin:${ADMIN_PASSWORD}" "$BASE_URL/api/v1/provisioning/policies")")"
if ! grep -q '"grafana_folder","=","Household"' <<<"$policies_json" \
  || ! grep -q '"repeat_interval":"1d"' <<<"$policies_json" \
  || ! grep -q '"repeat_interval":"12h"' <<<"$policies_json"; then
  echo "notification policy does not hold the 12h root route and the daily Household child route" >&2
  status=1
fi

contact_points_json="$(curl -s -u "admin:${ADMIN_PASSWORD}" "$BASE_URL/api/v1/provisioning/contact-points")"
if ! grep -q "operator-email" <<<"$contact_points_json"; then
  echo "contact point operator-email not found in /api/v1/provisioning/contact-points" >&2
  status=1
fi

echo "Asserting provisioned dashboards"
dashboards_json="$(tr -d ' \n' <<<"$(curl -s -u "admin:${ADMIN_PASSWORD}" "$BASE_URL/api/search?type=dash-db")")"
for uid in ledger-sync-en ledger-sync-nl; do
  if ! grep -qE "\"uid\":\"${uid}\"[^}]*\"folderTitle\":\"HouseholdLedger\"|\"folderTitle\":\"HouseholdLedger\"[^}]*\"uid\":\"${uid}\"" <<<"$dashboards_json"; then
    echo "dashboard uid $uid is not listed in the Household Ledger folder" >&2
    status=1
  fi

  dashboard_json="$(tr -d ' \n' <<<"$(curl -s -u "admin:${ADMIN_PASSWORD}" "$BASE_URL/api/dashboards/uid/${uid}")")"
  if ! grep -q '"provisioned":true' <<<"$dashboard_json"; then
    echo "dashboard $uid is not reported as provisioned" >&2
    status=1
  fi
done

exit "$status"
