#!/usr/bin/env bash
###
### Re-runnable check that the address range the reverse-proxy template lets
### through for Anthropic's connector traffic is still on Anthropic's
### published IP address page. Run it before each release: the page changes
### only with notice, but a range that has been retired would silently stop
### connector traffic or keep admitting an address Anthropic no longer owns.
### Named *-network-test.sh so the default lint run skips it; run it with
### LEDGER_LINT_NETWORK=1.
###
### Every range of the ledger-mcp-allow middleware that is not a documentation
### placeholder (RFC 5737) must appear on the page.
###
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATE="${SCRIPT_DIR}/../../deploy/traefik/ledger.yml.example"
PAGE_URL="${LEDGER_ANTHROPIC_IP_PAGE_URL:-https://platform.claude.com/docs/en/api/ip-addresses}"
CURL_MAX_TIME=30

FAILURES=0

fail() {
  echo "FAIL: $*" >&2
  FAILURES=$((FAILURES + 1))
}

ok() {
  echo "PASS: $*"
}

is_placeholder_range() {
  case "$1" in
    192.0.2.* | 198.51.100.* | 203.0.113.*) return 0 ;;
    *) return 1 ;;
  esac
}

ranges_of_allow_middleware() {
  awk '
    /^    ledger-mcp-allow:/ { inside = 1; next }
    inside && /^    [A-Za-z]/ { inside = 0 }
    inside && /^ *- "/ {
      gsub(/^ *- "/, "")
      gsub(/".*$/, "")
      print
    }
  ' "$TEMPLATE"
}

if [[ ! -f "$TEMPLATE" ]]; then
  fail "template not found at ${TEMPLATE}"
  exit 1
fi

mapfile -t RANGES < <(ranges_of_allow_middleware)
if [[ "${#RANGES[@]}" -eq 0 ]]; then
  fail "no range found in the ledger-mcp-allow middleware"
  exit 1
fi

PAGE="$(curl --fail --silent --show-error --location --max-time "$CURL_MAX_TIME" "$PAGE_URL")" \
  || {
    fail "could not fetch ${PAGE_URL}"
    exit 1
  }

CHECKED=0
for range in "${RANGES[@]}"; do
  if is_placeholder_range "$range"; then
    continue
  fi
  CHECKED=$((CHECKED + 1))
  if grep -qF -- "$range" <<< "$PAGE"; then
    ok "${range} is listed on ${PAGE_URL}"
  else
    fail "${range} is in the template but is not on ${PAGE_URL}"
  fi
done

if [[ "$CHECKED" -eq 0 ]]; then
  fail "the ledger-mcp-allow middleware holds no Anthropic range"
fi

if [[ "$FAILURES" -gt 0 ]]; then
  echo "${FAILURES} check(s) failed." >&2
  exit 1
fi
echo "All checks passed."
