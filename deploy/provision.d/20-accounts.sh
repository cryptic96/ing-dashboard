#!/usr/bin/env bash
###
### Idempotent module: creates the ledger service accounts, their
### directories, the Data Protection certificate and the application env
### file. Never overwrites the certificate or the env file once created,
### never prints or logs the Data Protection password, and always tells
### the operator which two files to copy into the password manager.
###
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=deploy/provision.sh
LEDGER_PROVISION_LIB_ONLY=1 source "${SCRIPT_DIR}/../provision.sh"

###
### Renders the application env file content from the given Traefik IP and
### Data Protection certificate password. Produces exactly these keys, in
### this order, and no database password (peer auth needs none).
###
accounts_render_ledger_env() {
  local traefik_ip="$1" dp_password="$2"
  cat <<EOF
ASPNETCORE_ENVIRONMENT=Production
ReverseProxy__KnownProxies__0=${traefik_ip}
DataProtection__CertificatePath=/etc/ledger/dataprotection.pfx
DataProtection__CertificatePassword=${dp_password}
EOF
}

if [[ "${LEDGER_PROVISION_LIB_ONLY:-0}" != "1" ]]; then
  DP_CERT_PATH="/etc/ledger/dataprotection.pfx"
  DP_ENV_PATH="/etc/ledger/ledger.env"

  provision_log "Creating service groups and users"

  if ! getent group ledger-metrics >/dev/null; then
    groupadd --system ledger-metrics
  fi

  for svc_user in ledger ledger_migrator ledger_backup; do
    if ! id -u "$svc_user" >/dev/null 2>&1; then
      useradd --system --user-group --no-create-home --home-dir /nonexistent \
        --shell /usr/sbin/nologin "$svc_user"
    fi
  done

  usermod -aG ledger-metrics ledger_backup

  provision_log "Creating directories"

  mkdir -p /opt/ledger/releases
  chown root:root /opt/ledger/releases
  chmod 755 /opt/ledger/releases

  mkdir -p /etc/ledger
  chown root:ledger /etc/ledger
  chmod 750 /etc/ledger

  mkdir -p /var/lib/ledger-deploy
  chown root:root /var/lib/ledger-deploy
  chmod 700 /var/lib/ledger-deploy

  mkdir -p /var/backups/ledger
  chown ledger_backup:ledger_backup /var/backups/ledger
  chmod 700 /var/backups/ledger

  mkdir -p /var/lib/prometheus/node-exporter
  chown root:ledger-metrics /var/lib/prometheus/node-exporter
  chmod 2775 /var/lib/prometheus/node-exporter

  LEDGER_DP_PASSWORD=""

  if [[ ! -f "$DP_CERT_PATH" ]]; then
    provision_log "Generating the Data Protection certificate"

    workdir="$(mktemp -d)"
    chmod 700 "$workdir"
    old_umask="$(umask)"
    umask 077

    openssl req -x509 -newkey rsa:4096 -sha256 -days 3650 -nodes \
      -keyout "${workdir}/dp.key" -out "${workdir}/dp.crt" \
      -subj "/CN=household-ledger-data-protection" \
      >/dev/null 2>&1

    LEDGER_DP_PASSWORD="$(openssl rand -base64 32)"
    export LEDGER_DP_PASSWORD

    openssl pkcs12 -export \
      -inkey "${workdir}/dp.key" -in "${workdir}/dp.crt" \
      -out "${workdir}/dataprotection.pfx" \
      -passout env:LEDGER_DP_PASSWORD

    umask "$old_umask"

    install -m 640 -o root -g ledger "${workdir}/dataprotection.pfx" "$DP_CERT_PATH"
    shred -u "${workdir}/dp.key" 2>/dev/null || rm -f "${workdir}/dp.key"
    rm -rf "$workdir"
  fi

  if [[ ! -f "$DP_ENV_PATH" ]]; then
    if [[ -z "$LEDGER_DP_PASSWORD" ]]; then
      provision_die "cannot create ${DP_ENV_PATH} without the Data Protection password that belongs to the existing ${DP_CERT_PATH}; delete ${DP_CERT_PATH} to regenerate both together"
    fi

    provision_log "Writing ${DP_ENV_PATH}"

    render_tmp="$(mktemp)"
    chmod 600 "$render_tmp"
    accounts_render_ledger_env "${LEDGER_TRAEFIK_IP:-}" "$LEDGER_DP_PASSWORD" >"$render_tmp"
    install -m 640 -o root -g ledger "$render_tmp" "$DP_ENV_PATH"
    rm -f "$render_tmp"
  fi

  unset LEDGER_DP_PASSWORD

  provision_log "Data Protection certificate: ${DP_CERT_PATH}"
  provision_log "Application env file: ${DP_ENV_PATH}"
  provision_log "Copy both files into the password manager; neither is printed here."
fi
