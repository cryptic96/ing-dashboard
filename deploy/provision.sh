#!/usr/bin/env bash
###
### Idempotent, re-runnable provisioning orchestrator for the ledger LXC.
### Run as root. Loads deploy/versions.env and /etc/ledger/provision.conf,
### then runs each numbered module in deploy/provision.d/ in order (or a
### single module with --only), stopping at the first failure.
###
### Works both from a git checkout and from an installed release tree
### (e.g. /opt/ledger/current/deploy/provision.sh) — deploy/versions.env
### and deploy/provision.d/ always ship as siblings of this script.
###
### Set LEDGER_PROVISION_LIB_ONLY=1 before sourcing this file to load only
### the shared functions below (provision_log, provision_die,
### provision_load_conf, provision_version_ge, provision_key_fingerprint)
### without requiring root, network access or package installation. Every
### module under provision.d/ does exactly this to reuse the same
### functions.
###
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

###
### --- shared library functions -------------------------------------------
###

provision_log() {
  echo "[provision] $*"
}

provision_die() {
  echo "[provision] ERROR: $*" >&2
  exit 1
}

###
### Parses KEY=VALUE lines from stdin-less file "$1" into shell variables,
### restricted to the allow-list array named by "$2" (a nameref target),
### with optional double-quoting and full-line "#" comments. Rejects any
### key not on the allow-list, any line containing a backtick or "$(" (no
### command substitution is ever permitted), and any line that doesn't
### parse as KEY=VALUE. Never evaluates a value.
###
_provision_parse_kv_file() {
  local file="$1" allowed_array_name="$2"
  local -n allowed_keys="$allowed_array_name"
  local line key value is_allowed k

  while IFS= read -r line || [[ -n "$line" ]]; do
    [[ -z "$line" ]] && continue
    [[ "$line" =~ ^[[:space:]]*# ]] && continue

    # shellcheck disable=SC2016 # intentional: matching literal backtick/$( text, not expanding it
    if [[ "$line" == *'`'* || "$line" == *'$('* ]]; then
      provision_die "${file}: line contains command substitution, refusing: ${line}"
    fi

    if [[ "$line" =~ ^([A-Z][A-Z0-9_]*)=\"([^\"]*)\"[[:space:]]*$ ]]; then
      key="${BASH_REMATCH[1]}"
      value="${BASH_REMATCH[2]}"
    elif [[ "$line" =~ ^([A-Z][A-Z0-9_]*)=(.*)$ ]]; then
      key="${BASH_REMATCH[1]}"
      value="${BASH_REMATCH[2]}"
    else
      provision_die "${file}: malformed line: ${line}"
    fi

    is_allowed=0
    for k in "${allowed_keys[@]}"; do
      if [[ "$k" == "$key" ]]; then
        is_allowed=1
        break
      fi
    done
    if [[ "$is_allowed" -ne 1 ]]; then
      provision_die "${file}: unknown key '${key}'"
    fi

    printf -v "$key" '%s' "$value"
    # shellcheck disable=SC2163 # intentional: $key holds the name of the variable to export
    export "$key"
  done <"$file"
}

###
### Loads /etc/ledger/provision.conf (or any file with the same shape),
### refusing to read it unless it is owned by root and not writable by
### group or other, then parses it with _provision_parse_kv_file against
### PROVISION_CONF_ALLOWED_KEYS.
###
# shellcheck disable=SC2034 # read via the nameref in _provision_parse_kv_file
PROVISION_CONF_ALLOWED_KEYS=(
  LEDGER_TRAEFIK_IP
  LEDGER_ADMIN_SSH_SOURCES
  LEDGER_GRAFANA_DOMAIN
  LEDGER_API_DOMAIN
  LEDGER_SMTP_RELAY
  LEDGER_MAIL_FROM
  LEDGER_ALERT_EMAIL
  LEDGER_GITHUB_REPO
)

provision_load_conf() {
  local file="$1" owner mode group_bit world_bit

  [[ -f "$file" ]] || provision_die "config file not found: ${file}"

  owner="$(stat -c '%U' "$file" 2>/dev/null || stat -f '%Su' "$file" 2>/dev/null || true)"
  if [[ "$owner" != "root" ]]; then
    provision_die "config file ${file} must be owned by root (owned by ${owner:-unknown})"
  fi

  mode="$(stat -c '%a' "$file" 2>/dev/null || stat -f '%Lp' "$file" 2>/dev/null || true)"
  group_bit="${mode: -2:1}"
  world_bit="${mode: -1:1}"
  case "$group_bit" in
    2 | 3 | 6 | 7) provision_die "config file ${file} must not be group-writable (mode ${mode})" ;;
  esac
  case "$world_bit" in
    2 | 3 | 6 | 7) provision_die "config file ${file} must not be world-writable (mode ${mode})" ;;
  esac

  _provision_parse_kv_file "$file" PROVISION_CONF_ALLOWED_KEYS
}

# shellcheck disable=SC2034 # read via the nameref in _provision_parse_kv_file
VERSIONS_ENV_ALLOWED_KEYS=(
  PG_MAJOR
  PGDG_KEY_URL
  PGDG_KEY_FINGERPRINT
  DOTNET_RUNTIME_PACKAGE
  GRAFANA_VERSION_PIN
  GRAFANA_KEY_URL
  GRAFANA_KEY_FINGERPRINT
  GRAFANA_SERVICE_USER
  PROMETHEUS_VERSION
  PROMETHEUS_SHA256
  GH_CLI_KEY_URL
  GH_CLI_KEY_FINGERPRINT
  GH_CLI_MIN_VERSION
)

###
### Loads deploy/versions.env (not a secrets file, no ownership check) into
### shell variables restricted to VERSIONS_ENV_ALLOWED_KEYS.
###
provision_load_versions() {
  local file="$1"
  [[ -f "$file" ]] || provision_die "versions file not found: ${file}"
  _provision_parse_kv_file "$file" VERSIONS_ENV_ALLOWED_KEYS
}

###
### Returns success (0) if version "$1" is greater than or equal to
### version "$2", using dpkg's own version-ordering rules rather than a
### hand-rolled comparator.
###
provision_version_ge() {
  dpkg --compare-versions "$1" ge "$2"
}

###
### Reads gpg --batch --with-colons --show-keys output from stdin and
### prints the fingerprint of the single non-expired, non-revoked primary
### key it describes. Fails if there is zero or more than one such key —
### an expired transitional key alongside a current one is treated as
### exactly one active primary, matching how apt itself trusts a keyring.
###
provision_key_fingerprint() {
  local line validity pending=0 fp="" count=0

  while IFS= read -r line; do
    case "$line" in
      pub:*)
        validity="$(cut -d: -f2 <<<"$line")"
        if [[ "$validity" != "e" && "$validity" != "r" ]]; then
          pending=1
        else
          pending=0
        fi
        ;;
      fpr:*)
        if [[ "$pending" -eq 1 ]]; then
          count=$((count + 1))
          fp="$(cut -d: -f10 <<<"$line")"
        fi
        pending=0
        ;;
    esac
  done

  if [[ "$count" -ne 1 ]]; then
    provision_log "expected exactly one active primary key, found ${count}"
    return 1
  fi
  printf '%s' "$fp"
}

###
### --- orchestrator (skipped entirely in library mode) --------------------
###

if [[ "${LEDGER_PROVISION_LIB_ONLY:-0}" != "1" ]]; then
  if [[ "$(id -u)" -ne 0 ]]; then
    provision_die "must be run as root"
  fi

  ONLY_MODULE=""
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --only)
        ONLY_MODULE="${2:-}"
        shift 2
        ;;
      --only=*)
        ONLY_MODULE="${1#--only=}"
        shift
        ;;
      *)
        provision_die "unknown argument: $1"
        ;;
    esac
  done

  provision_load_versions "${SCRIPT_DIR}/versions.env"

  mkdir -p /etc/ledger
  chmod 750 /etc/ledger

  PROVISION_CONF="/etc/ledger/provision.conf"
  if [[ ! -f "$PROVISION_CONF" ]]; then
    install -o root -g root -m 600 "${SCRIPT_DIR}/provision.conf.example" "$PROVISION_CONF"
    provision_log "Created ${PROVISION_CONF} from provision.conf.example."
    provision_log "Fill in the placeholder values, then re-run provision.sh."
    exit 0
  fi

  provision_load_conf "$PROVISION_CONF"

  MODULES_DIR="${SCRIPT_DIR}/provision.d"
  MODULE_FILES=()
  if [[ -n "$ONLY_MODULE" ]]; then
    candidate="$ONLY_MODULE"
    [[ "$candidate" == *.sh ]] || candidate="${candidate}.sh"
    if [[ -f "${MODULES_DIR}/${candidate}" ]]; then
      MODULE_FILES=("${MODULES_DIR}/${candidate}")
    else
      provision_die "module not found: ${ONLY_MODULE}"
    fi
  else
    for module in "${MODULES_DIR}"/[0-9][0-9]-*.sh; do
      [[ -e "$module" ]] || continue
      MODULE_FILES+=("$module")
    done
  fi

  if [[ "${#MODULE_FILES[@]}" -eq 0 ]]; then
    provision_die "no provisioning modules found in ${MODULES_DIR}"
  fi

  for module in "${MODULE_FILES[@]}"; do
    provision_log "Running $(basename "$module")"
    bash "$module"
  done

  provision_log "Provisioning complete."
fi
