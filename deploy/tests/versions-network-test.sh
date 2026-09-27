#!/usr/bin/env bash
###
### Re-runnable, read-only check that every pin in deploy/versions.env still
### matches its real source: the PGDG and Ubuntu package indexes, the three
### third-party signing keys, the Grafana package's service user, the
### Prometheus release checksum, and a GitHub CLI new enough for attestation
### verification. Named *-network-test.sh so a lint harness that skips
### network tests by name can find it.
###
### Nothing here installs a package, writes outside a temp directory, or
### runs as anything other than the invoking user.
###
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSIONS_ENV="${SCRIPT_DIR}/../versions.env"
CURL_MAX_TIME=30

ALLOWED_KEYS=(
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

FAILURES=0

fail() {
  echo "FAIL: $*" >&2
  FAILURES=$((FAILURES + 1))
}

pass() {
  echo "OK: $*"
}

###
### Loads KEY=VALUE lines from versions.env into shell variables, refusing
### any key not on the allow-list and never evaluating a value.
###
load_versions_env() {
  local file="$1" line key value is_allowed k
  [[ -f "$file" ]] || { echo "versions.env not found at $file" >&2; exit 1; }
  while IFS= read -r line || [[ -n "$line" ]]; do
    [[ -z "$line" ]] && continue
    [[ "$line" =~ ^[[:space:]]*# ]] && continue
    if [[ "$line" =~ ^([A-Z][A-Z0-9_]*)=(.*)$ ]]; then
      key="${BASH_REMATCH[1]}"
      value="${BASH_REMATCH[2]}"
      value="${value%\"}"
      value="${value#\"}"
      is_allowed=0
      for k in "${ALLOWED_KEYS[@]}"; do
        if [[ "$k" == "$key" ]]; then
          is_allowed=1
          break
        fi
      done
      if [[ "$is_allowed" -ne 1 ]]; then
        echo "versions.env: unknown key '${key}'" >&2
        exit 1
      fi
      printf -v "$key" '%s' "$value"
    else
      echo "versions.env: malformed line: ${line}" >&2
      exit 1
    fi
  done <"$file"
}

###
### Extracts the fingerprint of the single non-expired, non-revoked primary
### key in a downloaded key file (armored or already dearmored), through a
### throwaway GNUPGHOME. Fails if there is not exactly one such key.
###
key_fingerprint() {
  local key_file="$1" gnupg_home raw line validity pending fp="" count=0
  gnupg_home="$(mktemp -d)"
  chmod 700 "$gnupg_home"
  raw="$(GNUPGHOME="$gnupg_home" gpg --batch --with-colons --show-keys "$key_file" 2>/dev/null || true)"
  rm -rf "$gnupg_home"

  pending=0
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
  done <<<"$raw"

  if [[ "$count" -ne 1 ]]; then
    echo "expected exactly one active primary key in ${key_file}, found ${count}" >&2
    return 1
  fi
  printf '%s' "$fp"
}

check_pgdg_package() {
  local gz decompressed
  gz="$(mktemp)"
  decompressed="$(mktemp)"
  if ! curl -fsSL --max-time "$CURL_MAX_TIME" \
    "https://apt.postgresql.org/pub/repos/apt/dists/noble-pgdg/main/binary-amd64/Packages.gz" \
    -o "$gz"; then
    fail "could not download the PGDG noble-pgdg package index"
    rm -f "$gz" "$decompressed"
    return
  fi
  gunzip -c "$gz" >"$decompressed"
  if grep -qx "Package: postgresql-${PG_MAJOR}" "$decompressed"; then
    pass "PGDG noble-pgdg lists postgresql-${PG_MAJOR}"
  else
    fail "PGDG noble-pgdg does not list postgresql-${PG_MAJOR}"
  fi
  rm -f "$gz" "$decompressed"
}

check_dotnet_package() {
  local gz decompressed
  gz="$(mktemp)"
  decompressed="$(mktemp)"
  if ! curl -fsSL --max-time "$CURL_MAX_TIME" \
    "http://archive.ubuntu.com/ubuntu/dists/noble-updates/main/binary-amd64/Packages.gz" \
    -o "$gz"; then
    fail "could not download the Ubuntu noble-updates package index"
    rm -f "$gz" "$decompressed"
    return
  fi
  gunzip -c "$gz" >"$decompressed"
  if grep -qx "Package: ${DOTNET_RUNTIME_PACKAGE}" "$decompressed"; then
    pass "Ubuntu noble-updates lists ${DOTNET_RUNTIME_PACKAGE}"
  else
    fail "Ubuntu noble-updates does not list ${DOTNET_RUNTIME_PACKAGE}"
  fi
  rm -f "$gz" "$decompressed"
}

check_key_fingerprint() {
  local label="$1" url="$2" expected="$3" tmp actual
  tmp="$(mktemp)"
  if ! curl -fsSL --max-time "$CURL_MAX_TIME" "$url" -o "$tmp"; then
    fail "could not download the ${label} signing key from ${url}"
    rm -f "$tmp"
    return
  fi
  if actual="$(key_fingerprint "$tmp")"; then
    if [[ "$actual" == "$expected" ]]; then
      pass "${label} signing key fingerprint matches (${actual})"
    else
      fail "${label} signing key fingerprint mismatch: expected ${expected}, got ${actual}"
    fi
  else
    fail "${label} signing key at ${url} did not yield exactly one active primary key"
  fi
  rm -f "$tmp"
}

check_grafana() {
  local gz decompressed deb_url deb_tmp extract_dir unit_file actual_user
  gz="$(mktemp)"
  decompressed="$(mktemp)"
  if ! curl -fsSL --max-time "$CURL_MAX_TIME" \
    "https://apt.grafana.com/dists/stable/main/binary-amd64/Packages.gz" \
    -o "$gz"; then
    fail "could not download the apt.grafana.com stable package index"
    rm -f "$gz" "$decompressed"
    return
  fi
  gunzip -c "$gz" >"$decompressed"

  deb_url="$(awk -v ver="${GRAFANA_VERSION_PIN}" '
    /^Package: grafana$/ { pkg = 1; ver_match = 0; next }
    pkg && /^Version: / {
      v = $0
      sub(/^Version: /, "", v)
      ver_match = (v == ver)
      next
    }
    pkg && ver_match && /^Filename: / {
      f = $0
      sub(/^Filename: /, "", f)
      print f
      exit
    }
    /^$/ { pkg = 0; ver_match = 0 }
  ' "$decompressed")"
  rm -f "$gz" "$decompressed"

  if [[ -z "$deb_url" ]]; then
    fail "apt.grafana.com stable does not offer grafana ${GRAFANA_VERSION_PIN}"
    return
  fi
  pass "apt.grafana.com stable offers grafana ${GRAFANA_VERSION_PIN}"

  deb_tmp="$(mktemp -d)"
  if ! curl -fsSL --max-time 120 "https://apt.grafana.com/${deb_url}" -o "${deb_tmp}/grafana.deb"; then
    fail "could not download the pinned grafana .deb"
    rm -rf "$deb_tmp"
    return
  fi

  extract_dir="${deb_tmp}/extracted"
  mkdir -p "$extract_dir"
  if ! dpkg-deb -x "${deb_tmp}/grafana.deb" "$extract_dir" 2>/dev/null; then
    fail "could not extract the pinned grafana .deb"
    rm -rf "$deb_tmp"
    return
  fi

  unit_file="${extract_dir}/usr/lib/systemd/system/grafana-server.service"
  if [[ ! -f "$unit_file" ]]; then
    fail "grafana .deb does not contain usr/lib/systemd/system/grafana-server.service"
    rm -rf "$deb_tmp"
    return
  fi

  actual_user="$(grep -m1 '^User=' "$unit_file" | cut -d= -f2)"
  if [[ "$actual_user" == "$GRAFANA_SERVICE_USER" ]]; then
    pass "grafana-server.service runs as User=${actual_user}"
  else
    fail "grafana-server.service runs as User=${actual_user}, versions.env expects ${GRAFANA_SERVICE_USER}"
  fi
  rm -rf "$deb_tmp"
}

check_prometheus() {
  local tmp expected_line
  tmp="$(mktemp)"
  if ! curl -fsSL -L --max-time "$CURL_MAX_TIME" \
    "https://github.com/prometheus/prometheus/releases/download/v${PROMETHEUS_VERSION}/sha256sums.txt" \
    -o "$tmp"; then
    fail "could not download the sha256sums.txt for Prometheus v${PROMETHEUS_VERSION}"
    rm -f "$tmp"
    return
  fi
  expected_line="prometheus-${PROMETHEUS_VERSION}.linux-amd64.tar.gz"
  if grep -qE "^${PROMETHEUS_SHA256}[[:space:]]+${expected_line//./\\.}\$" "$tmp"; then
    pass "Prometheus v${PROMETHEUS_VERSION} sha256sums.txt matches the pinned checksum"
  else
    fail "Prometheus v${PROMETHEUS_VERSION} sha256sums.txt does not list PROMETHEUS_SHA256 for ${expected_line}"
  fi
  rm -f "$tmp"
}

check_gh_cli() {
  local tmp version
  tmp="$(mktemp)"
  if ! curl -fsSL --max-time "$CURL_MAX_TIME" \
    "https://cli.github.com/packages/dists/stable/main/binary-amd64/Packages" \
    -o "$tmp"; then
    fail "could not download the GitHub CLI stable package index"
    rm -f "$tmp"
    return
  fi
  version="$(awk '/^Package: gh$/ { found = 1; next } found && /^Version: / { print $2; exit }' "$tmp")"
  rm -f "$tmp"
  if [[ -z "$version" ]]; then
    fail "GitHub CLI stable index does not list package gh"
    return
  fi
  if dpkg --compare-versions "$version" ge "$GH_CLI_MIN_VERSION"; then
    pass "GitHub CLI stable offers gh ${version} (>= ${GH_CLI_MIN_VERSION})"
  else
    fail "GitHub CLI stable offers gh ${version}, below the required ${GH_CLI_MIN_VERSION}"
  fi
}

main() {
  load_versions_env "$VERSIONS_ENV"

  check_pgdg_package
  check_dotnet_package
  check_key_fingerprint "PGDG" "$PGDG_KEY_URL" "$PGDG_KEY_FINGERPRINT"
  check_key_fingerprint "Grafana" "$GRAFANA_KEY_URL" "$GRAFANA_KEY_FINGERPRINT"
  check_key_fingerprint "GitHub CLI" "$GH_CLI_KEY_URL" "$GH_CLI_KEY_FINGERPRINT"
  check_grafana
  check_prometheus
  check_gh_cli

  if [[ "$FAILURES" -gt 0 ]]; then
    echo "${FAILURES} pin(s) failed verification against their real source." >&2
    exit 1
  fi
  echo "All install-source pins verified against their real source."
}

main "$@"
