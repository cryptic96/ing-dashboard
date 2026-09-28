#!/usr/bin/env bash
###
### Logic tests for provision.sh's shared functions and 20-accounts.sh's
### env-file renderer. Sources both files in library mode
### (LEDGER_PROVISION_LIB_ONLY=1) so nothing here needs root, network
### access or any package to be installed.
###
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

# shellcheck source=deploy/provision.sh
LEDGER_PROVISION_LIB_ONLY=1 source "${DEPLOY_DIR}/provision.sh"
# shellcheck source=deploy/provision.d/20-accounts.sh
LEDGER_PROVISION_LIB_ONLY=1 source "${DEPLOY_DIR}/provision.d/20-accounts.sh"

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
  ) >/dev/null 2>&1
}

# shellcheck disable=SC2034 # read via the nameref in _provision_parse_kv_file
TEST_ALLOWED_KEYS=(FOO BAR BAZ)

###
### --- config parser: accepts allowed KEY=VALUE, quotes, comments -------
###

conf_ok="$(mktemp)"
cat >"$conf_ok" <<'EOF'
# a full-line comment
FOO=hello
BAR="hello world"
# another comment

BAZ=192.0.2.10
EOF

if (
  unset FOO BAR BAZ
  _provision_parse_kv_file "$conf_ok" TEST_ALLOWED_KEYS
  [[ "$FOO" == "hello" && "$BAR" == "hello world" && "$BAZ" == "192.0.2.10" ]]
); then
  pass "config parser: accepts allowed KEY=VALUE with quotes and comments"
else
  failtest "config parser: accepts allowed KEY=VALUE with quotes and comments"
fi
rm -f "$conf_ok"

###
### --- config parser: rejects an unknown key -----------------------------
###

conf_unknown="$(mktemp)"
echo "QUUX=nope" >"$conf_unknown"
if subshell_succeeds _provision_parse_kv_file "$conf_unknown" TEST_ALLOWED_KEYS; then
  failtest "config parser: rejects an unknown key (expected failure, got success)"
else
  pass "config parser: rejects an unknown key"
fi
rm -f "$conf_unknown"

###
### --- config parser: rejects backticks and command substitution -------
###

conf_backtick="$(mktemp)"
# shellcheck disable=SC2016 # intentional: writing literal backtick text, not expanding it
echo 'FOO=`id`' >"$conf_backtick"
if subshell_succeeds _provision_parse_kv_file "$conf_backtick" TEST_ALLOWED_KEYS; then
  failtest "config parser: rejects a backtick (expected failure, got success)"
else
  pass "config parser: rejects a backtick"
fi
rm -f "$conf_backtick"

conf_subst="$(mktemp)"
# shellcheck disable=SC2016 # intentional: writing literal command-substitution text, not expanding it
echo 'FOO=$(id)' >"$conf_subst"
if (
  unset FOO
  subshell_succeeds _provision_parse_kv_file "$conf_subst" TEST_ALLOWED_KEYS
); then
  failtest "config parser: rejects command substitution, never evaluates it (expected failure, got success)"
else
  pass "config parser: rejects command substitution, never evaluates it"
fi
if [[ "${FOO:-unset}" != "unset" ]]; then
  failtest "config parser: command substitution attempt did not leak into FOO"
else
  pass "config parser: command substitution attempt did not leak into FOO"
fi
rm -f "$conf_subst"

###
### --- config parser: rejects a malformed line ---------------------------
###

conf_malformed="$(mktemp)"
echo "this is not key=value" >"$conf_malformed"
if subshell_succeeds _provision_parse_kv_file "$conf_malformed" TEST_ALLOWED_KEYS; then
  failtest "config parser: rejects a malformed line (expected failure, got success)"
else
  pass "config parser: rejects a malformed line"
fi
rm -f "$conf_malformed"

###
### --- provision_load_conf: refuses a file not owned by root ------------
###

conf_notroot="$(mktemp)"
chmod 600 "$conf_notroot"
echo "FOO=bar" >"$conf_notroot"
if subshell_succeeds provision_load_conf "$conf_notroot"; then
  failtest "provision_load_conf: refuses a file not owned by root (expected failure, got success)"
else
  pass "provision_load_conf: refuses a file not owned by root"
fi
rm -f "$conf_notroot"

###
### --- version comparison helper -----------------------------------------
###

if provision_version_ge "2.101.0" "2.49.0"; then
  pass "provision_version_ge: 2.101.0 >= 2.49.0"
else
  failtest "provision_version_ge: 2.101.0 >= 2.49.0"
fi

if provision_version_ge "2.49.0" "2.49.0"; then
  pass "provision_version_ge: 2.49.0 >= 2.49.0 (equal)"
else
  failtest "provision_version_ge: 2.49.0 >= 2.49.0 (equal)"
fi

if provision_version_ge "2.10.0" "2.49.0"; then
  failtest "provision_version_ge: 2.10.0 is not >= 2.49.0 (expected failure, got success)"
else
  pass "provision_version_ge: 2.10.0 is not >= 2.49.0"
fi

###
### --- fingerprint extractor ----------------------------------------------
###

one_active_key=$'pub:-:4096:1:AAAAAAAAAAAAAAAA:1000000000:::-:::scESC::::::23::0:\nfpr:::::::::1111111111111111111111111111111111111111:\nuid:-::::1000000000::HASH::Test Key <test@example.com>::::::::::0:\nsub:-:4096:1:BBBBBBBBBBBBBBBB:1000000000::::::e::::::23:\nfpr:::::::::2222222222222222222222222222222222222222:'

fp="$(printf '%s\n' "$one_active_key" | provision_key_fingerprint)"
assert_eq "provision_key_fingerprint: returns the fpr of a single active primary key" \
  "1111111111111111111111111111111111111111" "$fp"

zero_active_keys=$'pub:e:4096:1:AAAAAAAAAAAAAAAA:1000000000:::-:::scESC::::::23::0:\nfpr:::::::::1111111111111111111111111111111111111111:\nuid:e::::1000000000::HASH::Test Key <test@example.com>::::::::::0:'

if printf '%s\n' "$zero_active_keys" | provision_key_fingerprint >/dev/null 2>&1; then
  failtest "provision_key_fingerprint: rejects zero active primary keys (expected failure, got success)"
else
  pass "provision_key_fingerprint: rejects zero active primary keys (all expired)"
fi

several_active_keys=$'pub:-:4096:1:AAAAAAAAAAAAAAAA:1000000000:::-:::scESC::::::23::0:\nfpr:::::::::1111111111111111111111111111111111111111:\nuid:-::::1000000000::HASH::Test Key <test@example.com>::::::::::0:\npub:-:4096:1:CCCCCCCCCCCCCCCC:1000000000:::-:::scESC::::::23::0:\nfpr:::::::::3333333333333333333333333333333333333333:\nuid:-::::1000000000::HASH::Second Key <second@example.com>::::::::::0:'

if printf '%s\n' "$several_active_keys" | provision_key_fingerprint >/dev/null 2>&1; then
  failtest "provision_key_fingerprint: rejects several active primary keys (expected failure, got success)"
else
  pass "provision_key_fingerprint: rejects several active primary keys"
fi

###
### --- env file rendering --------------------------------------------------
###

rendered="$(accounts_render_ledger_env "192.0.2.10" "s3cr3t-password")"
expected_rendered=$'ASPNETCORE_ENVIRONMENT=Production\nReverseProxy__KnownProxies__0=192.0.2.10\nDataProtection__CertificatePath=/etc/ledger/dataprotection.pfx\nDataProtection__CertificatePassword=s3cr3t-password'
assert_eq "accounts_render_ledger_env: renders exactly the expected keys" "$expected_rendered" "$rendered"

if echo "$rendered" | grep -qiE 'ConnectionStrings|Password=.*[Dd]atabase|Database.*Password'; then
  failtest "accounts_render_ledger_env: contains no database password"
else
  pass "accounts_render_ledger_env: contains no database password"
fi

echo "----"
echo "${TESTS_RUN} test(s) run, ${FAILURES} failure(s)"
if [[ "$FAILURES" -gt 0 ]]; then
  exit 1
fi
exit 0
