#!/usr/bin/env bash
###
### Idempotent module: renames Grafana's built-in admin account away from
### its default login and creates the household's Viewer accounts through
### Grafana's own HTTP API. Requires an interactive terminal (accounts are
### typed, never scripted or stored in provision.conf); otherwise it skips
### with a notice. Re-runs only once, unless explicitly re-selected with
### `provision.sh --only 60-grafana-accounts`.
###
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=deploy/provision.sh
LEDGER_PROVISION_LIB_ONLY=1 source "${SCRIPT_DIR}/../provision.sh"

GRAFANA_URL="http://127.0.0.1:3000"
GRAFANA_HOST="${LEDGER_GRAFANA_DOMAIN:-}"
STATE_MARKER="${LEDGER_GRAFANA_ACCOUNTS_STATE_MARKER:-/var/lib/ledger-deploy/state/grafana-accounts.done}"
LOGIN_PATTERN='^[a-z][a-z0-9._-]{2,31}$'

###
### Escapes a value for embedding inside a double-quoted curl config-file
### value (backslash and double-quote only).
###
grafana_config_escape() {
  local s="$1"
  s="${s//\\/\\\\}"
  s="${s//\"/\\\"}"
  printf '%s' "$s"
}

###
### Runs one Grafana HTTP API call. Credentials and, when given, a JSON
### body travel to curl entirely through a config stream piped on standard
### input (curl --config -); neither ever appears on the command line, in
### `ps`, or in any log this module writes. Grafana enforces its domain, so
### the request carries the configured Grafana hostname as its Host header;
### any response other than 2xx (including a redirect) is a failure.
###
grafana_api() {
  local method="$1" path="$2" user="$3" password="$4" body="${5:-}"
  local response status rc=0
  response="$({
    printf 'request = "%s"\n' "$method"
    printf 'user = "%s:%s"\n' "$(grafana_config_escape "$user")" "$(grafana_config_escape "$password")"
    printf 'header = "Content-Type: application/json"\n'
    printf 'header = "Host: %s"\n' "$(grafana_config_escape "$GRAFANA_HOST")"
    printf 'silent\n'
    printf 'show-error\n'
    printf 'fail\n'
    printf 'write-out = "\\n%%{http_code}"\n'
    if [[ -n "$body" ]]; then
      printf 'data = "%s"\n' "$(grafana_config_escape "$body")"
    fi
    printf 'url = "%s%s"\n' "$GRAFANA_URL" "$path"
  } | curl --config -)" || rc=$?
  if [[ "$rc" -ne 0 ]]; then
    return "$rc"
  fi
  status="${response##*$'\n'}"
  response="${response%$'\n'*}"
  if [[ ! "$status" =~ ^2[0-9][0-9]$ ]]; then
    echo "Grafana answered HTTP ${status} for ${method} ${path}" >&2
    return 22
  fi
  printf '%s' "$response"
}

###
### Prompts twice for a password (hidden input) and re-prompts until both
### entries match and the password is at least 20 characters. Prints
### nothing but prompts; the result is left in the OUT_VAR nameref target.
###
prompt_password() {
  local label="$1"
  local -n out_var="$2"
  local pw1 pw2
  while true; do
    read -r -s -p "${label}: " pw1
    echo
    read -r -s -p "Confirm ${label}: " pw2
    echo
    if [[ "$pw1" != "$pw2" ]]; then
      echo "Passwords did not match, try again." >&2
      continue
    fi
    if [[ "${#pw1}" -lt 20 ]]; then
      echo "Password must be at least 20 characters, try again." >&2
      continue
    fi
    # shellcheck disable=SC2034 # written via the nameref, read by the caller
    out_var="$pw1"
    unset pw1 pw2
    break
  done
}

if [[ "${LEDGER_PROVISION_LIB_ONLY:-0}" != "1" ]]; then
  if [[ ! -t 0 ]]; then
    provision_log "No interactive terminal; skipping Grafana account setup. Re-run 'provision.sh --only 60-grafana-accounts' from a terminal to create the Grafana accounts."
    exit 0
  fi

  if [[ -f "$STATE_MARKER" && "${PROVISION_ONLY_MODULE:-}" != "60-grafana-accounts" ]]; then
    provision_log "Grafana accounts are already set up (${STATE_MARKER} exists); skipping. Re-run with --only 60-grafana-accounts to redo it."
    exit 0
  fi

  if [[ -z "$GRAFANA_HOST" ]]; then
    provision_die "LEDGER_GRAFANA_DOMAIN is not set in provision.conf; Grafana answers only on its own hostname"
  fi

  provision_log "Waiting for Grafana to become ready"
  ready=0
  for _ in $(seq 1 60); do
    if curl --fail --silent --show-error --max-time 3 "${GRAFANA_URL}/api/health" > /dev/null 2>&1; then
      ready=1
      break
    fi
    sleep 2
  done
  if [[ "$ready" -ne 1 ]]; then
    provision_die "Grafana never became ready at ${GRAFANA_URL}/api/health"
  fi

  ADMIN_USER="admin"
  ADMIN_PASSWORD="admin"

  if grafana_api GET "/api/org" "$ADMIN_USER" "$ADMIN_PASSWORD" > /dev/null 2>&1; then
    provision_log "The default admin credentials still work; replacing them."
  else
    provision_log "The default admin credentials no longer work; enter the current admin credentials to continue."
    read -r -p "Current admin login: " ADMIN_USER
    read -r -s -p "Current admin password: " ADMIN_PASSWORD
    echo
    if ! grafana_api GET "/api/org" "$ADMIN_USER" "$ADMIN_PASSWORD" > /dev/null 2>&1; then
      provision_die "could not authenticate to Grafana with the supplied admin credentials (after 5 failures Grafana blocks the login for 5 minutes)"
    fi
  fi

  # The password is replaced before the login is renamed, and each step is
  # also taken on a re-run whenever it is still at its default, so an
  # interrupted run never leaves the default password in place.
  if [[ "$ADMIN_PASSWORD" == "admin" ]]; then
    new_admin_password=""
    prompt_password "New admin password (at least 20 characters)" new_admin_password
    # Passwords reach jq through its environment, never its argument list,
    # which any local user could read from the process table.
    password_body="$(LEDGER_GRAFANA_PASSWORD="$new_admin_password" jq -nc '{password: env.LEDGER_GRAFANA_PASSWORD}')"
    grafana_api PUT "/api/admin/users/1/password" "$ADMIN_USER" "$ADMIN_PASSWORD" "$password_body" > /dev/null \
      || provision_die "failed to set the new admin password"
    ADMIN_PASSWORD="$new_admin_password"
    unset new_admin_password password_body
    provision_log "Admin password replaced. Store it in the password manager now."
  fi

  if [[ "$ADMIN_USER" == "admin" ]]; then
    read -r -p "New admin login (not 'admin'): " new_admin_login
    if [[ "$new_admin_login" == "admin" ]] || ! [[ "$new_admin_login" =~ $LOGIN_PATTERN ]]; then
      provision_die "invalid admin login: use 3 to 32 lowercase letters, digits, dots, dashes or underscores, starting with a letter, and not admin (the value is not repeated here in case a password was pasted)"
    fi
    rename_body="$(jq -nc --arg login "$new_admin_login" --arg email "${new_admin_login}@example.invalid" \
      '{login: $login, email: $email}')"
    grafana_api PUT "/api/users/1" "$ADMIN_USER" "$ADMIN_PASSWORD" "$rename_body" > /dev/null \
      || provision_die "failed to rename the built-in admin account"
    ADMIN_USER="$new_admin_login"
    unset rename_body
    provision_log "Admin account renamed. Store the new admin login in the password manager now."
  fi

  if ! grafana_api GET "/api/org" "$ADMIN_USER" "$ADMIN_PASSWORD" > /dev/null 2>&1; then
    provision_die "could not authenticate to Grafana with the new admin credentials"
  fi

  viewer_count_default=2
  read -r -p "Number of Viewer accounts to create [${viewer_count_default}]: " viewer_count
  viewer_count="${viewer_count:-$viewer_count_default}"
  if ! [[ "$viewer_count" =~ ^[0-9]+$ ]] || [[ "$viewer_count" -lt 1 ]]; then
    provision_die "invalid viewer account count: ${viewer_count}"
  fi

  existing_users="$(grafana_api GET "/api/org/users" "$ADMIN_USER" "$ADMIN_PASSWORD")" \
    || provision_die "failed to list existing Grafana users"

  for ((viewer_index = 1; viewer_index <= viewer_count; viewer_index++)); do
    read -r -p "Viewer #${viewer_index} login: " viewer_login
    read -r -p "Viewer #${viewer_index} display name: " viewer_name
    if ! [[ "$viewer_login" =~ $LOGIN_PATTERN ]]; then
      provision_die "invalid viewer login: use 3 to 32 lowercase letters, digits, dots, dashes or underscores, starting with a letter (the value is not repeated here in case a password was pasted)"
    fi

    if printf '%s' "$existing_users" | jq -e --arg login "$viewer_login" \
      'map(select(.login == $login)) | length > 0' > /dev/null 2>&1; then
      provision_log "Viewer '${viewer_login}' already exists; leaving the existing account untouched."
      continue
    fi

    viewer_password=""
    prompt_password "Viewer #${viewer_index} password (at least 20 characters)" viewer_password

    create_body="$(LEDGER_GRAFANA_PASSWORD="$viewer_password" jq -nc --arg name "$viewer_name" --arg login "$viewer_login" \
      '{name: $name, login: $login, password: env.LEDGER_GRAFANA_PASSWORD, OrgId: 1}')"
    grafana_api POST "/api/admin/users" "$ADMIN_USER" "$ADMIN_PASSWORD" "$create_body" > /dev/null \
      || provision_die "failed to create viewer account ${viewer_login}"
    unset viewer_password create_body

    new_user_id="$(grafana_api GET "/api/users/lookup?loginOrEmail=${viewer_login}" "$ADMIN_USER" "$ADMIN_PASSWORD" \
      | jq -r '.id')" \
      || provision_die "failed to look up the newly created viewer account ${viewer_login}"

    grafana_api PATCH "/api/org/users/${new_user_id}" "$ADMIN_USER" "$ADMIN_PASSWORD" '{"role":"Viewer"}' > /dev/null \
      || provision_die "failed to set the Viewer role for ${viewer_login}"

    provision_log "Viewer account '${viewer_login}' created with the Viewer role."
  done

  provision_log "Verifying every non-admin user has the Viewer role"
  org_users="$(grafana_api GET "/api/org/users" "$ADMIN_USER" "$ADMIN_PASSWORD")" \
    || provision_die "failed to list organisation users for verification"

  if printf '%s' "$org_users" | jq -e --arg admin "$ADMIN_USER" \
    '[.[] | select(.login != $admin) | select(.role != "Viewer")] | length > 0' > /dev/null; then
    provision_die "at least one non-admin Grafana user does not have the Viewer role"
  fi

  mkdir -p "$(dirname "$STATE_MARKER")"
  touch "$STATE_MARKER"

  unset ADMIN_PASSWORD

  provision_log "Grafana accounts are set up. Store the admin login and password in the password manager."
fi
