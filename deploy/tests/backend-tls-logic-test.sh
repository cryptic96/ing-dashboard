#!/usr/bin/env bash
# Proves the shared helper that creates the certificate the application
# presents to the reverse proxy: it creates a pair with the right modes,
# owner, key type, name and lifetime when none exists, never touches an
# existing pair, refuses to continue on a half or mismatched pair, and never
# prints the private key. Also proves provisioning and the installer both go
# through that helper. Needs no root, network, systemd or database: the files
# are created in a temporary directory owned by the current user.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT
# shellcheck source=deploy/tests/lib/host-guard.sh
source "${SCRIPT_DIR}/lib/host-guard.sh"
host_guard_install "$WORKDIR"

# shellcheck source=deploy/lib/backend-tls.sh
source "${REPO_ROOT}/deploy/lib/backend-tls.sh"

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

export LEDGER_BACKEND_TLS_OWNER
LEDGER_BACKEND_TLS_OWNER="$(id -un)"
export LEDGER_BACKEND_TLS_GROUP
LEDGER_BACKEND_TLS_GROUP="$(id -gn)"

# Points the helper at a new empty directory and names it in DIR.
new_dir() {
  DIR="$(mktemp -d -p "$WORKDIR")"
  export LEDGER_BACKEND_TLS_DIR="$DIR"
}

# --- creation when absent ----------------------------------------------------

new_dir
OUTPUT="$(ledger_ensure_backend_tls 2>&1)"
STATUS=$?
CRT="${DIR}/backend-tls.crt"
KEY="${DIR}/backend-tls.key"

check "creating the pair succeeds" "0" "$STATUS"
check "the public certificate is world-readable (mode 644)" "644" "$(stat -c '%a' "$CRT")"
check "the private key is not readable beyond owner and group (mode 640)" "640" "$(stat -c '%a' "$KEY")"
check "the certificate belongs to the configured owner and group" \
  "${LEDGER_BACKEND_TLS_OWNER}:${LEDGER_BACKEND_TLS_GROUP}" "$(stat -c '%U:%G' "$CRT")"
check "the key belongs to the configured owner and group" \
  "${LEDGER_BACKEND_TLS_OWNER}:${LEDGER_BACKEND_TLS_GROUP}" "$(stat -c '%U:%G' "$KEY")"
check "no temporary file is left behind in the directory" "2" "$(find "$DIR" -mindepth 1 | wc -l | tr -d ' ')"

CERT_TEXT="$(openssl x509 -in "$CRT" -noout -text)"
check "the key is an EC P-256 key" "1" "$(grep -c 'NIST CURVE: P-256' <<< "$CERT_TEXT")"
check "the subject is the fixed internal name" "1" "$(grep -c 'Subject: CN *= *ledger-backend$' <<< "$CERT_TEXT")"
check "the only subject alternative name is the fixed internal name" "DNS:ledger-backend" \
  "$(grep -A1 'Subject Alternative Name' <<< "$CERT_TEXT" | tail -n1 | tr -d ' ')"
check "the certificate is for server authentication" "1" "$(grep -c 'TLS Web Server Authentication' <<< "$CERT_TEXT")"
check "the certificate names no IP address" "0" "$(grep -c 'IP Address' <<< "$CERT_TEXT")"
check "the certificate is valid for ten years" "0" \
  "$(openssl x509 -in "$CRT" -noout -checkend $((3649 * 86400)) > /dev/null 2>&1; echo $?)"
check "the certificate is valid now" "0" "$(openssl x509 -in "$CRT" -noout -checkend 0 > /dev/null 2>&1; echo $?)"
check "the pair belongs together" "0" "$(ledger_backend_tls_pair_matches "$CRT" "$KEY"; echo $?)"

check "the fingerprint is logged" "1" "$(grep -c 'SHA-256 [0-9A-F:]\{95\}' <<< "$OUTPUT")"
check "the private key is never printed" "0" "$(grep -c -- 'PRIVATE KEY' <<< "$OUTPUT")"
KEY_BODY="$(grep -v -- '-----' "$KEY" | head -n1)"
check "no line of the private key appears in the output" "0" "$(grep -cF -- "$KEY_BODY" <<< "$OUTPUT")"

check "a client pinning the certificate accepts it under the internal name" "0" \
  "$(openssl verify -CAfile "$CRT" -purpose sslserver -verify_hostname ledger-backend "$CRT" > /dev/null 2>&1; echo $?)"
check "a client pinning the certificate refuses it under any other name" "refused" \
  "$(openssl verify -CAfile "$CRT" -purpose sslserver -verify_hostname other-name "$CRT" > /dev/null 2>&1 && echo accepted || echo refused)"

# --- an existing pair is never touched -----------------------------------------

BEFORE_FP="$(ledger_backend_tls_fingerprint "$CRT")"
BEFORE_KEY_SUM="$(sha256sum "$KEY" | cut -d' ' -f1)"
OUTPUT="$(ledger_ensure_backend_tls 2>&1)"
STATUS=$?
check "running again succeeds" "0" "$STATUS"
check "running again keeps the certificate" "$BEFORE_FP" "$(ledger_backend_tls_fingerprint "$CRT")"
check "running again keeps the key byte for byte" "$BEFORE_KEY_SUM" "$(sha256sum "$KEY" | cut -d' ' -f1)"
check "running again logs the unchanged fingerprint" "1" "$(grep -c "SHA-256 ${BEFORE_FP}" <<< "$OUTPUT")"
chmod 600 "$KEY"
chmod 600 "$CRT"
ledger_ensure_backend_tls > /dev/null 2>&1
check "an existing pair keeps whatever modes it has (never rewritten)" "600" "$(stat -c '%a' "$KEY")"

# --- a half pair or a mismatched pair stops the caller -------------------------

new_dir
touch "${DIR}/backend-tls.key"
( ledger_ensure_backend_tls > /dev/null 2>&1 )
check "a lone key stops the caller" "1" "$?"
check "a lone key is left as it is" "0" "$(find "$DIR" -name 'backend-tls.crt' | wc -l | tr -d ' ')"

new_dir
touch "${DIR}/backend-tls.crt"
( ledger_ensure_backend_tls > /dev/null 2>&1 )
check "a lone certificate stops the caller" "1" "$?"
check "a lone certificate is left as it is" "0" "$(find "$DIR" -name 'backend-tls.key' | wc -l | tr -d ' ')"

new_dir
FIRST="$DIR"
ledger_ensure_backend_tls > /dev/null 2>&1
new_dir
SECOND="$DIR"
ledger_ensure_backend_tls > /dev/null 2>&1
cp "${FIRST}/backend-tls.key" "${SECOND}/backend-tls.key"
MISMATCH_OUTPUT="$( ( ledger_ensure_backend_tls ) 2>&1 )"
check "a certificate with someone else's key stops the caller" "1" "$( ( ledger_ensure_backend_tls > /dev/null 2>&1 ); echo $? )"
check "the mismatch report names the files but no key material" "0" "$(grep -c -- 'PRIVATE KEY' <<< "$MISMATCH_OUTPUT")"

# --- provisioning and the installer both use the helper ---------------------------

ACCOUNTS="${REPO_ROOT}/deploy/provision.d/20-accounts.sh"

# Counts the lines of FILE that are exactly TEXT.
count_exact_lines() {
  grep -cxF -- "$2" "$1" || true
}

# Prints the number of the first line of FILE that is exactly TEXT.
first_line_number() {
  grep -nxF -- "$2" "$1" | head -n1 | cut -d: -f1
}

ENSURE_CALL="  ledger_ensure_backend_tls"
ENV_GUARD="  if [[ ! -f \"\$DP_ENV_PATH\" ]]; then"
check "provisioning sources the shared helper" "1" \
  "$(count_exact_lines "$ACCOUNTS" "source \"\${DEPLOY_DIR}/lib/backend-tls.sh\"")"
check "provisioning calls the helper once" "1" "$(count_exact_lines "$ACCOUNTS" "$ENSURE_CALL")"
check "provisioning creates the pair before it renders the env file" "yes" \
  "$([ "$(first_line_number "$ACCOUNTS" "$ENSURE_CALL")" -lt "$(first_line_number "$ACCOUNTS" "$ENV_GUARD")" ] && echo yes || echo no)"
check "the installer script loads the helper" "1" \
  "$(count_exact_lines "${REPO_ROOT}/deploy/bin/ledger-deploy" "source \"\${LIB_DIR}/backend-tls.sh\"")"
check "the installer's provisioning step ships the helper with the other libraries" "1" \
  "$(count_exact_lines "${REPO_ROOT}/deploy/provision.d/40-services.sh" "  for lib in \"\${DEPLOY_DIR}\"/lib/*.sh; do")"

check "no check in this file reached the real systemctl" "" "$(host_guard_calls)"

if [ "$FAILURES" -ne 0 ]; then
  printf '%d check(s) failed\n' "$FAILURES" >&2
  exit 1
fi

printf 'All checks passed\n'
