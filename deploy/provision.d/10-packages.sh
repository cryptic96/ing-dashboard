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
### Succeeds when the keyring at KEYRING_PATH exists and holds exactly one
### active primary key whose fingerprint equals the pin. A missing, empty,
### half-written, rotated or never-verified keyring does not match.
###
apt_keyring_matches_pin() {
  local keyring_path="$1" expected_fpr="$2" current_fpr

  [[ -f "$keyring_path" ]] || return 1
  current_fpr="$(gpg --batch --with-colons --show-keys "$keyring_path" 2>/dev/null | provision_key_fingerprint)" || return 1
  [[ "$current_fpr" == "$expected_fpr" ]]
}

###
### Downloads a third-party signing key, refuses to trust it unless its
### fingerprint matches the pin from versions.env, then installs it as a
### dearmored keyring (armored input is dearmored; an already-binary
### keyring, such as the GitHub CLI's, is installed as-is). An installed
### keyring that already holds the pinned fingerprint is left alone; any
### other one is replaced, so a bumped pin reaches an existing host.
###
install_apt_signing_key() {
  local label="$1" key_url="$2" expected_fpr="$3" keyring_path="$4"
  local tmp_key tmp_keyring actual_fpr

  if apt_keyring_matches_pin "$keyring_path" "$expected_fpr"; then
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
    tmp_keyring="$(mktemp)"
    gpg --batch --yes --dearmor -o "$tmp_keyring" "$tmp_key"
    install -m 644 "$tmp_keyring" "$keyring_path"
    rm -f "$tmp_keyring"
  else
    install -m 644 "$tmp_key" "$keyring_path"
  fi
  rm -f "$tmp_key"
  provision_log "${label}: signing key installed and fingerprint verified"
}

###
### Prints the apt sources line for a third-party repository.
###
apt_source_line() {
  local keyring_path="$1" url="$2" suite="$3" component="$4"
  printf 'deb [signed-by=%s] %s %s %s\n' "$keyring_path" "$url" "$suite" "$component"
}

###
### Writes the apt sources file at FILE_PATH whenever its content differs
### from LINE, so a changed suite, URL or keyring path reaches an existing
### host instead of keeping the first-install value.
###
install_apt_source() {
  local file_path="$1" line="$2"

  if [[ -f "$file_path" ]] && [[ "$(cat "$file_path")" == "$line" ]]; then
    return 0
  fi
  provision_log "Writing apt source ${file_path}"
  printf '%s\n' "$line" >"$file_path"
}

###
### Prints the apt preferences entry that holds Grafana at the pinned
### version. The file is rewritten whenever it differs, so a pin bumped in
### versions.env reaches an existing host; a stale entry would keep the old
### version as apt's candidate and a later upgrade would move back to it.
###
grafana_pin_preferences() {
  local version="$1"
  printf 'Package: grafana\nPin: version %s\nPin-Priority: 1001\n' "$version"
}

###
### Succeeds when Prometheus must be (re)installed: the binary is missing or
### reports a different version than the pin. The first argument is the
### first line of `prometheus --version`, empty when it is not installed.
###
prometheus_needs_install() {
  local version_line="$1" wanted="$2" installed
  installed="$(awk '$1 == "prometheus," && $2 == "version" {print $3}' <<<"$version_line")"
  [[ "$installed" != "$wanted" ]]
}

if [[ "${LEDGER_PROVISION_LIB_ONLY:-0}" != "1" ]]; then
  provision_log "apt-get update"
  apt-get update -qq

  provision_log "Installing base packages"
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
    ca-certificates curl gnupg jq unzip openssl nftables age msmtp \
    prometheus-node-exporter unattended-upgrades tzdata \
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

  install_apt_source "${SOURCES_DIR}/pgdg.list" \
    "$(apt_source_line "${KEYRING_DIR}/pgdg.gpg" https://apt.postgresql.org/pub/repos/apt noble-pgdg main)"
  install_apt_source "${SOURCES_DIR}/grafana.list" \
    "$(apt_source_line "${KEYRING_DIR}/grafana.gpg" https://apt.grafana.com stable main)"
  install_apt_source "${SOURCES_DIR}/github-cli.list" \
    "$(apt_source_line "${KEYRING_DIR}/githubcli.gpg" https://cli.github.com/packages stable main)"

  grafana_pin="$(grafana_pin_preferences "$GRAFANA_VERSION_PIN")"
  if [[ ! -f "${PREFERENCES_DIR}/grafana" ]] || [[ "$(cat "${PREFERENCES_DIR}/grafana")" != "$grafana_pin" ]]; then
    provision_log "Pinning Grafana to ${GRAFANA_VERSION_PIN}"
    printf '%s\n' "$grafana_pin" >"${PREFERENCES_DIR}/grafana"
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

  prometheus_version_line=""
  if [[ -x /usr/local/bin/prometheus ]]; then
    prometheus_version_line="$(/usr/local/bin/prometheus --version 2>/dev/null | head -n1 || true)"
  fi
  prometheus_installed_now=0
  if prometheus_needs_install "$prometheus_version_line" "$PROMETHEUS_VERSION"; then
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
    prometheus_installed_now=1
  fi

  mkdir -p /etc/prometheus /var/lib/prometheus
  chown prometheus:prometheus /etc/prometheus /var/lib/prometheus

  if [[ "$prometheus_installed_now" == "1" ]] && systemctl is-active --quiet prometheus; then
    provision_log "Restarting Prometheus on ${PROMETHEUS_VERSION}"
    systemctl restart prometheus
  fi

  provision_log "Package installation complete"
fi
