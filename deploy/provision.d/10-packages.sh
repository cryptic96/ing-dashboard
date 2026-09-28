#!/usr/bin/env bash
###
### Idempotent module: installs every package this platform needs from a
### fingerprint- or checksum-verified source. Nothing here pipes a network
### download straight into a shell — every third-party key is fingerprint
### -checked, every third-party repository is added with an explicit
### signed-by keyring, and the Prometheus tarball is checksummed before
### extraction.
###
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=deploy/provision.sh
LEDGER_PROVISION_LIB_ONLY=1 source "${SCRIPT_DIR}/../provision.sh"

KEYRING_DIR="/usr/share/keyrings"
SOURCES_DIR="/etc/apt/sources.list.d"
PREFERENCES_DIR="/etc/apt/preferences.d"

###
### Downloads a third-party signing key, refuses to trust it unless its
### fingerprint matches the pin from versions.env, then installs it as a
### dearmored keyring (armored input is dearmored; an already-binary
### keyring, such as the GitHub CLI's, is installed as-is).
###
install_apt_signing_key() {
  local label="$1" key_url="$2" expected_fpr="$3" keyring_path="$4"
  local tmp_key actual_fpr

  if [[ -f "$keyring_path" ]]; then
    return 0
  fi

  tmp_key="$(mktemp)"
  curl -fsSL --max-time 60 "$key_url" -o "$tmp_key"

  if ! actual_fpr="$(gpg --batch --with-colons --show-keys "$tmp_key" 2>/dev/null | provision_key_fingerprint)"; then
    rm -f "$tmp_key"
    provision_die "${label}: signing key at ${key_url} did not yield exactly one active primary key"
  fi
  if [[ "$actual_fpr" != "$expected_fpr" ]]; then
    rm -f "$tmp_key"
    provision_die "${label}: signing key fingerprint mismatch (expected ${expected_fpr}, got ${actual_fpr})"
  fi

  if head -c 20 "$tmp_key" | grep -q "BEGIN PGP"; then
    gpg --batch --dearmor -o "$keyring_path" "$tmp_key"
  else
    install -m 644 "$tmp_key" "$keyring_path"
  fi
  chmod 644 "$keyring_path"
  rm -f "$tmp_key"
  provision_log "${label}: signing key installed and fingerprint verified"
}

if [[ "${LEDGER_PROVISION_LIB_ONLY:-0}" != "1" ]]; then
  provision_log "apt-get update"
  apt-get update -qq

  provision_log "Installing base packages"
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
    ca-certificates curl gnupg jq unzip openssl nftables age msmtp \
    prometheus-node-exporter unattended-upgrades \
    "$DOTNET_RUNTIME_PACKAGE"

  # Some container templates ship a local MTA. Mail leaves this host only
  # through msmtp and Grafana's own SMTP client, so a listening MTA is
  # unused attack surface.
  postfix_status="$(dpkg-query -W -f='${Status}' postfix 2>/dev/null || true)"
  if [[ "$postfix_status" == "install ok installed" ]]; then
    provision_log "Removing the unused local MTA (postfix)"
    DEBIAN_FRONTEND=noninteractive apt-get purge -y -qq postfix
  fi

  install_apt_signing_key "PGDG" "$PGDG_KEY_URL" "$PGDG_KEY_FINGERPRINT" \
    "${KEYRING_DIR}/pgdg.gpg"
  install_apt_signing_key "Grafana" "$GRAFANA_KEY_URL" "$GRAFANA_KEY_FINGERPRINT" \
    "${KEYRING_DIR}/grafana.gpg"
  install_apt_signing_key "GitHub CLI" "$GH_CLI_KEY_URL" "$GH_CLI_KEY_FINGERPRINT" \
    "${KEYRING_DIR}/githubcli.gpg"

  if [[ ! -f "${SOURCES_DIR}/pgdg.list" ]]; then
    echo "deb [signed-by=${KEYRING_DIR}/pgdg.gpg] https://apt.postgresql.org/pub/repos/apt noble-pgdg main" \
      >"${SOURCES_DIR}/pgdg.list"
  fi
  if [[ ! -f "${SOURCES_DIR}/grafana.list" ]]; then
    echo "deb [signed-by=${KEYRING_DIR}/grafana.gpg] https://apt.grafana.com stable main" \
      >"${SOURCES_DIR}/grafana.list"
  fi
  if [[ ! -f "${SOURCES_DIR}/github-cli.list" ]]; then
    echo "deb [signed-by=${KEYRING_DIR}/githubcli.gpg] https://cli.github.com/packages stable main" \
      >"${SOURCES_DIR}/github-cli.list"
  fi

  if [[ ! -f "${PREFERENCES_DIR}/grafana" ]]; then
    cat >"${PREFERENCES_DIR}/grafana" <<EOF
Package: grafana
Pin: version ${GRAFANA_VERSION_PIN}
Pin-Priority: 1001
EOF
  fi

  provision_log "apt-get update (with the new sources)"
  apt-get update -qq

  provision_log "Installing PostgreSQL, Grafana and the GitHub CLI"
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
    "postgresql-${PG_MAJOR}" "postgresql-client-${PG_MAJOR}" \
    "grafana=${GRAFANA_VERSION_PIN}" gh

  GH_VERSION="$(gh --version | head -n1 | awk '{print $3}')"
  if ! provision_version_ge "$GH_VERSION" "$GH_CLI_MIN_VERSION"; then
    provision_die "gh ${GH_VERSION} is older than the required ${GH_CLI_MIN_VERSION} (attestation verify support)"
  fi

  if ! command -v /usr/local/bin/prometheus >/dev/null 2>&1; then
    provision_log "Installing Prometheus ${PROMETHEUS_VERSION}"
    prom_tmp="$(mktemp -d)"
    prom_tarball="${prom_tmp}/prometheus.tar.gz"
    curl -fsSL --max-time 120 \
      "https://github.com/prometheus/prometheus/releases/download/v${PROMETHEUS_VERSION}/prometheus-${PROMETHEUS_VERSION}.linux-amd64.tar.gz" \
      -o "$prom_tarball"

    actual_sha="$(sha256sum "$prom_tarball" | awk '{print $1}')"
    if [[ "$actual_sha" != "$PROMETHEUS_SHA256" ]]; then
      rm -rf "$prom_tmp"
      provision_die "Prometheus tarball checksum mismatch (expected ${PROMETHEUS_SHA256}, got ${actual_sha})"
    fi

    tar -xzf "$prom_tarball" -C "$prom_tmp"
    prom_dir="${prom_tmp}/prometheus-${PROMETHEUS_VERSION}.linux-amd64"
    install -m 755 "${prom_dir}/prometheus" /usr/local/bin/prometheus
    install -m 755 "${prom_dir}/promtool" /usr/local/bin/promtool
    rm -rf "$prom_tmp"
  fi

  mkdir -p /etc/prometheus /var/lib/prometheus
  chown prometheus:prometheus /etc/prometheus /var/lib/prometheus

  provision_log "Package installation complete"
fi
