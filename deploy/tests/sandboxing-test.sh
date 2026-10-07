#!/usr/bin/env bash
# Proves the privileged entry points are confined: the installer unit carries
# its sandboxing directives and write allow-list, ledger-apikey, ledger-login
# and ledger-grants hand systemd-run every sandboxing property, and
# provisioning installs tzdata.
# Offline: systemd-run, systemctl and id are stubbed, nothing on the host is
# touched.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
POLL_UNIT="${DEPLOY_DIR}/systemd/ledger-deploy-poll.service"
APIKEY="${DEPLOY_DIR}/bin/ledger-apikey"
LOGIN="${DEPLOY_DIR}/bin/ledger-login"
GRANTS="${DEPLOY_DIR}/bin/ledger-grants"
PACKAGES="${DEPLOY_DIR}/provision.d/10-packages.sh"

FAILURES=0

check() {
  local description="$1" expected="$2" actual="$3"
  if [ "$actual" = "$expected" ]; then
    printf 'PASS: %s\n' "$description"
  else
    printf 'FAIL: %s (expected [%s], got [%s])\n' "$description" "$expected" "$actual"
    FAILURES=$((FAILURES + 1))
  fi
}

file_has_line() {
  local file="$1" line="$2"
  if grep -qxF -- "$line" "$file"; then
    echo 1
  else
    echo 0
  fi
}

file_has_text() {
  local file="$1" text="$2"
  if grep -qF -- "$text" "$file"; then
    echo 1
  else
    echo 0
  fi
}

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

# --- installer unit -----------------------------------------------------------
for directive in \
  NoNewPrivileges=yes ProtectSystem=strict ProtectHome=read-only PrivateTmp=yes \
  ProtectKernelTunables=yes ProtectKernelModules=yes ProtectControlGroups=yes \
  RestrictNamespaces=yes LockPersonality=yes RestrictRealtime=yes \
  RestrictSUIDSGID=yes SystemCallArchitectures=native; do
  check "poll unit has ${directive}" "1" "$(file_has_line "$POLL_UNIT" "$directive")"
done

check "poll unit keeps the installer command" "1" \
  "$(file_has_line "$POLL_UNIT" "ExecStart=/usr/local/sbin/ledger-deploy poll")"

WRITE_PATHS="$(sed -n 's/^ReadWritePaths=//p' "$POLL_UNIT")"
for path in /opt/ledger /etc/grafana /etc/prometheus /var/lib/prometheus/node-exporter \
  /var/lib/ledger-deploy -/var/log/msmtp.log; do
  found=0
  for listed in $WRITE_PATHS; do
    [ "$listed" = "$path" ] && found=1
  done
  check "poll unit may write ${path}" "1" "$found"
done

check "poll unit does not restrict capabilities (the installer runs as root and switches users)" "0" \
  "$(grep -c '^CapabilityBoundingSet=' "$POLL_UNIT")"
check "poll unit does not restrict address families (the installer reaches GitHub)" "0" \
  "$(grep -c '^RestrictAddressFamilies=' "$POLL_UNIT")"

# --- wrapper properties ----------------------------------------------------------
for wrapper in "$APIKEY" "$LOGIN" "$GRANTS"; do
  for property in \
    NoNewPrivileges=yes ProtectSystem=strict ProtectHome=yes PrivateTmp=yes \
    PrivateDevices=yes ProtectKernelTunables=yes ProtectKernelModules=yes \
    ProtectControlGroups=yes RestrictNamespaces=yes LockPersonality=yes \
    CapabilityBoundingSet= RestrictAddressFamilies=AF_UNIX \
    Environment=HOME=/var/lib/ledger Environment=DOTNET_NOLOGO=1 \
    EnvironmentFile=/etc/ledger/ledger.env; do
    check "$(basename "$wrapper") passes ${property}" "1" \
      "$(file_has_text "$wrapper" "--property=${property} ")"
  done
  check "$(basename "$wrapper") runs as the ledger user and group" "1" \
    "$(file_has_text "$wrapper" "--uid=ledger --gid=ledger ")"
done

# --- apikey behaviour with stubbed host tools ------------------------------------
STUB_BIN="${WORKDIR}/bin"
SYSTEMD_RUN_LOG="${WORKDIR}/systemd-run.log"
mkdir -p "$STUB_BIN"
: > "$SYSTEMD_RUN_LOG"

cat > "${STUB_BIN}/id" <<'EOF_STUB'
#!/usr/bin/env bash
echo 0
EOF_STUB

cat > "${STUB_BIN}/systemd-run" <<EOF_STUB
#!/usr/bin/env bash
printf '%s\n' "\$*" >> "${SYSTEMD_RUN_LOG}"
exit 0
EOF_STUB
chmod +x "${STUB_BIN}/id" "${STUB_BIN}/systemd-run"
export PATH="${STUB_BIN}:${PATH}"

"$APIKEY" create 'Bad Name!' > /dev/null 2>&1
check "an invalid key name is rejected" "1" "$?"
check "an invalid key name never reaches systemd-run" "0" "$(wc -l < "$SYSTEMD_RUN_LOG" | tr -d ' ')"

"$APIKEY" create household-reader > /dev/null 2>&1
check "a valid key name is accepted" "0" "$?"
check "a valid key name reaches systemd-run once" "1" "$(wc -l < "$SYSTEMD_RUN_LOG" | tr -d ' ')"
check "the systemd-run call carries the sandboxing properties" "1" \
  "$(grep -c -- '--property=ProtectSystem=strict .*--property=RestrictAddressFamilies=AF_UNIX' "$SYSTEMD_RUN_LOG")"

# --- provisioning packages ---------------------------------------------------------
check "tzdata is in the provisioned package list" "1" "$(file_has_text "$PACKAGES" ' tzdata ')"

echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "All checks passed."
  exit 0
else
  echo "${FAILURES} check(s) failed."
  exit 1
fi
