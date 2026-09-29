#!/usr/bin/env bash
# Drives the real 60-grafana-accounts.sh module through a pseudo-terminal
# against a stubbed Grafana HTTP API (a fake curl on PATH that parses the
# `curl --config -` stream the module sends) to prove its interactive
# account-bootstrap behaviour: password-before-rename ordering, the "admin"
# rename refusal, the default-credentials-broken recovery path, Viewer role
# assignment/skip/verification, that typed passwords never reach any
# process's argument list, that every call carries the configured Grafana
# Host header, that a non-2xx or redirect response is treated as failure,
# and that a non-interactive run skips cleanly.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
MODULE="${REPO_ROOT}/deploy/provision.d/60-grafana-accounts.sh"

FAILURES=0

check() {
  local description="$1"
  local expected="$2"
  local actual="$3"
  if [ "$actual" = "$expected" ]; then
    printf 'PASS: %s\n' "$description"
  else
    printf 'FAIL: %s (expected [%s], got [%s])\n' "$description" "$expected" "$actual"
    FAILURES=$((FAILURES + 1))
  fi
}

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT
# shellcheck source=deploy/tests/lib/host-guard.sh
source "${SCRIPT_DIR}/lib/host-guard.sh"
mkdir -p "${WORKDIR}/host-guard"
host_guard_install "${WORKDIR}/host-guard"

STUB_BIN="${WORKDIR}/stub-bin"
mkdir -p "$STUB_BIN"

# --- stub curl: parses the module's `curl --config -` stream and plays the
# part of Grafana's HTTP API against a small JSON/flat-file "database" kept
# in $GRAFANA_STUB_STATE. Every invocation's argv is logged verbatim (to
# prove secrets never travel that way); the config stream (which does carry
# the password) is logged separately to prove it DOES reach curl, just not
# via argv. ------------------------------------------------------------
cat > "${STUB_BIN}/curl" <<'CURL_STUB'
#!/usr/bin/env bash
set -uo pipefail
STATE="${GRAFANA_STUB_STATE:?GRAFANA_STUB_STATE not set}"
mkdir -p "$STATE"
printf '%s\n' "$*" >> "${STATE}/argv.log"

is_config=0
for a in "$@"; do
  [[ "$a" == "--config" ]] && is_config=1
done

if [[ "$is_config" -ne 1 ]]; then
  # Plain readiness probe: curl --fail --silent --show-error --max-time 3 <url>
  if printf '%s\n' "$*" | grep -q '/api/health'; then
    exit 0
  fi
  exit 1
fi

cfg="$(cat)"
method="$(printf '%s\n' "$cfg" | sed -n 's/^request = "\(.*\)"$/\1/p')"
userpass="$(printf '%s\n' "$cfg" | sed -n 's/^user = "\(.*\)"$/\1/p')"
user="${userpass%%:*}"
pass="${userpass#*:}"
host="$(printf '%s\n' "$cfg" | sed -n 's/^header = "Host: \(.*\)"$/\1/p')"
url="$(printf '%s\n' "$cfg" | sed -n 's/^url = "\(.*\)"$/\1/p')"
data_raw="$(printf '%s\n' "$cfg" | sed -n 's/^data = "\(.*\)"$/\1/p')"
data="${data_raw//\\\"/\"}"
path="${url#http://127.0.0.1:3000}"

printf 'CALL %s %s user=%s host=%s data=%s\n' "$method" "$path" "$user" "$host" "$data" >> "${STATE}/calls.log"
printf '%s\n%s\n' "$pass" "$data" >> "${STATE}/stdin-secrets.log"

respond() {
  local body="$1" code="$2"
  if [[ "$code" -ge 400 ]]; then
    exit 22
  fi
  printf '%s\n%s' "$body" "$code"
  exit 0
}

key="$(printf '%s' "$path" | tr '/?=&' '____')"
force_file="${STATE}/force-status-${method}-${key}"
if [[ -f "$force_file" ]]; then
  respond '{}' "$(cat "$force_file")"
fi

admin_user="$(cat "${STATE}/admin-user" 2>/dev/null || echo admin)"
admin_pass="$(cat "${STATE}/admin-pass" 2>/dev/null || echo admin)"
if [[ "$user" != "$admin_user" || "$pass" != "$admin_pass" ]]; then
  respond '{"message":"Invalid username or password"}' 401
fi

case "${method} ${path}" in
  "GET /api/org")
    respond '{"id":1}' 200
    ;;
  "PUT /api/admin/users/1/password")
    newpw="$(printf '%s' "$data" | jq -r '.password')"
    printf '%s' "$newpw" > "${STATE}/admin-pass"
    respond '{"message":"password updated"}' 200
    ;;
  "PUT /api/users/1")
    newlogin="$(printf '%s' "$data" | jq -r '.login')"
    printf '%s' "$newlogin" > "${STATE}/admin-user"
    jq --arg login "$newlogin" 'map(if .userId==1 then .login=$login else . end)' \
      "${STATE}/org-users.json" > "${STATE}/org-users.json.tmp" && mv "${STATE}/org-users.json.tmp" "${STATE}/org-users.json"
    respond '{"message":"user updated"}' 200
    ;;
  "GET /api/org/users")
    respond "$(cat "${STATE}/org-users.json")" 200
    ;;
  "POST /api/admin/users")
    newlogin="$(printf '%s' "$data" | jq -r '.login')"
    id="$(cat "${STATE}/next-id")"
    echo $((id + 1)) > "${STATE}/next-id"
    jq --arg login "$newlogin" --argjson id "$id" \
      '. + [{"userId":$id,"login":$login,"role":"Editor"}]' \
      "${STATE}/org-users.json" > "${STATE}/org-users.json.tmp" && mv "${STATE}/org-users.json.tmp" "${STATE}/org-users.json"
    respond "$(jq -nc --argjson id "$id" '{id:$id}')" 200
    ;;
  "GET /api/users/lookup"*)
    loginq="${path#*loginOrEmail=}"
    id="$(jq -r --arg l "$loginq" '.[] | select(.login==$l) | .userId' "${STATE}/org-users.json")"
    respond "$(jq -nc --argjson id "$id" '{id:$id}')" 200
    ;;
  "PATCH /api/org/users/"*)
    uid="${path#/api/org/users/}"
    role="$(printf '%s' "$data" | jq -r '.role')"
    jq --argjson uid "$uid" --arg role "$role" \
      'map(if .userId==$uid then .role=$role else . end)' \
      "${STATE}/org-users.json" > "${STATE}/org-users.json.tmp" && mv "${STATE}/org-users.json.tmp" "${STATE}/org-users.json"
    respond '{"message":"role updated"}' 200
    ;;
  *)
    respond '{"message":"not found"}' 404
    ;;
esac
CURL_STUB
chmod +x "${STUB_BIN}/curl"

# --- stub jq: records the module's own jq argv (to prove no password is
# ever passed to jq as an argument either), then execs the real jq. -------
cat > "${STUB_BIN}/jq" <<'JQ_STUB'
#!/usr/bin/env bash
STATE="${GRAFANA_STUB_STATE:?GRAFANA_STUB_STATE not set}"
mkdir -p "$STATE"
printf 'jq %s\n' "$*" >> "${STATE}/argv.log"
exec /usr/bin/jq "$@"
JQ_STUB
chmod +x "${STUB_BIN}/jq"

init_state() {
  local state="$1" admin_user="$2" admin_pass="$3" users_json="$4"
  mkdir -p "$state"
  printf '%s' "$admin_user" > "${state}/admin-user"
  printf '%s' "$admin_pass" > "${state}/admin-pass"
  printf '%s' "$users_json" > "${state}/org-users.json"
  echo 2 > "${state}/next-id"
  : > "${state}/argv.log"
  : > "${state}/calls.log"
  : > "${state}/stdin-secrets.log"
}

force_status() {
  local state="$1" method="$2" path="$3" code="$4"
  local key
  key="$(printf '%s' "$path" | tr '/?=&' '____')"
  printf '%s' "$code" > "${state}/force-status-${method}-${key}"
}

# Runs the module under a pty via `script`, feeding it the given scripted
# input, and returns the module's own exit status (script -e).
run_module() {
  local state="$1" outfile="$2" input="$3"
  set +e
  (
    export GRAFANA_STUB_STATE="$state"
    export LEDGER_GRAFANA_DOMAIN="ledger.example.org"
    export LEDGER_GRAFANA_ACCOUNTS_STATE_MARKER="${state}/grafana-accounts.done"
    # The stubs must apply only to this module run, hence the subshell.
    # shellcheck disable=SC2030
    export PATH="${STUB_BIN}:${PATH}"
    printf '%s' "$input" | script -qec "bash '${MODULE}'" "$outfile"
  )
  local status=$?
  set -e
  return "$status"
}

ADMIN_NEW_PW="CorrectHorseBatteryStaple1!"
ALICE_PW="AliceViewerPassphrase123!"
BOB_PW="BobViewerPassphrase456!"
CAROL_PW="CarolViewerPassphrase789!"
RECOVERY_NEW_PW="RecoveredAdminPassphrase99!"

# =========================================================================
# Scenario 1: default admin/admin still works. Password must be replaced
# before the login is renamed; the new credentials must then be used.
# =========================================================================
STATE1="${WORKDIR}/state1"
init_state "$STATE1" admin admin '[{"userId":1,"login":"admin","role":"Admin"}]'
OUT1="${WORKDIR}/out1.typescript"
INPUT1="$(printf '%s\n%s\n%s\n2\naliceviewer\nAlice Viewer\n%s\n%s\nbobviewer\nBob Viewer\n%s\n%s\n' \
  "$ADMIN_NEW_PW" "$ADMIN_NEW_PW" "householdadmin" "$ALICE_PW" "$ALICE_PW" "$BOB_PW" "$BOB_PW")"$'\n'
STATUS1=0
run_module "$STATE1" "$OUT1" "$INPUT1" || STATUS1=$?

CALLS1="${STATE1}/calls.log"
PW_LINE="$(grep -n 'PUT /api/admin/users/1/password' "$CALLS1" | head -1 | cut -d: -f1)"
RENAME_LINE="$(grep -n 'PUT /api/users/1 ' "$CALLS1" | head -1 | cut -d: -f1)"
check "the admin password is replaced before the login is renamed" "1" \
  "$([[ -n "$PW_LINE" && -n "$RENAME_LINE" && "$PW_LINE" -lt "$RENAME_LINE" ]] && echo 1 || echo 0)"

check "the module authenticates with the new login and password afterwards" "1" \
  "$([[ "$(grep -c "user=householdadmin" "$CALLS1")" -ge 1 ]] && echo 1 || echo 0)"

check "both viewer accounts are created" "2" "$(grep -c 'POST /api/admin/users' "$CALLS1")"
check "the PATCH for the first new viewer sets the Viewer role" "1" \
  "$(grep -cF 'PATCH /api/org/users/2 user=householdadmin host=ledger.example.org data={"role":"Viewer"}' "$CALLS1")"
check "the PATCH for the second new viewer sets the Viewer role" "1" \
  "$(grep -cF 'PATCH /api/org/users/3 user=householdadmin host=ledger.example.org data={"role":"Viewer"}' "$CALLS1")"

check "every recorded call carries the configured Grafana Host header" "0" \
  "$(grep -vc 'host=ledger.example.org' "$CALLS1")"

check "the module reaches final role verification (reachable steps all succeeded)" "1" \
  "$(grep -qF 'Verifying every non-admin user has the Viewer role' "$OUT1" && echo 1 || echo 0)"

ALL_ARGV="${STATE1}/argv.log"
check "the new admin password never appears in any process argv" "0" \
  "$(grep -cF "$ADMIN_NEW_PW" "$ALL_ARGV" || true)"
check "viewer passwords never appear in any process argv" "0" \
  "$(( $(grep -cF "$ALICE_PW" "$ALL_ARGV" || true) + $(grep -cF "$BOB_PW" "$ALL_ARGV" || true) ))"

STDIN_SECRETS="${STATE1}/stdin-secrets.log"
check "the new admin password does reach Grafana via the curl config stream" "1" \
  "$(grep -cF "$ADMIN_NEW_PW" "$STDIN_SECRETS" | awk '{print ($1>=1)?1:0}')"
ALICE_IN_STDIN="$(grep -cF "$ALICE_PW" "$STDIN_SECRETS" || true)"
BOB_IN_STDIN="$(grep -cF "$BOB_PW" "$STDIN_SECRETS" || true)"
check "viewer passwords do reach Grafana via the curl config stream" "1" \
  "$([[ "$ALICE_IN_STDIN" -ge 1 && "$BOB_IN_STDIN" -ge 1 ]] && echo 1 || echo 0)"

check "a complete run exits successfully" "0" "$STATUS1"
check "a complete run records that the accounts are set up" "1" \
  "$([[ -f "${STATE1}/grafana-accounts.done" ]] && echo 1 || echo 0)"

# A later full provisioning run finds the marker and leaves Grafana alone.
CALLS_BEFORE_RERUN="$(wc -l < "${STATE1}/calls.log")"
OUT1B="${WORKDIR}/out1b.typescript"
STATUS1B=0
run_module "$STATE1" "$OUT1B" $'\n' || STATUS1B=$?
check "a re-run after setup exits successfully" "0" "$STATUS1B"
check "a re-run after setup reports the accounts as already set up" "1" \
  "$(grep -qF 'already set up' "$OUT1B" && echo 1 || echo 0)"
check "a re-run after setup makes no Grafana API call" "$CALLS_BEFORE_RERUN" \
  "$(wc -l < "${STATE1}/calls.log")"

# =========================================================================
# Scenario 2: recovery path. The default admin/admin login no longer
# works, but the *current* password is still "admin" -> the module must
# prompt for current credentials and still replace the password.
# =========================================================================
STATE2="${WORKDIR}/state2"
init_state "$STATE2" curradmin admin '[{"userId":1,"login":"curradmin","role":"Admin"}]'
OUT2="${WORKDIR}/out2.typescript"
INPUT2="$(printf 'curradmin\nadmin\n%s\n%s\n1\ncarolviewer\nCarol Viewer\n%s\n%s\n' \
  "$RECOVERY_NEW_PW" "$RECOVERY_NEW_PW" "$CAROL_PW" "$CAROL_PW")"$'\n'
STATUS2=0
run_module "$STATE2" "$OUT2" "$INPUT2" || STATUS2=$?

CALLS2="${STATE2}/calls.log"
check "the recovery prompt for current credentials is shown" "1" \
  "$(grep -qF 'Current admin login' "$OUT2" && echo 1 || echo 0)"
check "the recovery path still replaces the password" "1" \
  "$(grep -c 'PUT /api/admin/users/1/password' "$CALLS2")"
check "the recovery path does not force a rename (login was already custom)" "0" \
  "$(grep -c 'PUT /api/users/1 ' "$CALLS2")"
check "the recovered admin password never appears in any argv" "0" \
  "$(grep -cF "$RECOVERY_NEW_PW" "${STATE2}/argv.log" || true)"
check "the recovery run exits successfully" "0" "$STATUS2"

# =========================================================================
# Scenario 3: renaming the built-in admin login to "admin" is refused,
# before any rename API call is made.
# =========================================================================
STATE3="${WORKDIR}/state3"
init_state "$STATE3" admin admin '[{"userId":1,"login":"admin","role":"Admin"}]'
OUT3="${WORKDIR}/out3.typescript"
INPUT3="$(printf '%s\n%s\nadmin\n' "$ADMIN_NEW_PW" "$ADMIN_NEW_PW")"$'\n'
STATUS3=0
run_module "$STATE3" "$OUT3" "$INPUT3" || STATUS3=$?
check "renaming admin to 'admin' fails" "1" "$([[ "$STATUS3" -ne 0 ]] && echo 1 || echo 0)"
check "no rename API call is made when the new login is refused" "0" \
  "$(grep -c 'PUT /api/users/1 ' "${STATE3}/calls.log")"
check "the password replacement did still happen before the refused rename" "1" \
  "$(grep -c 'PUT /api/admin/users/1/password' "${STATE3}/calls.log")"

# =========================================================================
# Scenario 4: an existing viewer is left untouched, and the final
# verification fails when a non-admin user does not hold the Viewer role.
# =========================================================================
STATE4="${WORKDIR}/state4"
init_state "$STATE4" siteadmin "ExistingSiteAdminPass123!" \
  '[{"userId":1,"login":"siteadmin","role":"Admin"},{"userId":2,"login":"legacyeditor","role":"Editor"}]'
echo 3 > "${STATE4}/next-id"
OUT4="${WORKDIR}/out4.typescript"
INPUT4="$(printf 'siteadmin\nExistingSiteAdminPass123!\n1\nlegacyeditor\nLegacy Editor\n')"$'\n'
STATUS4=0
run_module "$STATE4" "$OUT4" "$INPUT4" || STATUS4=$?

check "an already-existing viewer login is left untouched" "1" \
  "$(grep -qF "already exists; leaving the existing account untouched" "$OUT4" && echo 1 || echo 0)"
check "no create/patch call is made for the untouched existing viewer" "0" \
  "$(grep -c 'POST /api/admin/users\|PATCH /api/org/users/2 ' "${STATE4}/calls.log")"
check "final verification fails when a non-admin user is not a Viewer" "1" \
  "$([[ "$STATUS4" -ne 0 ]] && echo 1 || echo 0)"
check "the verification failure names the Viewer-role requirement" "1" \
  "$(grep -qF "does not have the Viewer role" "$OUT4" && echo 1 || echo 0)"

# =========================================================================
# Scenario 5: a non-2xx response is treated as failure, not success.
# =========================================================================
STATE5="${WORKDIR}/state5"
init_state "$STATE5" admin admin '[{"userId":1,"login":"admin","role":"Admin"}]'
force_status "$STATE5" PUT "/api/admin/users/1/password" 500
OUT5="${WORKDIR}/out5.typescript"
INPUT5="$(printf '%s\n%s\n' "$ADMIN_NEW_PW" "$ADMIN_NEW_PW")"$'\n'
STATUS5=0
run_module "$STATE5" "$OUT5" "$INPUT5" || STATUS5=$?
check "a 500 response makes the module fail rather than report success" "1" \
  "$([[ "$STATUS5" -ne 0 ]] && echo 1 || echo 0)"
check "a 500 response is reported as a failed password change" "1" \
  "$(grep -qF "failed to set the new admin password" "$OUT5" && echo 1 || echo 0)"
check "no rename is attempted after the password change failed" "0" \
  "$(grep -c 'PUT /api/users/1 ' "${STATE5}/calls.log")"

# =========================================================================
# Scenario 6: a redirect response is treated as failure, not success
# (curl's own --fail does not trigger on 3xx, so this exercises the
# module's own explicit status-code check).
# =========================================================================
STATE6="${WORKDIR}/state6"
init_state "$STATE6" admin admin '[{"userId":1,"login":"admin","role":"Admin"}]'
force_status "$STATE6" PUT "/api/admin/users/1/password" 302
OUT6="${WORKDIR}/out6.typescript"
INPUT6="$(printf '%s\n%s\n' "$ADMIN_NEW_PW" "$ADMIN_NEW_PW")"$'\n'
STATUS6=0
run_module "$STATE6" "$OUT6" "$INPUT6" || STATUS6=$?
check "a redirect response makes the module fail rather than report success" "1" \
  "$([[ "$STATUS6" -ne 0 ]] && echo 1 || echo 0)"
check "the redirect is reported by its HTTP status" "1" \
  "$(grep -qF "Grafana answered HTTP 302" "$OUT6" && echo 1 || echo 0)"

# =========================================================================
# Scenario 7: with no terminal on stdin, the module skips cleanly and
# makes no API calls at all.
# =========================================================================
STATE7="${WORKDIR}/state7"
init_state "$STATE7" admin admin '[{"userId":1,"login":"admin","role":"Admin"}]'
STATUS7=0
# PATH is read here in the parent shell, deliberately unaffected by the
# run_module subshells above.
# shellcheck disable=SC2031
NOTTY_OUT="$(
  GRAFANA_STUB_STATE="$STATE7" \
  LEDGER_GRAFANA_DOMAIN="ledger.example.org" \
  PATH="${STUB_BIN}:${PATH}" \
  bash "$MODULE" < /dev/null 2>&1
)" || STATUS7=$?
check "a non-interactive run exits cleanly" "0" "$STATUS7"
check "a non-interactive run explains that it skipped" "1" \
  "$(printf '%s' "$NOTTY_OUT" | grep -qF "No interactive terminal" && echo 1 || echo 0)"
check "a non-interactive run makes no Grafana API calls" "0" \
  "$(wc -l < "${STATE7}/argv.log" | tr -d ' ')"

# --- host-guard sanity: nothing this module runs should ever have
# invoked systemctl/pkexec. ------------------------------------------------
check "no systemctl/pkexec call was made by any scenario" "" "$(host_guard_calls)"

echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "All checks passed."
  exit 0
else
  echo "${FAILURES} check(s) failed."
  exit 1
fi
