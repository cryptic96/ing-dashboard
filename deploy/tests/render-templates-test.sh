#!/usr/bin/env bash
###
### Logic tests for provision.sh's template-rendering functions and the
### services/firewall modules' own rendering helpers. Sources everything in
### library mode (LEDGER_PROVISION_LIB_ONLY=1) so nothing here needs root,
### network access or any package to be installed. Placeholder values only.
###
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

# shellcheck source=deploy/provision.sh
LEDGER_PROVISION_LIB_ONLY=1 source "${DEPLOY_DIR}/provision.sh"
# shellcheck source=deploy/provision.d/40-services.sh
LEDGER_PROVISION_LIB_ONLY=1 source "${DEPLOY_DIR}/provision.d/40-services.sh"

# Sourcing the files above also applies their own "set -euo pipefail" to
# this shell; restore this script's own intended options (no -e, since
# several assertions below deliberately run commands expected to fail).
set -uo pipefail

FAILURES=0
TESTS_RUN=0

pass() {
  TESTS_RUN=$((TESTS_RUN + 1))
  echo "ok - $*"
}

failtest() {
  TESTS_RUN=$((TESTS_RUN + 1))
  FAILURES=$((FAILURES + 1))
  echo "not ok - $*"
}

assert_eq() {
  local label="$1" expected="$2" actual="$3"
  if [[ "$expected" == "$actual" ]]; then
    pass "$label"
  else
    failtest "$label (expected [${expected}], got [${actual}])"
  fi
}

# Runs "$@" in a subshell so a provision_die-triggered "exit" only ends the
# subshell, not this test script, and reports whether it succeeded.
subshell_succeeds() {
  (
    "$@"
  ) > /dev/null 2>&1
}

###
### --- provision_render_template: valid substitution ----------------------
###

template_ok="$(mktemp)"
printf 'traefik=@LEDGER_TRAEFIK_IP@\nssh=@LEDGER_ADMIN_SSH_SOURCES@\n' > "$template_ok"
output_ok="$(mktemp)"

if subshell_succeeds provision_render_template "$template_ok" "$output_ok" \
  "LEDGER_TRAEFIK_IP=192.0.2.10" "LEDGER_ADMIN_SSH_SOURCES=192.0.2.0/24,198.51.100.0/24"; then
  provision_render_template "$template_ok" "$output_ok" \
    "LEDGER_TRAEFIK_IP=192.0.2.10" "LEDGER_ADMIN_SSH_SOURCES=192.0.2.0/24,198.51.100.0/24"
  rendered_ok="$(cat "$output_ok")"
  assert_eq "provision_render_template: replaces @LEDGER_TRAEFIK_IP@ with a valid IPv4 address" \
    $'traefik=192.0.2.10\nssh=192.0.2.0/24,198.51.100.0/24' "$rendered_ok"
else
  failtest "provision_render_template: accepts a valid IPv4 address and CIDR list (expected success, got failure)"
fi
rm -f "$output_ok"

###
### --- provision_render_template: rejects an invalid IPv4 address --------
###

if subshell_succeeds provision_render_template "$template_ok" "$output_ok" \
  "LEDGER_TRAEFIK_IP=999.0.2.10" "LEDGER_ADMIN_SSH_SOURCES=192.0.2.0/24"; then
  failtest "provision_render_template: rejects an invalid IPv4 address (expected failure, got success)"
else
  pass "provision_render_template: rejects an invalid IPv4 address"
fi

###
### --- provision_render_template: rejects an invalid CIDR ----------------
###

if subshell_succeeds provision_render_template "$template_ok" "$output_ok" \
  "LEDGER_TRAEFIK_IP=192.0.2.10" "LEDGER_ADMIN_SSH_SOURCES=192.0.2.0/99"; then
  failtest "provision_render_template: rejects an invalid CIDR (expected failure, got success)"
else
  pass "provision_render_template: rejects an invalid CIDR"
fi

###
### --- provision_render_template: rejects a semicolon/brace/newline ------
###

if subshell_succeeds provision_render_template "$template_ok" "$output_ok" \
  "LEDGER_TRAEFIK_IP=192.0.2.10" "LEDGER_ADMIN_SSH_SOURCES=192.0.2.0/24; drop table"; then
  failtest "provision_render_template: rejects a value containing a semicolon (expected failure, got success)"
else
  pass "provision_render_template: rejects a value containing a semicolon"
fi

if subshell_succeeds provision_render_template "$template_ok" "$output_ok" \
  "LEDGER_TRAEFIK_IP=192.0.2.10" "LEDGER_ADMIN_SSH_SOURCES={192.0.2.0/24}"; then
  failtest "provision_render_template: rejects a value containing a brace (expected failure, got success)"
else
  pass "provision_render_template: rejects a value containing a brace"
fi

if subshell_succeeds provision_render_template "$template_ok" "$output_ok" \
  "LEDGER_TRAEFIK_IP=192.0.2.10" $'LEDGER_ADMIN_SSH_SOURCES=192.0.2.0/24\nssh dport 2222 accept'; then
  failtest "provision_render_template: rejects a value containing a newline (expected failure, got success)"
else
  pass "provision_render_template: rejects a value containing a newline"
fi

###
### --- provision_render_template: fails on an unreplaced token -----------
###

template_missing="$(mktemp)"
printf 'traefik=@LEDGER_TRAEFIK_IP@\nother=@LEDGER_UNKNOWN_TOKEN@\n' > "$template_missing"

if subshell_succeeds provision_render_template "$template_missing" "$output_ok" \
  "LEDGER_TRAEFIK_IP=192.0.2.10"; then
  failtest "provision_render_template: fails when a @TOKEN@ is left unreplaced (expected failure, got success)"
else
  pass "provision_render_template: fails when a @TOKEN@ is left unreplaced"
fi
rm -f "$template_missing" "$template_ok" "$output_ok"

###
### --- rendered nftables file: policy drop, SSH and app/Grafana rules ----
###

nft_output="$(mktemp)"
provision_render_template "${DEPLOY_DIR}/nftables/ledger.nft.in" "$nft_output" \
  "LEDGER_ADMIN_SSH_SOURCES=192.0.2.0/24" "LEDGER_TRAEFIK_IP=192.0.2.10"
nft_content="$(cat "$nft_output")"
rm -f "$nft_output"

drop_count="$(grep -c 'policy drop' <<< "$nft_content")"
assert_eq "rendered nftables: has an input and a forward chain with policy drop" "2" "$drop_count"

if grep -qE 'tcp dport \{ 5080, 3000 \} ip saddr 192\.0\.2\.10 accept' <<< "$nft_content"; then
  pass "rendered nftables: admits 5080 and 3000 only from the Traefik address"
else
  failtest "rendered nftables: admits 5080 and 3000 only from the Traefik address"
fi

if grep -qE 'tcp dport 22 ip saddr \{ 192\.0\.2\.0/24 \} accept' <<< "$nft_content"; then
  pass "rendered nftables: admits SSH only from the admin/VPN CIDR list"
else
  failtest "rendered nftables: admits SSH only from the admin/VPN CIDR list"
fi

###
### --- rendered msmtprc: STARTTLS on and off ------------------------------
###

msmtp_on="$(mktemp)"
services_render_msmtprc "smtp-relay.example.com:25" "ledger-alerts@example.com" "on" "$msmtp_on"
msmtp_on_content="$(cat "$msmtp_on")"
rm -f "$msmtp_on"

if grep -q '^host smtp-relay.example.com$' <<< "$msmtp_on_content" \
  && grep -q '^port 25$' <<< "$msmtp_on_content" \
  && grep -q '^from ledger-alerts@example.com$' <<< "$msmtp_on_content" \
  && grep -q '^tls on$' <<< "$msmtp_on_content" \
  && grep -q '^tls_starttls on$' <<< "$msmtp_on_content"; then
  pass "rendered msmtprc (STARTTLS on): contains host, port, from and tls lines"
else
  failtest "rendered msmtprc (STARTTLS on): contains host, port, from and tls lines"
fi

if grep -niE '^\s*(auth on|password|user )' <<< "$msmtp_on_content"; then
  failtest "rendered msmtprc: contains no auth credentials (expected none, found one)"
else
  pass "rendered msmtprc: contains no auth credentials"
fi

msmtp_off="$(mktemp)"
services_render_msmtprc "smtp-relay.example.com:25" "ledger-alerts@example.com" "off" "$msmtp_off"
msmtp_off_content="$(cat "$msmtp_off")"
rm -f "$msmtp_off"

if grep -q '^tls off$' <<< "$msmtp_off_content"; then
  pass "rendered msmtprc (STARTTLS off): contains tls off"
else
  failtest "rendered msmtprc (STARTTLS off): contains tls off"
fi

###
### --- age recipient validator: one age1 key, rejects anything else ------
###

if provision_validate_age_public_key "age1qyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqsxwq49r"; then
  pass "provision_validate_age_public_key: accepts a single age1 public key"
else
  failtest "provision_validate_age_public_key: accepts a single age1 public key"
fi

if provision_validate_age_public_key "AGE-SECRET-KEY-1QYQSZQGPQYQSZQGPQYQSZQGPQYQSZQGPQYQSZQGPQYQSZQGPQYQSZQGP"; then
  failtest "provision_validate_age_public_key: rejects a private identity (expected failure, got success)"
else
  pass "provision_validate_age_public_key: rejects a private identity"
fi

if provision_validate_age_public_key "not-an-age-key-at-all"; then
  failtest "provision_validate_age_public_key: rejects an arbitrary string (expected failure, got success)"
else
  pass "provision_validate_age_public_key: rejects an arbitrary string"
fi

if provision_validate_age_public_key "age1validkey another-line"; then
  failtest "provision_validate_age_public_key: rejects more than one key/line (expected failure, got success)"
else
  pass "provision_validate_age_public_key: rejects more than one key/line"
fi

echo "----"
echo "${TESTS_RUN} test(s) run, ${FAILURES} failure(s)"
if [[ "$FAILURES" -gt 0 ]]; then
  exit 1
fi
exit 0
