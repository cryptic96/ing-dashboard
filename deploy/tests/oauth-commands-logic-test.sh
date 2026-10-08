#!/usr/bin/env bash
# Proves the behaviour of ledger-login and ledger-grants without touching the
# host: both refuse anyone but root, refuse names, codes and grant ids outside
# their patterns, never put a password or a one-time code on systemd-run's
# argument list, and hand a piped password or code over on standard input. systemd-run and id are
# stubbed; the stub records the argument list and standard input separately.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
LOGIN="${DEPLOY_DIR}/bin/ledger-login"
GRANTS="${DEPLOY_DIR}/bin/ledger-grants"

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
ARGV_LOG="${WORKDIR}/argv.log"
STDIN_LOG="${WORKDIR}/stdin.log"
mkdir -p "$STUB_BIN"

cat > "${STUB_BIN}/id" <<'EOF_STUB'
#!/usr/bin/env bash
echo "${STUB_UID:-0}"
EOF_STUB

cat > "${STUB_BIN}/systemd-run" <<'EOF_STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$ARGV_LOG"
if [ -t 0 ]; then
  echo "<terminal>" >> "$STDIN_LOG"
else
  cat >> "$STDIN_LOG"
fi
exit 0
EOF_STUB
chmod +x "${STUB_BIN}/id" "${STUB_BIN}/systemd-run"
export PATH="${STUB_BIN}:${PATH}"
export ARGV_LOG STDIN_LOG

reset_logs() {
  : > "$ARGV_LOG"
  : > "$STDIN_LOG"
}

calls() {
  wc -l < "$ARGV_LOG" | tr -d ' '
}

run_status() {
  "$@" > /dev/null 2>&1 < /dev/null
  echo "$?"
}

PASSWORD='correct horse battery staple 42'
GRANT_ID='0b9e2c7a-4d1f-4a6e-9c3b-5e8f1a2d7c64'

# --- refuse anyone but root ---------------------------------------------------
reset_logs
for wrapper in "$LOGIN" "$GRANTS"; do
  check "$(basename "$wrapper") list refuses a non-root caller" "1" \
    "$(STUB_UID=1000 run_status "$wrapper" list)"
done
check "a non-root caller never reaches systemd-run" "0" "$(calls)"

# --- ledger-login: argument checks ----------------------------------------------
reset_logs
for bad_name in 'Bad Name!' 'a' 'x;y' 'Operator' '1abc' 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' '-abc'; do
  for subcommand in create set-password reset-totp remove; do
    check "login ${subcommand} refuses the name [${bad_name}]" "1" \
      "$(run_status "$LOGIN" "$subcommand" "$bad_name")"
  done
  check "login confirm-totp refuses the name [${bad_name}]" "1" \
    "$(run_status "$LOGIN" confirm-totp "$bad_name")"
done

check "login without arguments is a usage error" "2" "$(run_status "$LOGIN")"
check "login with an unknown subcommand is a usage error" "2" "$(run_status "$LOGIN" explode)"
check "login create without a name is a usage error" "2" "$(run_status "$LOGIN" create)"
check "login list with an extra argument is a usage error" "2" "$(run_status "$LOGIN" list extra)"
check "login confirm-totp without a name is a usage error" "2" "$(run_status "$LOGIN" confirm-totp)"
check "login confirm-totp with the code on the command line is a usage error" "2" \
  "$(run_status "$LOGIN" confirm-totp household-admin 123456)"
check "no refused login call reaches systemd-run" "0" "$(calls)"

# --- ledger-login: forwarding -----------------------------------------------------
reset_logs
printf '%s\n' "$PASSWORD" | "$LOGIN" create household-admin > /dev/null 2>&1
check "login create with a piped password succeeds" "0" "$?"
check "login create reaches systemd-run once" "1" "$(calls)"
check "login create passes the subcommand and the name" "1" \
  "$(grep -c -- ' login create household-admin$' "$ARGV_LOG")"
check "login create never puts the password on the argument list" "0" \
  "$(grep -c -F -- "$PASSWORD" "$ARGV_LOG")"
check "login create forwards the piped password on standard input" "$PASSWORD" "$(cat "$STDIN_LOG")"

reset_logs
printf '%s\n' "$PASSWORD" | "$LOGIN" set-password household-admin > /dev/null 2>&1
check "login set-password with a piped password succeeds" "0" "$?"
check "login set-password never puts the password on the argument list" "0" \
  "$(grep -c -F -- "$PASSWORD" "$ARGV_LOG")"
check "login set-password forwards the piped password on standard input" "$PASSWORD" "$(cat "$STDIN_LOG")"

reset_logs
printf '%s\n' 123456 | "$LOGIN" confirm-totp household-admin > /dev/null 2>&1
check "login confirm-totp with a piped code succeeds" "0" "$?"
check "login confirm-totp passes only the subcommand and the name" "1" \
  "$(grep -c -- ' login confirm-totp household-admin$' "$ARGV_LOG")"
check "login confirm-totp never puts the code on the argument list" "0" "$(grep -c -F -- 123456 "$ARGV_LOG")"
check "login confirm-totp forwards the piped code on standard input" "123456" "$(cat "$STDIN_LOG")"

reset_logs
"$LOGIN" reset-totp household-admin > /dev/null 2>&1 < /dev/null
"$LOGIN" remove household-admin > /dev/null 2>&1 < /dev/null
"$LOGIN" list > /dev/null 2>&1 < /dev/null
check "login reset-totp, remove and list each reach systemd-run" "3" "$(calls)"
check "login reset-totp passes the name" "1" "$(grep -c -- ' login reset-totp household-admin$' "$ARGV_LOG")"
check "login remove passes the name" "1" "$(grep -c -- ' login remove household-admin$' "$ARGV_LOG")"
check "login list passes no name" "1" "$(grep -c -- ' login list$' "$ARGV_LOG")"

# --- ledger-login: the terminal prompt ----------------------------------------------
if command -v script > /dev/null 2>&1; then
  reset_logs
  printf '%s\n%s\n' "$PASSWORD" "$PASSWORD" \
    | script -qec "'${LOGIN}' create household-admin" /dev/null > /dev/null 2>&1
  check "a matching prompted password reaches systemd-run once" "1" "$(calls)"
  check "a prompted password never lands on the argument list" "0" "$(grep -c -F -- "$PASSWORD" "$ARGV_LOG")"
  check "a prompted password is passed on standard input" "$PASSWORD" "$(cat "$STDIN_LOG")"

  reset_logs
  printf '%s\n%s\n' "$PASSWORD" 'a different password entirely' \
    | script -qec "'${LOGIN}' create household-admin" /dev/null > /dev/null 2>&1
  check "two different prompted passwords never reach systemd-run" "0" "$(calls)"

  reset_logs
  printf '%s\n' 654321 | script -qec "'${LOGIN}' confirm-totp household-admin" /dev/null > /dev/null 2>&1
  check "a prompted code reaches systemd-run once" "1" "$(calls)"
  check "a prompted code never lands on the argument list" "0" "$(grep -c -F -- 654321 "$ARGV_LOG")"
  check "a prompted code is passed on standard input" "654321" "$(cat "$STDIN_LOG")"

  for bad_code in '12345' '1234567' 'abcdef' '12345a' '12 456' ''; do
    reset_logs
    printf '%s\n' "$bad_code" | script -qec "'${LOGIN}' confirm-totp household-admin" /dev/null > /dev/null 2>&1
    check "a prompted code [${bad_code}] outside six digits never reaches systemd-run" "0" "$(calls)"
  done
else
  echo "SKIP: the prompt checks need the script utility"
fi

# --- ledger-grants -------------------------------------------------------------------
reset_logs
for bad_id in 'abc' '0B9E2C7A-4D1F-4A6E-9C3B-5E8F1A2D7C64' "${GRANT_ID};id" "${GRANT_ID}0" '../../etc/passwd'; do
  check "grants revoke refuses the id [${bad_id}]" "1" "$(run_status "$GRANTS" revoke "$bad_id")"
done
check "grants without arguments is a usage error" "2" "$(run_status "$GRANTS")"
check "grants with an unknown subcommand is a usage error" "2" "$(run_status "$GRANTS" explode)"
check "grants revoke without an id is a usage error" "2" "$(run_status "$GRANTS" revoke)"
check "grants revoke-all with an extra argument is a usage error" "2" "$(run_status "$GRANTS" revoke-all extra)"
check "no refused grants call reaches systemd-run" "0" "$(calls)"

reset_logs
"$GRANTS" list > /dev/null 2>&1 < /dev/null
"$GRANTS" revoke-all > /dev/null 2>&1 < /dev/null
"$GRANTS" revoke "$GRANT_ID" > /dev/null 2>&1 < /dev/null
check "grants list, revoke-all and revoke each reach systemd-run" "3" "$(calls)"
check "grants list passes only the subcommand" "1" "$(grep -c -- ' grants list$' "$ARGV_LOG")"
check "grants revoke-all passes only the subcommand" "1" "$(grep -c -- ' grants revoke-all$' "$ARGV_LOG")"
check "grants revoke passes the grant id" "1" "$(grep -c -- " grants revoke ${GRANT_ID}\$" "$ARGV_LOG")"

echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "All checks passed."
  exit 0
else
  echo "${FAILURES} check(s) failed."
  exit 1
fi
