#!/usr/bin/env bash
# Proves check-exposure.sh passes on the designed answers and fails on every
# deviation, with curl replaced by a stand-in that answers from a fixture
# file. Nothing here ever makes a network request.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
SCRIPT="${REPO_ROOT}/build/check-exposure.sh"

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

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

STUB_BIN="${WORKDIR}/bin"
mkdir -p "$STUB_BIN"

cat > "${STUB_BIN}/curl" <<'EOF_STUB'
#!/usr/bin/env bash
method="GET"
dump_file=""
url=""
args=("$@")
for ((i = 0; i < ${#args[@]}; i++)); do
  case "${args[$i]}" in
    --request) method="${args[$((i + 1))]}" ;;
    --dump-header) dump_file="${args[$((i + 1))]}" ;;
    https://*) url="${args[$i]}" ;;
  esac
done
key="${method} ${url#https://}"
status="$(awk -v k="$key" '{ if (($1 " " $2) == k) { print $3; exit } }' "$STUB_MAP")"
if [[ -z "$status" ]]; then
  exit 7
fi
if [[ -n "$dump_file" ]]; then
  printf 'HTTP/1.1 %s\r\n' "$status" > "$dump_file"
  if [[ "$key" == "POST ${STUB_MCP_HOST}/mcp" && -n "${STUB_CHALLENGE:-}" ]]; then
    printf 'WWW-Authenticate: Bearer resource_metadata="%s"\r\n' "$STUB_CHALLENGE" >> "$dump_file"
  fi
fi
printf '%s' "$status"
EOF_STUB
chmod +x "${STUB_BIN}/curl"
export PATH="${STUB_BIN}:${PATH}"

MCP="mcp.example.com"
API="ledger-api.example.com"
GRAFANA="grafana.example.com"
export STUB_MCP_HOST="$MCP"
export STUB_MAP="${WORKDIR}/map.txt"

outside_map() {
  cat <<EOF
POST ${MCP}/mcp 403
GET ${MCP}/.well-known/oauth-protected-resource/mcp 403
GET ${MCP}/.well-known/oauth-authorization-server 403
GET ${MCP}/.well-known/openid-configuration 403
POST ${MCP}/connect/token 403
GET ${MCP}/connect/authorize 403
GET ${MCP}/account/login 403
GET ${MCP}/ 404
GET ${MCP}/api/v1/status 404
GET ${MCP}/metrics 404
GET ${MCP}/health 404
GET ${API}/api/v1/status 403
GET ${API}/mcp 404
GET ${API}/connect/authorize 404
GET ${GRAFANA}/ 403
GET ${GRAFANA}/login 403
EOF
}

inside_map() {
  cat <<EOF
POST ${MCP}/mcp 401
GET ${MCP}/.well-known/oauth-protected-resource/mcp 200
GET ${MCP}/.well-known/oauth-authorization-server 200
GET ${MCP}/.well-known/openid-configuration 200
GET ${MCP}/connect/token 405
GET ${MCP}/connect/authorize 400
GET ${MCP}/account/login 200
GET ${MCP}/api/v1/status 404
GET ${API}/api/v1/status 401
GET ${API}/mcp 404
GET ${GRAFANA}/ 302
EOF
}

GOOD_CHALLENGE="https://${MCP}/.well-known/oauth-protected-resource/mcp"

# Runs the script against the map on stdin and prints "<exit code>|<output>".
run_exposure() {
  local from="$1"
  cat > "$STUB_MAP"
  local output code
  output="$("$SCRIPT" --from "$from" --mcp-host "$MCP" --api-host "$API" --grafana-host "$GRAFANA" 2>&1)"
  code=$?
  printf '%s|%s' "$code" "$output"
}

code_of() { printf '%s' "${1%%|*}"; }
output_of() { printf '%s' "${1#*|}"; }

contains() {
  local found=0
  if grep -qF -- "$2" <<< "$1"; then
    found=1
  fi
  printf '%s' "$found"
}

# --- outside matrix ---------------------------------------------------------
unset STUB_CHALLENGE
RESULT="$(outside_map | run_exposure outside)"
check "outside: the designed answers pass" "0" "$(code_of "$RESULT")"
check "outside: a PASS line names host, method and path" "1" \
  "$(contains "$(output_of "$RESULT")" "PASS - POST ${MCP}/mcp: expected 403, got 403")"

RESULT="$(outside_map | sed "s#^POST ${MCP}/mcp 403#POST ${MCP}/mcp 200#" | run_exposure outside)"
check "outside: a 200 on /mcp fails" "1" "$(code_of "$RESULT")"
check "outside: the failing line names the path and the status" "1" \
  "$(contains "$(output_of "$RESULT")" "FAIL - POST ${MCP}/mcp: expected 403, got 200")"

RESULT="$(outside_map | sed "s#^POST ${MCP}/mcp 403#POST ${MCP}/mcp 401#" | run_exposure outside)"
check "outside: a 401 on /mcp fails (the address list is not in front of it)" "1" "$(code_of "$RESULT")"

RESULT="$(outside_map | sed "s#^GET ${MCP}/account/login 403#GET ${MCP}/account/login 200#" | run_exposure outside)"
check "outside: a reachable sign-in page fails" "1" "$(code_of "$RESULT")"

RESULT="$(outside_map | sed "s#^GET ${MCP}/connect/authorize 403#GET ${MCP}/connect/authorize 400#" | run_exposure outside)"
check "outside: a reachable authorize endpoint fails" "1" "$(code_of "$RESULT")"

RESULT="$(outside_map | grep -v "^POST ${MCP}/connect/token " | run_exposure outside)"
check "outside: a connection failure on the MCP host fails" "1" "$(code_of "$RESULT")"
check "outside: the connection failure carries the Anthropic hint" "1" \
  "$(contains "$(output_of "$RESULT")" "Anthropic could not reach it either")"

RESULT="$(outside_map | sed "s#^GET ${MCP}/metrics 404#GET ${MCP}/metrics 200#" | run_exposure outside)"
check "outside: a served metrics path on the MCP host fails" "1" "$(code_of "$RESULT")"

RESULT="$(outside_map | sed "s#^GET ${MCP}/ 404#GET ${MCP}/ 403#" | run_exposure outside)"
check "outside: a 403 where 404 is designed fails" "1" "$(code_of "$RESULT")"

RESULT="$(outside_map | sed "s#^GET ${API}/api/v1/status 403#GET ${API}/api/v1/status 401#" | run_exposure outside)"
check "outside: the REST hostname answering 401 fails" "1" "$(code_of "$RESULT")"

RESULT="$(outside_map | sed "s#^GET ${API}/mcp 404#GET ${API}/mcp 401#" | run_exposure outside)"
check "outside: /mcp served on the REST hostname fails" "1" "$(code_of "$RESULT")"

RESULT="$(outside_map | sed "s#^GET ${GRAFANA}/ 403#GET ${GRAFANA}/ 200#" | run_exposure outside)"
check "outside: Grafana answering 200 fails" "1" "$(code_of "$RESULT")"

RESULT="$(outside_map | grep -v "^GET ${GRAFANA}/login " | run_exposure outside)"
check "outside: a connection failure on the Grafana host fails" "1" "$(code_of "$RESULT")"

# --- inside matrix ----------------------------------------------------------
export STUB_CHALLENGE="$GOOD_CHALLENGE"
RESULT="$(inside_map | run_exposure inside)"
check "inside: the designed answers pass" "0" "$(code_of "$RESULT")"

RESULT="$(inside_map | sed "s#^GET ${MCP}/connect/token 405#GET ${MCP}/connect/token 400#" | run_exposure inside)"
check "inside: a 400 on GET /connect/token passes" "0" "$(code_of "$RESULT")"

RESULT="$(inside_map | sed "s#^GET ${GRAFANA}/ 302#GET ${GRAFANA}/ 200#" | run_exposure inside)"
check "inside: Grafana answering 200 passes" "0" "$(code_of "$RESULT")"

export STUB_CHALLENGE=""
RESULT="$(inside_map | run_exposure inside)"
check "inside: a 401 without the resource_metadata challenge fails" "1" "$(code_of "$RESULT")"

export STUB_CHALLENGE="https://other.example.org/.well-known/oauth-protected-resource/mcp"
RESULT="$(inside_map | run_exposure inside)"
check "inside: a challenge naming another resource fails" "1" "$(code_of "$RESULT")"

export STUB_CHALLENGE="$GOOD_CHALLENGE"
RESULT="$(inside_map | sed "s#^GET ${MCP}/account/login 200#GET ${MCP}/account/login 404#" | run_exposure inside)"
check "inside: a missing sign-in page fails" "1" "$(code_of "$RESULT")"

RESULT="$(inside_map | sed "s#^GET ${MCP}/.well-known/openid-configuration 200#GET ${MCP}/.well-known/openid-configuration 404#" | run_exposure inside)"
check "inside: a missing discovery document fails" "1" "$(code_of "$RESULT")"

RESULT="$(inside_map | sed "s#^GET ${API}/api/v1/status 401#GET ${API}/api/v1/status 200#" | run_exposure inside)"
check "inside: the REST API answering 200 without a key fails" "1" "$(code_of "$RESULT")"

RESULT="$(inside_map | sed "s#^GET ${MCP}/api/v1/status 404#GET ${MCP}/api/v1/status 401#" | run_exposure inside)"
check "inside: the REST status path on the MCP hostname fails when served" "1" "$(code_of "$RESULT")"

# --- arguments --------------------------------------------------------------
: > "$STUB_MAP"
"$SCRIPT" --from sideways --mcp-host "$MCP" --api-host "$API" --grafana-host "$GRAFANA" > /dev/null 2>&1
check "an unknown --from value is refused with exit 2" "2" "$?"

"$SCRIPT" --from outside --mcp-host "$MCP" --api-host "$API" > /dev/null 2>&1
check "a missing hostname is refused with exit 2" "2" "$?"

"$SCRIPT" --from outside --mcp-host "bad host;x" --api-host "$API" --grafana-host "$GRAFANA" > /dev/null 2>&1
check "a malformed hostname is refused with exit 2" "2" "$?"

echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "All checks passed."
  exit 0
else
  echo "${FAILURES} check(s) failed."
  exit 1
fi
