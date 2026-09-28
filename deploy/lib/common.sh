#!/usr/bin/env bash
# Shared logging, safe config loading, textfile metrics and email helpers used
# by the deploy tooling. Sourced, never executed directly.

if [ -n "${LEDGER_COMMON_SH_LOADED:-}" ]; then
  return 0
fi
LEDGER_COMMON_SH_LOADED=1

LEDGER_CONF_ALLOWED_KEYS=(
  LEDGER_GITHUB_REPO
  LEDGER_SIGNER_WORKFLOW
  LEDGER_NOTIFY_EMAIL
  LEDGER_KEEP_RELEASES
  LEDGER_HEALTH_TIMEOUT_SECONDS
  LEDGER_OPS_URL
  LEDGER_TEXTFILE_DIR
)

# Writes a timestamped line to stderr. journald captures stderr for services
# invoked by systemd; interactive runs simply see it on the terminal.
ledger_log() {
  printf '%s %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$*" >&2
}

# Logs an error-prefixed message and exits non-zero.
ledger_die() {
  ledger_log "ERROR: $*"
  exit 1
}

# Loads KEY=VALUE pairs from a configuration file into global shell variables,
# without ever sourcing the file. Refuses a file that is not owned by the
# expected privileged user, or that is group- or world-writable. Only keys on
# the fixed allow-list are read; every other line is silently ignored. Values
# may be optionally wrapped in single or double quotes, which are stripped.
#
# Under a relocated test root (LEDGER_DEPLOY_ROOT set) the expected owner is
# the current effective user rather than root, since the test root stands in
# for the privileged installation root and is never itself run as root.
ledger_load_conf() {
  local conf_file="$1"

  if [ ! -f "$conf_file" ]; then
    ledger_die "configuration file not found: $conf_file"
  fi

  local expected_uid=0
  if [ -n "${LEDGER_DEPLOY_ROOT:-}" ]; then
    expected_uid="$(id -u)"
  fi

  local owner_uid
  owner_uid="$(stat -c '%u' "$conf_file")"
  if [ "$owner_uid" != "$expected_uid" ]; then
    ledger_die "configuration file $conf_file has an unexpected owner"
  fi

  local perm group_digit other_digit
  perm="$(stat -c '%a' "$conf_file")"
  group_digit="${perm:1:1}"
  other_digit="${perm:2:1}"
  if (( (10#$group_digit & 2) != 0 )) || (( 10#$other_digit != 0 )); then
    ledger_die "configuration file $conf_file must not be group- or world-writable"
  fi

  local line key value allowed candidate
  while IFS= read -r line || [ -n "$line" ]; do
    [[ "$line" =~ ^([A-Z][A-Z0-9_]*)=(.*)$ ]] || continue
    key="${BASH_REMATCH[1]}"
    value="${BASH_REMATCH[2]}"

    allowed=0
    for candidate in "${LEDGER_CONF_ALLOWED_KEYS[@]}"; do
      if [ "$candidate" = "$key" ]; then
        allowed=1
        break
      fi
    done
    [ "$allowed" -eq 1 ] || continue

    if [[ "$value" =~ ^\"(.*)\"$ ]]; then
      value="${BASH_REMATCH[1]}"
    elif [[ "$value" =~ ^\'(.*)\'$ ]]; then
      value="${BASH_REMATCH[1]}"
    fi

    printf -v "$key" '%s' "$value"
  done < "$conf_file"
}

# Merges the given metric sample lines into DIR/NAME.prom: any existing
# sample for the same metric name (matched before the first '{' or
# whitespace) is replaced, every other existing sample is preserved, and the
# file is written atomically (temp file in the same directory, then
# rename). CONTENT may include '# HELP'/'# TYPE' comment lines; comments
# already on disk for metrics not present in CONTENT are dropped, since
# Prometheus text exposition format treats HELP/TYPE as optional metadata.
ledger_write_textfile_metrics() {
  local name="$1"
  local content="$2"
  local dir="${LEDGER_TEXTFILE_DIR:-/var/lib/prometheus/node-exporter}"
  local file="${dir}/${name}.prom"

  mkdir -p "$dir"
  local tmp
  tmp="$(mktemp "${dir}/.${name}.XXXXXX")"

  local incoming_names=" "
  local content_line metric_name
  while IFS= read -r content_line; do
    case "$content_line" in
      ""|\#*) continue ;;
    esac
    metric_name="${content_line%%[ {]*}"
    incoming_names="${incoming_names}${metric_name} "
  done <<< "$content"

  if [ -f "$file" ]; then
    local existing_line
    while IFS= read -r existing_line; do
      case "$existing_line" in
        ""|\#*) continue ;;
      esac
      metric_name="${existing_line%%[ {]*}"
      case "$incoming_names" in
        *" ${metric_name} "*) continue ;;
      esac
      printf '%s\n' "$existing_line" >> "$tmp"
    done < "$file"
  fi

  printf '%s\n' "$content" >> "$tmp"
  mv -f "$tmp" "$file"
}

# Sends a plain-text notification email through msmtp when it is available
# and a recipient is configured. Email failure never changes the deploy
# result: this function always returns success, only logging a warning.
ledger_notify_email() {
  local subject="$1"
  local body="$2"
  local to="${LEDGER_NOTIFY_EMAIL:-}"

  if [ -z "$to" ]; then
    ledger_log "no notification recipient configured, skipping email"
    return 0
  fi

  if ! command -v msmtp >/dev/null 2>&1; then
    ledger_log "msmtp not available, skipping email notification"
    return 0
  fi

  {
    printf 'To: %s\n' "$to"
    printf 'Subject: [ledger-deploy] %s\n' "$subject"
    printf '\n'
    printf '%s\n' "$body"
  } | msmtp -t || ledger_log "sending notification email failed"

  return 0
}

# Compares two MAJOR.MINOR.PATCH version strings numerically. Succeeds
# (returns 0) when A is strictly greater than B.
ledger_semver_gt() {
  local a="$1" b="$2"
  local a_major a_minor a_patch b_major b_minor b_patch

  IFS='.' read -r a_major a_minor a_patch <<< "$a"
  IFS='.' read -r b_major b_minor b_patch <<< "$b"

  if (( 10#$a_major != 10#$b_major )); then
    (( 10#$a_major > 10#$b_major ))
    return
  fi
  if (( 10#$a_minor != 10#$b_minor )); then
    (( 10#$a_minor > 10#$b_minor ))
    return
  fi
  (( 10#$a_patch > 10#$b_patch ))
}
