#!/usr/bin/env bash
###
### Idempotent module: renders /etc/nftables.conf from
### deploy/nftables/ledger.nft.in using the admin SSH sources and reverse
### proxy address from provision.conf, dry-run checks the result before
### ever loading it, then loads it in a single atomic transaction so a bad
### or interrupted reload can never leave a partial ruleset active — the
### previous ruleset stays in force until the new one fully applies.
###
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
# shellcheck source=deploy/provision.sh
LEDGER_PROVISION_LIB_ONLY=1 source "${SCRIPT_DIR}/../provision.sh"

if [[ "${LEDGER_PROVISION_LIB_ONLY:-0}" != "1" ]]; then
  NFTABLES_TARGET="/etc/nftables.conf"

  provision_log "Rendering the default-drop firewall ruleset"

  rendered="$(mktemp)"
  provision_render_template "${DEPLOY_DIR}/nftables/ledger.nft.in" "$rendered" \
    "LEDGER_ADMIN_SSH_SOURCES=${LEDGER_ADMIN_SSH_SOURCES:-}" \
    "LEDGER_TRAEFIK_IP=${LEDGER_TRAEFIK_IP:-}"

  provision_log "Checking the rendered ruleset before loading it"
  if ! nft -c -f "$rendered"; then
    rm -f "$rendered"
    provision_die "rendered nftables ruleset failed the dry-run check; the previous ruleset is untouched"
  fi

  install -m 755 -o root -g root "$rendered" "$NFTABLES_TARGET"
  rm -f "$rendered"

  provision_log "Loading the firewall ruleset in one transaction"
  nft -f "$NFTABLES_TARGET"

  systemctl enable --now nftables.service

  provision_log "Default-drop firewall loaded: SSH is admitted only from ${LEDGER_ADMIN_SSH_SOURCES:-<unset>}, the app and Grafana ports only from ${LEDGER_TRAEFIK_IP:-<unset>}."
  provision_log "If the SSH sources were set wrongly, 'pct enter <ctid>' from the Proxmox host still works — it never goes through this firewall."
fi
