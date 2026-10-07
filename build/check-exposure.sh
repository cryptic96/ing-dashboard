#!/usr/bin/env bash
set -euo pipefail

###
### Operator tool: checks from the machine it runs on what the three public
### hostnames answer, so a mistake in the reverse proxy shows up before
### anyone else finds it. Run it from a workstation, and for the outside
### matrix from a laptop tethered to a phone with the VPN off. Every request
### is a plain HTTPS request without redirects and with a 10-second timeout;
### nothing is ever changed on any host.
###
### Usage: check-exposure.sh --from outside|inside \
###          --mcp-host HOST --api-host HOST --grafana-host HOST
###
###   --from outside   the machine is on the internet only. Every MCP, OAuth
###                    and sign-in path of the MCP hostname must answer 403
###                    (the address list refuses it), every other path of it
###                    404, the REST hostname 403 for /api/ and 404
###                    elsewhere, the Grafana hostname 403.
###   --from inside    the machine is on the home network or VPN. Discovery,
###                    the 401 challenge, sign-in and the REST 401 must
###                    behave as designed.
###
### Prints one PASS or FAIL line per request and exits 1 on any FAIL.
###

FAILURES=0
CURL_MAX_TIME=10

usage() {
  echo "Usage: check-exposure.sh --from outside|inside --mcp-host HOST --api-host HOST --grafana-host HOST" >&2
  exit 2
}

pass() {
  printf 'PASS - %s\n' "$*"
}

fail() {
  printf 'FAIL - %s\n' "$*"
  FAILURES=$((FAILURES + 1))
}

valid_host() {
  [[ "$1" =~ ^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$ ]]
}

FROM=""
MCP_HOST=""
API_HOST=""
GRAFANA_HOST=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --from)
      [[ $# -ge 2 ]] || usage
      FROM="$2"
      shift 2
      ;;
    --mcp-host)
      [[ $# -ge 2 ]] || usage
      MCP_HOST="$2"
      shift 2
      ;;
    --api-host)
      [[ $# -ge 2 ]] || usage
      API_HOST="$2"
      shift 2
      ;;
    --grafana-host)
      [[ $# -ge 2 ]] || usage
      GRAFANA_HOST="$2"
      shift 2
      ;;
    *) usage ;;
  esac
done

[[ "$FROM" == "outside" || "$FROM" == "inside" ]] || usage
[[ -n "$MCP_HOST" && -n "$API_HOST" && -n "$GRAFANA_HOST" ]] || usage
valid_host "$MCP_HOST" || usage
valid_host "$API_HOST" || usage
valid_host "$GRAFANA_HOST" || usage

PROBE_STATUS=""
PROBE_HEADERS=""

probe() {
  local method="$1" host="$2" path="$3" header_file
  header_file="$(mktemp)"
  PROBE_STATUS="$(curl --silent --proto '=https' --max-time "$CURL_MAX_TIME" \
    --request "$method" --dump-header "$header_file" --output /dev/null \
    --write-out '%{http_code}' "https://${host}${path}" 2> /dev/null || true)"
  PROBE_HEADERS="$(cat "$header_file")"
  rm -f "$header_file"
  [[ -n "$PROBE_STATUS" ]] || PROBE_STATUS="000"
}

has_status() {
  local actual="$1" expected_list="$2" candidate
  for candidate in $expected_list; do
    [[ "$actual" == "$candidate" ]] && return 0
  done
  return 1
}

expect() {
  local method="$1" host="$2" path="$3" expected="$4" hint="${5:-}"
  probe "$method" "$host" "$path"
  local label="${method} ${host}${path}"
  if has_status "$PROBE_STATUS" "$expected"; then
    pass "${label}: expected ${expected// / or }, got ${PROBE_STATUS}"
    return 0
  fi
  if [[ "$PROBE_STATUS" == "000" ]]; then
    fail "${label}: no answer (connection failed or timed out)${hint:+; ${hint}}"
  else
    fail "${label}: expected ${expected// / or }, got ${PROBE_STATUS}"
  fi
  return 1
}

check_outside() {
  local unreachable_hint="Anthropic could not reach it either"
  local path

  for path in /.well-known/oauth-protected-resource/mcp /.well-known/oauth-authorization-server \
    /.well-known/openid-configuration /connect/authorize /account/login; do
    expect GET "$MCP_HOST" "$path" 403 "$unreachable_hint" || true
  done
  expect POST "$MCP_HOST" /mcp 403 "$unreachable_hint" || true
  expect POST "$MCP_HOST" /connect/token 403 "$unreachable_hint" || true
  for path in / /api/v1/status /metrics /health; do
    expect GET "$MCP_HOST" "$path" 404 "$unreachable_hint" || true
  done

  expect GET "$API_HOST" /api/v1/status 403 || true
  expect GET "$API_HOST" /mcp 404 || true
  expect GET "$API_HOST" /connect/authorize 404 || true

  expect GET "$GRAFANA_HOST" / 403 || true
  expect GET "$GRAFANA_HOST" /login 403 || true
}

check_inside() {
  local path
  local metadata_url="https://${MCP_HOST}/.well-known/oauth-protected-resource/mcp"

  if expect POST "$MCP_HOST" /mcp 401; then
    if grep -i '^www-authenticate:' <<< "$PROBE_HEADERS" | grep -qF -- "resource_metadata=\"${metadata_url}\""; then
      pass "POST ${MCP_HOST}/mcp challenge names ${metadata_url}"
    else
      fail "POST ${MCP_HOST}/mcp challenge lacks resource_metadata=\"${metadata_url}\""
    fi
  fi

  for path in /.well-known/oauth-protected-resource/mcp /.well-known/oauth-authorization-server \
    /.well-known/openid-configuration /account/login; do
    expect GET "$MCP_HOST" "$path" 200 || true
  done
  expect GET "$MCP_HOST" /connect/token "400 405" || true
  expect GET "$MCP_HOST" /connect/authorize 400 || true
  expect GET "$MCP_HOST" /api/v1/status 404 || true

  expect GET "$API_HOST" /api/v1/status 401 || true
  expect GET "$API_HOST" /mcp 404 || true

  expect GET "$GRAFANA_HOST" / "200 302" || true
}

if [[ "$FROM" == "outside" ]]; then
  check_outside
else
  check_inside
fi

echo "----"
echo "check-exposure (${FROM}): ${FAILURES} failure(s)"
if [[ "$FAILURES" -gt 0 ]]; then
  exit 1
fi
exit 0
