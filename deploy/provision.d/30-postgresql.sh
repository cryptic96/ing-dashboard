#!/usr/bin/env bash
###
### Idempotent module: installs the socket-only PostgreSQL configuration,
### bootstraps the roles and database, then self-checks the result and
### refuses to finish unless the database really is socket-only and every
### role boundary really holds.
###
### Operators reach the database by connecting to the LXC over SSH and
### running psql there as the postgres OS user; a GUI tool reaches it by
### forwarding that same Unix socket over the SSH connection. There is no
### network route to PostgreSQL from anywhere, on purpose.
###
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
# shellcheck source=deploy/provision.sh
LEDGER_PROVISION_LIB_ONLY=1 source "${SCRIPT_DIR}/../provision.sh"

if [[ "${LEDGER_PROVISION_LIB_ONLY:-0}" != "1" ]]; then
  PG_CONF_DIR="/etc/postgresql/${PG_MAJOR}/main"
  CONF_D_TARGET="${PG_CONF_DIR}/conf.d/ledger.conf"
  HBA_TARGET="${PG_CONF_DIR}/pg_hba.conf"
  IDENT_TARGET="${PG_CONF_DIR}/pg_ident.conf"
  CONFIG_CHANGED=0

  install_if_different() {
    local src="$1" dest="$2" mode="$3"
    if [[ ! -f "$dest" ]] || ! cmp -s "$src" "$dest"; then
      install -m "$mode" -o root -g postgres "$src" "$dest"
      CONFIG_CHANGED=1
    fi
  }

  provision_log "Installing PostgreSQL configuration"

  mkdir -p "${PG_CONF_DIR}/conf.d"
  install_if_different "${DEPLOY_DIR}/postgresql/ledger.conf" "$CONF_D_TARGET" 644
  install_if_different "${DEPLOY_DIR}/postgresql/pg_hba.conf" "$HBA_TARGET" 640

  # The repository's pg_ident.conf holds the currently-confirmed Grafana
  # service user (grafana); render it from versions.env's pin here so a
  # future change to GRAFANA_SERVICE_USER never needs a matching repo edit.
  rendered_ident="$(mktemp)"
  sed -E "s/^(grafana_reader_map[[:space:]]+)[^[:space:]]+/\1${GRAFANA_SERVICE_USER}/" \
    "${DEPLOY_DIR}/postgresql/pg_ident.conf" >"$rendered_ident"
  install_if_different "$rendered_ident" "$IDENT_TARGET" 640
  rm -f "$rendered_ident"

  if [[ "$CONFIG_CHANGED" -eq 1 ]]; then
    provision_log "Configuration changed, restarting postgresql@${PG_MAJOR}-main"
    systemctl restart "postgresql@${PG_MAJOR}-main"
  fi

  provision_log "Bootstrapping roles and database"

  runuser -u postgres -- psql -v ON_ERROR_STOP=1 -f "${DEPLOY_DIR}/sql/bootstrap-roles.sql"

  if [[ "$(runuser -u postgres -- psql -Atc "SELECT count(*) FROM pg_database WHERE datname = 'ledger'")" == "0" ]]; then
    runuser -u postgres -- psql -v ON_ERROR_STOP=1 -c "CREATE DATABASE ledger OWNER ledger_migrator"
  fi

  runuser -u postgres -- psql -v ON_ERROR_STOP=1 -d ledger -f "${DEPLOY_DIR}/sql/bootstrap-database.sql"

  provision_log "Running self-checks"

  listen_addresses="$(runuser -u postgres -- psql -Atc 'SHOW listen_addresses')"
  if [[ -n "$listen_addresses" ]]; then
    provision_die "listen_addresses is '${listen_addresses}', expected an empty string"
  fi

  if ss -Hltnp 2>/dev/null | grep -q 'postgres'; then
    provision_die "a TCP socket is owned by postgres; PostgreSQL must be socket-only"
  fi

  non_local_rules="$(runuser -u postgres -- psql -Atc "SELECT count(*) FROM pg_hba_file_rules WHERE type <> 'local'")"
  if [[ "$non_local_rules" != "0" ]]; then
    provision_die "pg_hba_file_rules reports ${non_local_rules} non-local rule(s)"
  fi

  actual="$(runuser -u ledger -- psql -d ledger -U ledger_runtime -Atc 'select current_user')"
  if [[ "$actual" != "ledger_runtime" ]]; then
    provision_die "OS user ledger did not reach role ledger_runtime (got '${actual}')"
  fi

  actual="$(runuser -u ledger_migrator -- psql -d ledger -Atc 'select current_user')"
  if [[ "$actual" != "ledger_migrator" ]]; then
    provision_die "OS user ledger_migrator did not reach role ledger_migrator (got '${actual}')"
  fi

  actual="$(runuser -u "$GRAFANA_SERVICE_USER" -- psql -d ledger -U grafana_reader -Atc 'select current_user')"
  if [[ "$actual" != "grafana_reader" ]]; then
    provision_die "OS user ${GRAFANA_SERVICE_USER} did not reach role grafana_reader (got '${actual}')"
  fi

  actual="$(runuser -u ledger_backup -- psql -d ledger -Atc 'select current_user')"
  if [[ "$actual" != "ledger_backup" ]]; then
    provision_die "OS user ledger_backup did not reach role ledger_backup (got '${actual}')"
  fi

  if runuser -u ledger -- psql -d ledger -U ledger_migrator -Atc 'select 1' >/dev/null 2>&1; then
    provision_die "OS user ledger was able to reach role ledger_migrator; peer auth is not isolating roles"
  fi

  provision_log "PostgreSQL is socket-only, and every OS user reaches exactly its own role"
fi
