#!/usr/bin/env bash
###
### Idempotent module: installs the platform's scripts, systemd units and
### non-secret server-side configuration, installs the initial Grafana and
### Prometheus provisioning, enables automatic security updates, and
### brings up every platform service (starting the app itself only once a
### release has actually been installed). Never touches the application
### environment file or the Data Protection certificate — those belong to
### 20-accounts.sh and the root installer alone.
###
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
# shellcheck source=deploy/provision.sh
LEDGER_PROVISION_LIB_ONLY=1 source "${SCRIPT_DIR}/../provision.sh"

###
### Renders /etc/msmtprc content from the given SMTP relay ("host:port"),
### From address and STARTTLS setting ("on" or "off"). Splits the
### "host:port" pair itself, since msmtp needs separate host/port
### directives.
###
services_render_msmtprc() {
  local relay="$1" mail_from="$2" starttls="$3" output="$4"
  local host port tls_line1 tls_line2

  provision_validate_hostport "$relay" \
    || provision_die "LEDGER_SMTP_RELAY is not a valid host:port: ${relay}"

  host="${relay%:*}"
  port="${relay##*:}"

  case "$starttls" in
    on) tls_line1="tls on"; tls_line2="tls_starttls on" ;;
    off) tls_line1="tls off"; tls_line2="" ;;
    *) provision_die "LEDGER_SMTP_STARTTLS must be 'on' or 'off', got '${starttls}'" ;;
  esac

  provision_render_template "${DEPLOY_DIR}/msmtp/msmtprc.in" "$output" \
    "LEDGER_SMTP_HOST=${host}" \
    "LEDGER_SMTP_PORT=${port}" \
    "LEDGER_MAIL_FROM=${mail_from}" \
    "LEDGER_SMTP_TLS_LINE1=${tls_line1}" \
    "LEDGER_SMTP_TLS_LINE2=${tls_line2}"
}

###
### Creates the msmtp log file when it is absent, owned by root with group
### adm and mode 640 when run as root. The installer unit runs with a
### read-only /var/log and can only write this one file, so it has to exist
### before the first deploy notification is sent. An existing file, and the
### mail history in it, is never touched.
###
services_ensure_msmtp_log() {
  local log_path="$1"

  if [[ -e "$log_path" ]]; then
    return 0
  fi
  if [[ "$(id -u)" -eq 0 ]]; then
    install -m 640 -o root -g adm /dev/null "$log_path"
  else
    install -m 640 /dev/null "$log_path"
  fi
}

###
### Renders OUTPUT from an example env-style file at SOURCE, replacing the
### value of each key named in a following "KEY=VALUE" override argument
### and leaving every other line (comments, and any key without an
### override) exactly as it appears in SOURCE. Refuses to write anything
### if an override names a key that SOURCE does not already define, since
### that would silently drop a value provisioning meant to set.
###
services_render_example_overrides() {
  local source="$1" output="$2"
  shift 2

  [[ -f "$source" ]] || provision_die "example file not found: ${source}"

  local tmp
  tmp="$(mktemp)"
  cp "$source" "$tmp"

  local pair name value escaped_value
  for pair in "$@"; do
    name="${pair%%=*}"
    value="${pair#*=}"

    provision_validate_safe_value "$value" \
      || provision_die "value for ${name} contains a disallowed character (semicolon, brace or newline)"

    if ! grep -qE "^${name}=" "$tmp"; then
      rm -f "$tmp"
      provision_die "${source} has no existing ${name}= line to override"
    fi

    escaped_value="$(printf '%s' "$value" | sed -e 's/[&/\]/\\&/g')"
    sed -i "s/^${name}=.*/${name}=${escaped_value}/" "$tmp"
  done

  mv -f "$tmp" "$output"
}

if [[ "${LEDGER_PROVISION_LIB_ONLY:-0}" != "1" ]]; then
  provision_log "Installing scripts and libraries"

  install -d -m 755 -o root -g root /usr/local/sbin
  for script in "${DEPLOY_DIR}"/bin/*; do
    [[ -e "$script" ]] || continue
    install -m 755 -o root -g root "$script" "/usr/local/sbin/$(basename "$script")"
  done

  install -d -m 755 -o root -g root /usr/local/lib/ledger
  for lib in "${DEPLOY_DIR}"/lib/*.sh; do
    [[ -e "$lib" ]] || continue
    install -m 644 -o root -g root "$lib" "/usr/local/lib/ledger/$(basename "$lib")"
  done

  provision_log "Installing systemd units"
  for unit in "${DEPLOY_DIR}"/systemd/*.service "${DEPLOY_DIR}"/systemd/*.timer; do
    [[ -e "$unit" ]] || continue
    install -m 644 -o root -g root "$unit" "/etc/systemd/system/$(basename "$unit")"
  done

  install -d -m 755 -o root -g root /etc/systemd/system/grafana-server.service.d
  install -m 644 -o root -g root \
    "${DEPLOY_DIR}/systemd/grafana-server.service.d/ledger.conf" \
    /etc/systemd/system/grafana-server.service.d/ledger.conf

  provision_log "Rendering non-secret server-side configuration"

  deploy_conf_rendered="$(mktemp)"
  services_render_example_overrides "${DEPLOY_DIR}/deploy.conf.example" "$deploy_conf_rendered" \
    "LEDGER_GITHUB_REPO=${LEDGER_GITHUB_REPO:-}" \
    "LEDGER_NOTIFY_EMAIL=${LEDGER_ALERT_EMAIL:-}"
  install -m 600 -o root -g root "$deploy_conf_rendered" /etc/ledger/deploy.conf
  rm -f "$deploy_conf_rendered"

  # Grafana follows the same STARTTLS choice as msmtp: required unless the
  # relay is explicitly a plaintext-only one on the local network.
  grafana_starttls_policy="MandatoryStartTLS"
  if [[ "${LEDGER_SMTP_STARTTLS:-on}" == "off" ]]; then
    grafana_starttls_policy="NoStartTLS"
  fi

  grafana_env_rendered="$(mktemp)"
  services_render_example_overrides "${DEPLOY_DIR}/grafana.env.example" "$grafana_env_rendered" \
    "GF_SERVER_DOMAIN=${LEDGER_GRAFANA_DOMAIN:-}" \
    "GF_SERVER_ROOT_URL=https://${LEDGER_GRAFANA_DOMAIN:-}/" \
    "GF_SMTP_HOST=${LEDGER_SMTP_RELAY:-}" \
    "GF_SMTP_FROM_ADDRESS=${LEDGER_MAIL_FROM:-}" \
    "GF_SMTP_STARTTLS_POLICY=${grafana_starttls_policy}" \
    "LEDGER_ALERT_EMAIL=${LEDGER_ALERT_EMAIL:-}"
  install -m 640 -o root -g "$GRAFANA_SERVICE_USER" "$grafana_env_rendered" /etc/ledger/grafana.env
  rm -f "$grafana_env_rendered"

  msmtprc_rendered="$(mktemp)"
  services_render_msmtprc "${LEDGER_SMTP_RELAY:-}" "${LEDGER_MAIL_FROM:-}" "${LEDGER_SMTP_STARTTLS:-on}" "$msmtprc_rendered"
  install -m 644 -o root -g root "$msmtprc_rendered" /etc/msmtprc
  rm -f "$msmtprc_rendered"
  services_ensure_msmtp_log /var/log/msmtp.log

  provision_log "Installing initial Grafana and Prometheus provisioning"

  install -d -m 755 -o root -g "$GRAFANA_SERVICE_USER" /etc/grafana
  install -m 640 -o root -g "$GRAFANA_SERVICE_USER" \
    "${DEPLOY_DIR}/provisioning/grafana/grafana.ini" /etc/grafana/grafana.ini
  rm -rf /etc/grafana/provisioning
  cp -a "${DEPLOY_DIR}/provisioning/grafana/provisioning" /etc/grafana/provisioning
  chown -R "root:${GRAFANA_SERVICE_USER}" /etc/grafana/provisioning

  install -d -m 755 -o root -g prometheus /etc/prometheus
  promtool check config "${DEPLOY_DIR}/provisioning/prometheus/prometheus.yml" \
    || provision_die "deploy/provisioning/prometheus/prometheus.yml failed promtool check config"
  install -m 640 -o root -g prometheus \
    "${DEPLOY_DIR}/provisioning/prometheus/prometheus.yml" /etc/prometheus/prometheus.yml

  # /var/lib/prometheus/node-exporter itself is created by 20-accounts.sh
  # (root:ledger-metrics, 2775) alongside the other service directories.
  install -m 644 -o root -g root \
    "${DEPLOY_DIR}/node-exporter/prometheus-node-exporter.default" \
    /etc/default/prometheus-node-exporter

  provision_log "Enabling automatic security updates"
  install -d -m 755 -o root -g root /etc/apt/apt.conf.d
  cat > /etc/apt/apt.conf.d/51ledger-unattended-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF

  RECIPIENTS_FILE="/etc/ledger/backup-recipients.txt"
  if [[ ! -f "$RECIPIENTS_FILE" ]]; then
    if [[ -t 0 ]]; then
      provision_log "No backup recipient configured yet."
      provision_log "Generate an age key pair on your own workstation (age-keygen), keep the private identity in the password manager, and paste only the public key (age1...) below."
      read -r -p "Backup recipient age public key: " age_public_key
      if ! provision_validate_age_public_key "$age_public_key"; then
        provision_die "that does not look like a single age1... public key; re-run provisioning to try again"
      fi
      printf '%s\n' "$age_public_key" > "$RECIPIENTS_FILE"
      chmod 644 "$RECIPIENTS_FILE"
      chown root:root "$RECIPIENTS_FILE"
      unset age_public_key
    else
      provision_log "WARNING: ${RECIPIENTS_FILE} is missing and there is no terminal to prompt on; backups will fail loudly and alert until it is created."
    fi
  fi

  provision_log "Reloading systemd"
  systemctl daemon-reload

  provision_log "Enabling and starting platform services"

  pg_service="$(systemctl list-units --type=service --no-legend --all 'postgresql@*-main.service' 2>/dev/null \
    | awk '{print $1}' | head -n1)"
  if [[ -z "$pg_service" ]]; then
    pg_service="postgresql@${PG_MAJOR}-main"
  fi
  systemctl enable --now "$pg_service"

  # Restarted rather than just started: the node exporter package starts its
  # service at install time, before the defaults file above exists, and a
  # re-run must apply changed configuration to services already running.
  for config_service in prometheus prometheus-node-exporter grafana-server; do
    systemctl enable "$config_service"
    systemctl restart "$config_service"
  done
  systemctl enable --now ledger-deploy-poll.timer
  systemctl enable --now ledger-backup.timer

  systemctl enable ledger.service
  if [[ -f /opt/ledger/current/app/Ledger.Service.dll ]]; then
    systemctl start ledger.service
  else
    provision_log "No release installed yet; ledger.service is enabled but not started."
  fi

  provision_log "Services module complete."
fi
