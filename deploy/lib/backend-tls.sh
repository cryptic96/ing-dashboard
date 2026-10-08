#!/usr/bin/env bash
# The certificate pair the application presents on the proxy-facing port.
# Sourced by provisioning and by the installer, never executed directly, so
# both create it with exactly the same code. The reverse proxy trusts this one
# certificate (it is pinned there), so an existing pair is never replaced.

if [ -n "${LEDGER_BACKEND_TLS_SH_LOADED:-}" ]; then
  return 0
fi
LEDGER_BACKEND_TLS_SH_LOADED=1

# The fixed internal name the certificate carries and the reverse proxy
# verifies. It is not a real host name: the proxy connects to the host's
# address and only checks that the presented certificate names this.
LEDGER_BACKEND_TLS_NAME="ledger-backend"

# Logs through whichever logger the caller has loaded.
ledger_backend_tls_log() {
  if declare -F ledger_log > /dev/null; then
    ledger_log "$*"
  elif declare -F provision_log > /dev/null; then
    provision_log "$*"
  else
    printf '%s\n' "$*" >&2
  fi
}

# Logs an error through the caller's logger and exits non-zero.
ledger_backend_tls_die() {
  if declare -F ledger_die > /dev/null; then
    ledger_die "$*"
  elif declare -F provision_die > /dev/null; then
    provision_die "$*"
  fi
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

# Prints the SHA-256 fingerprint of the certificate file, as colon-separated
# upper-case hex. Only the public certificate is ever read here.
ledger_backend_tls_fingerprint() {
  openssl x509 -in "$1" -noout -fingerprint -sha256 | cut -d= -f2
}

# Succeeds when the private key belongs to the certificate: both must yield
# the same public key. The key itself is read by openssl only and never printed.
ledger_backend_tls_pair_matches() {
  local crt="$1" key="$2" from_crt from_key
  from_crt="$(openssl x509 -in "$crt" -noout -pubkey 2> /dev/null)" || return 1
  from_key="$(openssl pkey -in "$key" -pubout 2> /dev/null)" || return 1
  [ -n "$from_crt" ] && [ "$from_crt" = "$from_key" ]
}

# Creates the self-signed certificate and its private key as
# DIR/backend-tls.crt (public, mode 0644) and DIR/backend-tls.key (mode 0640),
# owned by root and the ledger group, when neither exists. The key is an
# EC P-256 key, the certificate names only the internal name above and is
# valid for ten years. An existing pair is left exactly as it is, apart from a
# check that its two halves belong together. Exactly one of the two files
# existing, or a pair that does not match, stops the caller: regenerating
# would silently break the proxy's pin, and guessing which half is right is
# not safe.
#
# The directory defaults to /etc/ledger below the relocated test root, if any
# (LEDGER_BACKEND_TLS_DIR overrides it), and the owner and group default to
# root and ledger (LEDGER_BACKEND_TLS_OWNER and LEDGER_BACKEND_TLS_GROUP
# override them). Under a relocated test root the files belong to the current
# user instead, since the test root stands in for the privileged installation
# root and is never itself run as root.
ledger_ensure_backend_tls() {
  local dir="${LEDGER_BACKEND_TLS_DIR:-${LEDGER_DEPLOY_ROOT:-}/etc/ledger}"
  local owner="${LEDGER_BACKEND_TLS_OWNER:-root}" group="${LEDGER_BACKEND_TLS_GROUP:-ledger}"
  if [ -n "${LEDGER_DEPLOY_ROOT:-}" ]; then
    owner="${LEDGER_BACKEND_TLS_OWNER:-$(id -un)}"
    group="${LEDGER_BACKEND_TLS_GROUP:-$(id -gn)}"
  fi

  local crt="${dir}/backend-tls.crt" key="${dir}/backend-tls.key"

  if [ -e "$crt" ] && [ -e "$key" ]; then
    if ! ledger_backend_tls_pair_matches "$crt" "$key"; then
      ledger_backend_tls_die "${crt} and ${key} do not belong together; refusing to continue (delete both to generate a new pair, then update the reverse proxy)"
    fi
    ledger_backend_tls_log "backend TLS certificate present: ${crt} (SHA-256 $(ledger_backend_tls_fingerprint "$crt"))"
    return 0
  fi

  if [ -e "$crt" ] || [ -e "$key" ]; then
    ledger_backend_tls_die "only one of ${crt} and ${key} exists; refusing to generate a new pair (delete the remaining file to regenerate both, then update the reverse proxy)"
  fi

  ledger_backend_tls_log "generating the backend TLS certificate"

  local workdir old_umask
  workdir="$(mktemp -d)"
  chmod 700 "$workdir"
  old_umask="$(umask)"
  umask 077

  if ! openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -sha256 -days 3650 -nodes \
      -keyout "${workdir}/backend-tls.key" -out "${workdir}/backend-tls.crt" \
      -subj "/CN=${LEDGER_BACKEND_TLS_NAME}" \
      -addext "subjectAltName=DNS:${LEDGER_BACKEND_TLS_NAME}" \
      -addext "keyUsage=critical,digitalSignature" \
      -addext "extendedKeyUsage=serverAuth" \
      > /dev/null 2>&1; then
    umask "$old_umask"
    rm -rf "$workdir"
    ledger_backend_tls_die "openssl could not generate the backend TLS certificate"
  fi
  umask "$old_umask"

  mkdir -p "$dir"
  if ! install -m 640 -o "$owner" -g "$group" "${workdir}/backend-tls.key" "${dir}/.backend-tls.key.new" \
    || ! install -m 644 -o "$owner" -g "$group" "${workdir}/backend-tls.crt" "${dir}/.backend-tls.crt.new"; then
    rm -f "${dir}/.backend-tls.key.new" "${dir}/.backend-tls.crt.new"
    shred -u "${workdir}/backend-tls.key" 2> /dev/null || rm -f "${workdir}/backend-tls.key"
    rm -rf "$workdir"
    ledger_backend_tls_die "could not install the backend TLS certificate under ${dir}"
  fi

  mv -T "${dir}/.backend-tls.key.new" "$key"
  mv -T "${dir}/.backend-tls.crt.new" "$crt"
  shred -u "${workdir}/backend-tls.key" 2> /dev/null || rm -f "${workdir}/backend-tls.key"
  rm -rf "$workdir"

  ledger_backend_tls_log "backend TLS certificate created: ${crt} (SHA-256 $(ledger_backend_tls_fingerprint "$crt"))"
  ledger_backend_tls_log "install this public certificate in the reverse proxy; the key stays on this host"
}
