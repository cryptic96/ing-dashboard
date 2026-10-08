#!/usr/bin/env bash
###
### Logic tests for provision.sh's shared functions and 20-accounts.sh's
### env-file renderer. Sources both files in library mode
### (LEDGER_PROVISION_LIB_ONLY=1) so nothing here needs root, network
### access or any package to be installed.
###
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=deploy/tests/lib/host-guard.sh
source "${SCRIPT_DIR}/lib/host-guard.sh"
HOST_GUARD_DIR="$(mktemp -d)"
trap 'rm -rf "$HOST_GUARD_DIR"' EXIT
host_guard_install "$HOST_GUARD_DIR"
DEPLOY_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

# shellcheck source=deploy/provision.sh
LEDGER_PROVISION_LIB_ONLY=1 source "${DEPLOY_DIR}/provision.sh"
# shellcheck source=deploy/provision.d/20-accounts.sh
LEDGER_PROVISION_LIB_ONLY=1 source "${DEPLOY_DIR}/provision.d/20-accounts.sh"
# shellcheck source=deploy/provision.d/10-packages.sh
LEDGER_PROVISION_LIB_ONLY=1 source "${DEPLOY_DIR}/provision.d/10-packages.sh"

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

rendered_mcp="$(accounts_render_ledger_env "192.0.2.10" "s3cr3t-password" "mcp.example.com" "192.0.2.0/24,198.51.100.0/24")"
expected_mcp=$'ASPNETCORE_ENVIRONMENT=Production\nReverseProxy__KnownProxies__0=192.0.2.10\nDataProtection__CertificatePath=/etc/ledger/dataprotection.pfx\nDataProtection__CertificatePassword=s3cr3t-password\nOAuth__PublicBaseUrl=https://mcp.example.com\nOAuth__SignInNetworks__0=192.0.2.0/24\nOAuth__SignInNetworks__1=198.51.100.0/24'
assert_eq "accounts_render_ledger_env: renders the OAuth keys from the MCP domain and the home and VPN ranges" "$expected_mcp" "$rendered_mcp"

rendered_spaced="$(accounts_render_ledger_env "192.0.2.10" "s3cr3t-password" "mcp.example.com" " 192.0.2.0/24 , 198.51.100.0/24 ")"
assert_eq "accounts_render_ledger_env: trims blanks around the ranges" "$expected_mcp" "$rendered_spaced"

if grep -q '^OAuth__' <<<"$rendered"; then
  failtest "accounts_render_ledger_env: renders no OAuth key without an MCP domain"
else
  pass "accounts_render_ledger_env: renders no OAuth key without an MCP domain"
fi

if accounts_valid_mcp_domain "mcp.example.com"; then
  pass "accounts_valid_mcp_domain: accepts a plain hostname"
else
  failtest "accounts_valid_mcp_domain: accepts a plain hostname"
fi

for bad_domain in "mcp" "mcp.example.com/path" "mcp.example.com extra" "-mcp.example.com" "https://mcp.example.com"; do
  if accounts_valid_mcp_domain "$bad_domain"; then
    failtest "accounts_valid_mcp_domain: rejects [${bad_domain}] (expected failure, got success)"
  else
    pass "accounts_valid_mcp_domain: rejects [${bad_domain}]"
  fi
done

###
### --- version pins reach an existing host ---------------------------------
###

assert_eq "grafana_pin_preferences: renders the apt pin for the given version" \
  $'Package: grafana\nPin: version 13.2.3\nPin-Priority: 1001' "$(grafana_pin_preferences "13.2.3")"

if prometheus_needs_install "" "3.13.4"; then
  pass "prometheus_needs_install: a missing binary is installed"
else
  failtest "prometheus_needs_install: a missing binary is installed"
fi

if prometheus_needs_install "prometheus, version 3.13.3 (branch: HEAD, revision: abc)" "3.13.4"; then
  pass "prometheus_needs_install: an older installed version is replaced by the pin"
else
  failtest "prometheus_needs_install: an older installed version is replaced by the pin"
fi

if prometheus_needs_install "prometheus, version 3.13.4 (branch: HEAD, revision: abc)" "3.13.4"; then
  failtest "prometheus_needs_install: the pinned version already installed is left alone (expected failure, got success)"
else
  pass "prometheus_needs_install: the pinned version already installed is left alone"
fi

if prometheus_needs_install "something unexpected" "3.13.4"; then
  pass "prometheus_needs_install: an unrecognised version line is reinstalled rather than trusted"
else
  failtest "prometheus_needs_install: an unrecognised version line is reinstalled rather than trusted"
fi

###
### --- signing keyrings and apt sources converge on every run --------------
###

KEYRING_TEST_DIR="$(mktemp -d)"
trap 'rm -rf "$HOST_GUARD_DIR" "$KEYRING_TEST_DIR"' EXIT
export GNUPGHOME="${KEYRING_TEST_DIR}/gnupg"
mkdir -m 700 "$GNUPGHOME"

make_test_keyring() {
  local uid="$1" keyring_path="$2" fingerprint
  gpg --batch --quiet --passphrase '' --quick-generate-key "$uid" ed25519 sign never >/dev/null 2>&1
  fingerprint="$(gpg --batch --with-colons --list-keys "$uid" 2>/dev/null | awk -F: '$1 == "fpr" {print $10; exit}')"
  gpg --batch --export "$fingerprint" >"$keyring_path"
  printf '%s' "$fingerprint"
}

FIRST_KEYRING="${KEYRING_TEST_DIR}/first.gpg"
SECOND_KEYRING="${KEYRING_TEST_DIR}/second.gpg"
FIRST_FPR="$(make_test_keyring "First Test <first@example.com>" "$FIRST_KEYRING")"
SECOND_FPR="$(make_test_keyring "Second Test <second@example.com>" "$SECOND_KEYRING")"

if apt_keyring_matches_pin "$FIRST_KEYRING" "$FIRST_FPR"; then
  pass "apt_keyring_matches_pin: a keyring holding the pinned fingerprint matches"
else
  failtest "apt_keyring_matches_pin: a keyring holding the pinned fingerprint matches"
fi

if apt_keyring_matches_pin "$FIRST_KEYRING" "$SECOND_FPR"; then
  failtest "apt_keyring_matches_pin: a keyring from before a pin bump is replaced (expected failure, got success)"
else
  pass "apt_keyring_matches_pin: a keyring from before a pin bump is replaced"
fi

if apt_keyring_matches_pin "${KEYRING_TEST_DIR}/missing.gpg" "$FIRST_FPR"; then
  failtest "apt_keyring_matches_pin: a missing keyring does not match (expected failure, got success)"
else
  pass "apt_keyring_matches_pin: a missing keyring does not match"
fi

: >"${KEYRING_TEST_DIR}/empty.gpg"
if apt_keyring_matches_pin "${KEYRING_TEST_DIR}/empty.gpg" "$FIRST_FPR"; then
  failtest "apt_keyring_matches_pin: an empty keyring left by a half-finished run does not match (expected failure, got success)"
else
  pass "apt_keyring_matches_pin: an empty keyring left by a half-finished run does not match"
fi

head -c 40 "$FIRST_KEYRING" >"${KEYRING_TEST_DIR}/truncated.gpg"
if apt_keyring_matches_pin "${KEYRING_TEST_DIR}/truncated.gpg" "$FIRST_FPR"; then
  failtest "apt_keyring_matches_pin: a truncated keyring does not match (expected failure, got success)"
else
  pass "apt_keyring_matches_pin: a truncated keyring does not match"
fi

assert_eq "apt_source_line: renders the signed-by sources line" \
  "deb [signed-by=/usr/share/keyrings/example.gpg] https://apt.example.com stable main" \
  "$(apt_source_line /usr/share/keyrings/example.gpg https://apt.example.com stable main)"

SOURCE_FILE="${KEYRING_TEST_DIR}/example.list"
install_apt_source "$SOURCE_FILE" "deb [signed-by=/k.gpg] https://apt.example.com stable main" >/dev/null
assert_eq "install_apt_source: writes a missing sources file" \
  "deb [signed-by=/k.gpg] https://apt.example.com stable main" "$(cat "$SOURCE_FILE")"

install_apt_source "$SOURCE_FILE" "deb [signed-by=/k.gpg] https://apt.example.com unstable main" >/dev/null
assert_eq "install_apt_source: rewrites a sources file whose content changed" \
  "deb [signed-by=/k.gpg] https://apt.example.com unstable main" "$(cat "$SOURCE_FILE")"

touch -d '2001-01-01 00:00:00' "$SOURCE_FILE"
install_apt_source "$SOURCE_FILE" "deb [signed-by=/k.gpg] https://apt.example.com unstable main" >/dev/null
assert_eq "install_apt_source: leaves an up-to-date sources file untouched" \
  "2001-01-01" "$(date -r "$SOURCE_FILE" +%F)"

echo "----"
echo "${TESTS_RUN} test(s) run, ${FAILURES} failure(s)"
if [[ "$FAILURES" -gt 0 ]]; then
  exit 1
fi
exit 0
