#!/usr/bin/env bash
# Proves the aggregator key helper: key generation into a temporary directory,
# the password protection of the key, the env file edits and the value
# validation. Sources ledger-bank-key in library mode, runs as a normal user
# and never touches /etc/ledger.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
BANK_KEY="${REPO_ROOT}/deploy/bin/ledger-bank-key"

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

# shellcheck source=deploy/bin/ledger-bank-key
LEDGER_BANK_KEY_LIB_ONLY=1 source "$BANK_KEY"
set +e
set -uo pipefail

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

# --- validation ---------------------------------------------------------------
check "a lowercase UUID is a valid application id" "0" \
  "$(bank_key_valid_application_id '0b9e2c7a-4d1f-4a6e-9c3b-5e8f1a2d7c64' && echo 0 || echo 1)"
for bad_id in '' 'not-a-uuid' '0B9E2C7A-4D1F-4A6E-9C3B-5E8F1A2D7C64' \
  '0b9e2c7a-4d1f-4a6e-9c3b-5e8f1a2d7c64 ' '0b9e2c7a-4d1f-4a6e-9c3b-5e8f1a2d7c6' \
  '0b9e2c7a4d1f4a6e9c3b5e8f1a2d7c64'; do
  check "application id [${bad_id}] is rejected" "1" \
    "$(bank_key_valid_application_id "$bad_id" && echo 0 || echo 1)"
done

check "the https callback URL is a valid redirect URL" "0" \
  "$(bank_key_valid_redirect_url 'https://host.example.com/api/v1/bank/callback' && echo 0 || echo 1)"
for bad_url in '' 'http://host.example.com/api/v1/bank/callback' \
  'https://host.example.com/api/v1/bank/callback?x=1' \
  'https://host.example.com/other' \
  'https://host.example.com/api/v1/bank/callback ' \
  'https://host .example.com/api/v1/bank/callback' \
  'https://host.example.com/api/v1/bank/callback/'; do
  check "redirect URL [${bad_url}] is rejected" "1" \
    "$(bank_key_valid_redirect_url "$bad_url" && echo 0 || echo 1)"
done

# --- env file edits -----------------------------------------------------------
ENV_FILE="${WORKDIR}/ledger.env"
printf 'ASPNETCORE_ENVIRONMENT=Production\nFirst__Key=one\nSecond__Key=two\n' > "$ENV_FILE"
bank_key_set_env_value "$ENV_FILE" Second__Key 'a/b+c=&d'
bank_key_set_env_value "$ENV_FILE" Third__Key three
check "a present key is replaced in place and a missing key is appended" \
  $'ASPNETCORE_ENVIRONMENT=Production\nFirst__Key=one\nSecond__Key=a/b+c=&d\nThird__Key=three' \
  "$(cat "$ENV_FILE")"
BEFORE="$(cat "$ENV_FILE")"
bank_key_set_env_value "$ENV_FILE" Second__Key 'a/b+c=&d'
bank_key_set_env_value "$ENV_FILE" Third__Key three
check "setting the same values again changes nothing" "$BEFORE" "$(cat "$ENV_FILE")"
check "the env file is mode 640" "640" "$(stat -c '%a' "$ENV_FILE")"
check "no temporary files are left beside the env file" "1" "$(find "$WORKDIR" -maxdepth 1 -name '.ledger.env.*' | wc -l | awk '{print ($1 == 0) ? 1 : 0}')"

# --- key generation -----------------------------------------------------------
KEY_DIR="${WORKDIR}/etc-ledger"
GEN_ENV="${WORKDIR}/generated.env"
mkdir -p "$KEY_DIR"
printf 'ASPNETCORE_ENVIRONMENT=Production\n' > "$GEN_ENV"

GEN_OUT="$(bank_key_generate "$KEY_DIR" "$GEN_ENV" 2>&1)"
check "generate succeeds" "0" "$?"

KEY_FILE="${KEY_DIR}/enablebanking-key.pem"
check "the key file exists" "1" "$([ -f "$KEY_FILE" ] && echo 1 || echo 0)"
check "the certificate exists" "1" "$([ -f "${KEY_DIR}/enablebanking-cert.pem" ] && echo 1 || echo 0)"
check "the public key exists" "1" "$([ -f "${KEY_DIR}/enablebanking-public.pem" ] && echo 1 || echo 0)"
check "the key file is mode 640" "640" "$(stat -c '%a' "$KEY_FILE")"
check "the certificate is mode 644" "644" "$(stat -c '%a' "${KEY_DIR}/enablebanking-cert.pem")"

ENCRYPTED_HEADER="-----BEGIN ENCRYPTED ""PRIVATE KEY-----"
check "the key is an encrypted PKCS#8 key" "$ENCRYPTED_HEADER" "$(head -n1 "$KEY_FILE")"
check "the key is 4096 bits" "1" \
  "$(openssl x509 -in "${KEY_DIR}/enablebanking-cert.pem" -noout -text | grep -c 'Public-Key: (4096 bit)')"

read_env_value() {
  grep -E "^$1=" "$2" | head -n1 | cut -d= -f2-
}

BANK_TEST_PASSWORD="$(read_env_value EnableBanking__PrivateKeyPassword "$GEN_ENV")"
export BANK_TEST_PASSWORD
check "the env file holds the key path" "$KEY_FILE" "$(read_env_value EnableBanking__PrivateKeyPath "$GEN_ENV")"
check "the env file holds a password of at least 32 characters" "1" \
  "$([ "${#BANK_TEST_PASSWORD}" -ge 32 ] && echo 1 || echo 0)"
check "the key opens with the password from the env file" "0" \
  "$(openssl pkey -in "$KEY_FILE" -passin env:BANK_TEST_PASSWORD -noout > /dev/null 2>&1 && echo 0 || echo 1)"
check "the key does not open without a password" "1" \
  "$(openssl pkey -in "$KEY_FILE" -passin pass: -noout > /dev/null 2>&1 && echo 0 || echo 1)"
check "the key does not open with a wrong password" "1" \
  "$(openssl pkey -in "$KEY_FILE" -passin pass:wrong-password -noout > /dev/null 2>&1 && echo 0 || echo 1)"
check "the other env lines are untouched" "ASPNETCORE_ENVIRONMENT=Production" "$(head -n1 "$GEN_ENV")"

LEAKED=0
if grep -qF -- "$BANK_TEST_PASSWORD" <<< "$GEN_OUT"; then
  LEAKED=1
fi
check "nothing generate prints contains the password" "0" "$LEAKED"
check "generate prints the certificate" "1" "$(grep -c -- '-----BEGIN CERTIFICATE-----' <<< "$GEN_OUT")"
check "generate prints the public key" "1" "$(grep -c -- '-----BEGIN PUBLIC KEY-----' <<< "$GEN_OUT")"
check "generate prints no private key material" "0" "$(grep -c 'PRIVATE KEY' <<< "$GEN_OUT")"

KEY_BEFORE="$(cksum < "$KEY_FILE")"
ENV_BEFORE="$(cat "$GEN_ENV")"
SECOND_OUT="$(bank_key_generate "$KEY_DIR" "$GEN_ENV" 2>&1)"
check "a second generate refuses" "1" "$?"
check "a second generate leaves the key unchanged" "$KEY_BEFORE" "$(cksum < "$KEY_FILE")"
check "a second generate leaves the env file unchanged" "$ENV_BEFORE" "$(cat "$GEN_ENV")"
check "the refusal says the key already exists" "1" "$(grep -c 'already exists' <<< "$SECOND_OUT")"
check "only the key, certificate and public key are in the key directory" "3" "$(find "$KEY_DIR" -mindepth 1 | wc -l | tr -d ' ')"

# --- failure handling ---------------------------------------------------------
FAIL_TMP="${WORKDIR}/fail-tmp"
FAIL_DIR="${WORKDIR}/fail-etc-ledger"
FAIL_ENV="${WORKDIR}/fail-generated.env"
mkdir -p "$FAIL_TMP" "$FAIL_DIR"
printf 'ASPNETCORE_ENVIRONMENT=Production\n' > "$FAIL_ENV"
UMASK_BEFORE="$(umask)"

FAIL_OUT="$(TMPDIR="$FAIL_TMP" bank_key_generate "$FAIL_DIR" "${WORKDIR}/no-such-dir/ledger.env" 2>&1)"
check "generate fails when the env file cannot be written" "1" "$?"
check "a failed generate installs no key" "0" "$(find "$FAIL_DIR" -mindepth 1 | wc -l | tr -d ' ')"
check "a failed generate leaves no work directory" "0" "$(find "$FAIL_TMP" -mindepth 1 | wc -l | tr -d ' ')"
check "a failed generate tells the operator nothing was installed" "1" "$(grep -c 'No bank key was installed' <<< "$FAIL_OUT")"

FAIL_PASSWORD_LEFT="$(
  TMPDIR="$FAIL_TMP" bank_key_generate "$FAIL_DIR" "${WORKDIR}/no-such-dir/ledger.env" > /dev/null 2>&1
  printf '%s' "${LEDGER_BANK_KEY_PASSWORD:-}"
)"
check "a failed generate does not leave the password exported" "" "$FAIL_PASSWORD_LEFT"

TMPDIR="$FAIL_TMP" bank_key_generate "$FAIL_DIR" "${WORKDIR}/no-such-dir/ledger.env" > /dev/null 2>&1
check "a failed generate restores the umask" "$UMASK_BEFORE" "$(umask)"

TMPDIR="$FAIL_TMP" bank_key_generate "$FAIL_DIR" "$FAIL_ENV" > /dev/null 2>&1
check "generate succeeds after a failed attempt without manual cleanup" "0" "$?"
check "the retry installs the key" "1" "$([ -f "${FAIL_DIR}/enablebanking-key.pem" ] && echo 1 || echo 0)"
check "the retry leaves no work directory" "0" "$(find "$FAIL_TMP" -mindepth 1 | wc -l | tr -d ' ')"
check "the retry leaves no staged files" "0" "$(find "$FAIL_DIR" -name '*.new' | wc -l | tr -d ' ')"
check "the retry records the key path" "${FAIL_DIR}/enablebanking-key.pem" "$(read_env_value EnableBanking__PrivateKeyPath "$FAIL_ENV")"

# --- configure ----------------------------------------------------------------
CONFIGURED_ID='0b9e2c7a-4d1f-4a6e-9c3b-5e8f1a2d7c64'
CONFIGURED_URL='https://host.example.com/api/v1/bank/callback'
(bank_key_configure "$GEN_ENV" --application-id "$CONFIGURED_ID" --redirect-url "$CONFIGURED_URL") > /dev/null 2>&1
check "configure succeeds with valid values" "0" "$?"
check "configure writes the application id" "$CONFIGURED_ID" "$(read_env_value EnableBanking__ApplicationId "$GEN_ENV")"
check "configure selects the provider" "EnableBanking" "$(read_env_value Ingestion__Provider "$GEN_ENV")"
check "configure writes the callback URL" "$CONFIGURED_URL" "$(read_env_value BankLink__RedirectUrl "$GEN_ENV")"
check "configure keeps the generated key path" "$KEY_FILE" "$(read_env_value EnableBanking__PrivateKeyPath "$GEN_ENV")"

CONFIGURED_BEFORE="$(cat "$GEN_ENV")"
(bank_key_configure "$GEN_ENV" --application-id 'not-a-uuid' --redirect-url "$CONFIGURED_URL") > /dev/null 2>&1
check "configure rejects an invalid application id" "1" "$?"
(bank_key_configure "$GEN_ENV" --application-id "$CONFIGURED_ID" --redirect-url 'http://host.example.com/api/v1/bank/callback') > /dev/null 2>&1
check "configure rejects an insecure callback URL" "1" "$?"
(bank_key_configure "$GEN_ENV" --application-id "$CONFIGURED_ID") > /dev/null 2>&1
check "configure requires both values" "2" "$?"
check "rejected configure calls leave the env file unchanged" "$CONFIGURED_BEFORE" "$(cat "$GEN_ENV")"

echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "All checks passed."
  exit 0
else
  echo "${FAILURES} check(s) failed."
  exit 1
fi
