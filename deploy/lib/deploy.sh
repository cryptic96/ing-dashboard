#!/usr/bin/env bash
# Verification, activation, rollback and reporting functions for the deploy
# installer. Sourced, never executed directly.

if [ -n "${LEDGER_DEPLOY_SH_LOADED:-}" ]; then
  return 0
fi
LEDGER_DEPLOY_SH_LOADED=1

# Verifies a downloaded release artifact against the Sigstore bundle
# published with it. No GitHub API call is made and no GitHub credential is
# read or used: gh runs with GH_TOKEN, GITHUB_TOKEN and GH_ENTERPRISE_TOKEN
# unset and a fresh, empty GH_CONFIG_DIR. gh does fetch Sigstore's public
# trust root, so an unreachable Sigstore instance fails verification rather
# than skipping it. On success, prints the certificate's
# sourceRepositoryDigest; returns non-zero on any failure.
ledger_verify_attestation() {
  local artifact="$1"
  local bundle="$2"
  local repo="$3"
  local signer_workflow="$4"
  local source_ref="$5"

  local gh_config_dir
  gh_config_dir="$(mktemp -d)"

  local output
  if ! output=$(env -u GH_TOKEN -u GITHUB_TOKEN -u GH_ENTERPRISE_TOKEN \
      GH_CONFIG_DIR="$gh_config_dir" \
      gh attestation verify "$artifact" \
        --bundle "$bundle" \
        --repo "$repo" \
        --signer-workflow "${repo}/${signer_workflow}" \
        --source-ref "$source_ref" \
        --deny-self-hosted-runners \
        --format json 2>&1); then
    ledger_log "attestation verification failed for $artifact: $output"
    rm -rf "$gh_config_dir"
    return 1
  fi
  rm -rf "$gh_config_dir"

  local digest
  digest="$(printf '%s' "$output" | jq -r '.[0].verificationResult.signature.certificate.sourceRepositoryDigest // empty')"
  if [ -z "$digest" ]; then
    ledger_log "attestation verification produced no sourceRepositoryDigest for $artifact"
    return 1
  fi
  printf '%s' "$digest"
}

# Confirms an attested commit SHA is identical to or an ancestor of BRANCH on
# REPO, using the unauthenticated GitHub compare API (no Authorization
# header is ever sent). Fails on any other status or an unreachable API.
ledger_commit_on_branch() {
  local repo="$1"
  local sha="$2"
  local branch="$3"

  local response
  if ! response=$(curl --fail --silent --show-error --max-time 30 \
      "https://api.github.com/repos/${repo}/compare/${branch}...${sha}" 2>&1); then
    ledger_log "commit reachability check failed for ${repo}@${sha}: $response"
    return 1
  fi

  local status
  status="$(printf '%s' "$response" | jq -r '.status // empty')"
  case "$status" in
    identical|behind)
      return 0
      ;;
    *)
      ledger_log "commit ${sha} on ${repo} is not on ${branch} (compare status: ${status:-unknown})"
      return 1
      ;;
  esac
}

# Reads the newest published release tag for REPO from the unauthenticated
# releases/latest endpoint.
ledger_fetch_latest_tag() {
  local repo="$1"

  local response
  if ! response=$(curl --fail --silent --show-error --max-time 30 \
      "https://api.github.com/repos/${repo}/releases/latest" 2>&1); then
    ledger_log "failed to fetch the latest release for ${repo}: $response"
    return 1
  fi

  printf '%s' "$response" | jq -r '.tag_name // empty'
}

# Records the outcome of a poll attempt (SUCCESS is 1 or 0, TIMESTAMP a Unix
# time) into the shared deploy textfile metrics, alongside any install
# outcome already recorded there.
ledger_write_deploy_poll_metrics() {
  local success="$1"
  local timestamp="$2"

  ledger_write_textfile_metrics "ledger_deploy" "$(cat <<EOF_METRICS
# HELP ledger_deploy_last_poll_timestamp_seconds Unix timestamp of the last poll attempt.
# TYPE ledger_deploy_last_poll_timestamp_seconds gauge
ledger_deploy_last_poll_timestamp_seconds ${timestamp}
# HELP ledger_deploy_last_poll_success Whether the last poll attempt could determine the latest published release.
# TYPE ledger_deploy_last_poll_success gauge
ledger_deploy_last_poll_success ${success}
EOF_METRICS
)"
}

# Records the outcome of an install attempt into the shared deploy textfile
# metrics. RESULT is "success", "rolled_back" or "failed"; TIMESTAMP a Unix
# time; VERSION the installed (or reactivated) plain version string.
ledger_write_deploy_run_metrics() {
  local result="$1"
  local timestamp="$2"
  local version="$3"

  local success_value=0
  local rolled_back_value=0
  case "$result" in
    success) success_value=1 ;;
    rolled_back) success_value=1; rolled_back_value=1 ;;
    failed) success_value=0 ;;
  esac

  ledger_write_textfile_metrics "ledger_deploy" "$(cat <<EOF_METRICS
# HELP ledger_deploy_last_run_timestamp_seconds Unix timestamp of the last install attempt.
# TYPE ledger_deploy_last_run_timestamp_seconds gauge
ledger_deploy_last_run_timestamp_seconds ${timestamp}
# HELP ledger_deploy_last_run_success Whether the last install attempt left the deployment healthy.
# TYPE ledger_deploy_last_run_success gauge
ledger_deploy_last_run_success ${success_value}
# HELP ledger_deploy_last_run_rolled_back Whether the last install attempt rolled back to the previous release.
# TYPE ledger_deploy_last_run_rolled_back gauge
ledger_deploy_last_run_rolled_back ${rolled_back_value}
# HELP ledger_deploy_current_release_info Label-only series identifying the active release.
# TYPE ledger_deploy_current_release_info gauge
ledger_deploy_current_release_info{version="${version}"} 1
EOF_METRICS
)"
}

# Builds the notification email body for an install outcome. Contains only
# the version, result and timestamps that operations are permitted to see:
# no paths, no connection strings, no key material.
ledger_render_deploy_email_body() {
  local version="$1"
  local result="$2"
  local started_at="$3"
  local finished_at="$4"

  cat <<EOF_BODY
version: ${version}
result: ${result}
started: ${started_at}
finished: ${finished_at}
EOF_BODY
}

# Computes the migrations present in a release manifest but not yet applied
# to the database. MANIFEST_FILE is the release's release-manifest.json;
# APPLIED_FILE lists already-applied migration ids, one per line (an absent
# or empty file means none applied). Prints the pending migration ids, one
# per line, oldest first. Returns 2 (rather than printing anything) when the
# database has applied a migration this release's manifest does not list.
ledger_pending_migrations() {
  local manifest_file="$1"
  local applied_file="$2"

  local manifest_migrations=()
  local line
  while IFS= read -r line; do
    [ -n "$line" ] && manifest_migrations+=("$line")
  done < <(jq -r '.migrations[]' "$manifest_file")

  local applied_migrations=()
  if [ -f "$applied_file" ]; then
    while IFS= read -r line; do
      [ -n "$line" ] && applied_migrations+=("$line")
    done < "$applied_file"
  fi

  local applied_migration manifest_migration found
  for applied_migration in "${applied_migrations[@]}"; do
    found=0
    for manifest_migration in "${manifest_migrations[@]}"; do
      if [ "$manifest_migration" = "$applied_migration" ]; then
        found=1
        break
      fi
    done
    if [ "$found" -eq 0 ]; then
      ledger_log "database has applied migration '${applied_migration}', unknown to this release's manifest"
      return 2
    fi
  done

  local pending=()
  for manifest_migration in "${manifest_migrations[@]}"; do
    found=0
    for applied_migration in "${applied_migrations[@]}"; do
      if [ "$manifest_migration" = "$applied_migration" ]; then
        found=1
        break
      fi
    done
    [ "$found" -eq 0 ] && pending+=("$manifest_migration")
  done

  if [ "${#pending[@]}" -gt 0 ]; then
    printf '%s\n' "${pending[@]}"
  fi
}

# Reads the migrations already applied to the ledger database, as the
# migrator OS user, into a plain-text file (one migration id per line). An
# absent history table is treated as no migrations applied.
ledger_read_applied_migrations() {
  local destination="$1"

  # A fresh database has no history table yet, which means nothing is
  # applied. Any other failure to read it stops the deploy: treating an
  # unreadable history as empty would mark every migration as pending.
  local history_exists
  if ! history_exists="$(runuser -u ledger_migrator -- psql -d ledger -AtX \
      -c "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL" 2>/dev/null)"; then
    ledger_die "could not query the database for its migration history"
  fi
  if [ "$history_exists" != "t" ]; then
    : > "$destination"
    return 0
  fi

  # EF keeps its own column names on the history table; the snake_case
  # naming convention does not apply to it.
  if ! runuser -u ledger_migrator -- psql -d ledger -AtX \
      -c 'SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId"' \
      > "$destination" 2>/dev/null; then
    ledger_die "could not read the applied migrations from __EFMigrationsHistory"
  fi
}

# Atomically repoints CURRENT_LINK at RELEASES_DIR/VERSION. Records the
# previously active version (if any) into STATE_DIR/previous before
# swapping. The new symlink is built under a temporary name and moved into
# place with mv -T so the swap is a single atomic rename.
ledger_activate_release() {
  local version="$1"
  local releases_dir="$2"
  local current_link="$3"
  local state_dir="$4"

  local target="${releases_dir}/${version}"
  [ -d "$target" ] || ledger_die "cannot activate ${version}: ${target} does not exist"

  mkdir -p "$state_dir"
  if [ -L "$current_link" ]; then
    local previous_version
    previous_version="$(basename "$(readlink -f "$current_link")")"
    printf '%s\n' "$previous_version" > "${state_dir}/previous"
  fi

  local tmp_link
  tmp_link="$(mktemp -u "${current_link}.XXXXXX")"
  ln -s "$target" "$tmp_link"
  mv -T "$tmp_link" "$current_link"
}

# Decides the recovery action after a failed post-start health check.
# MIGRATED is "1" when this install ran a migration. Prints "rollback" when
# it is safe to reactivate the previous release automatically, or
# "fail-without-rollback" when a migration ran and EF migrations only run
# forward, so the new release is left in place and the failure must be
# investigated by hand.
ledger_rollback_decision() {
  local migrated="$1"

  if [ "$migrated" = "1" ] || [ "$migrated" = "true" ]; then
    printf 'fail-without-rollback\n'
  else
    printf 'rollback\n'
  fi
}

# Removes release directories beyond the configured KEEP count, oldest
# first, never removing the active or previous release even if that leaves
# more than KEEP directories on disk.
ledger_prune_releases() {
  local releases_dir="$1"
  local current_link="$2"
  local state_dir="$3"
  local keep="$4"

  local active_version="" previous_version=""
  if [ -L "$current_link" ]; then
    active_version="$(basename "$(readlink -f "$current_link")")"
  fi
  if [ -f "${state_dir}/previous" ]; then
    previous_version="$(cat "${state_dir}/previous")"
  fi

  local versions=()
  local entry
  for entry in "${releases_dir}"/*; do
    [ -d "$entry" ] || continue
    case "$(basename "$entry")" in
      .staging-*) continue ;;
    esac
    versions+=("$(basename "$entry")")
  done
  [ "${#versions[@]}" -gt 0 ] || return 0

  local sorted=()
  mapfile -t sorted < <(printf '%s\n' "${versions[@]}" | sort -V)

  local total="${#sorted[@]}"
  local to_delete_count=$(( total > keep ? total - keep : 0 ))
  [ "$to_delete_count" -gt 0 ] || return 0

  local deleted=0
  local v
  for v in "${sorted[@]}"; do
    [ "$deleted" -lt "$to_delete_count" ] || break
    if [ "$v" = "$active_version" ] || [ "$v" = "$previous_version" ]; then
      continue
    fi
    rm -rf "${releases_dir:?}/${v}"
    deleted=$((deleted + 1))
  done
}

# Waits for the ops endpoint to report a healthy app at VERSION, and (when
# requested) for Grafana and Prometheus to report ready. Returns non-zero if
# the deadline is reached first.
ledger_wait_for_health() {
  local ops_url="$1"
  local version="$2"
  local timeout_seconds="$3"
  local check_grafana="${4:-0}"
  local check_prometheus="${5:-0}"

  local deadline=$(( $(date +%s) + timeout_seconds ))
  while [ "$(date +%s)" -lt "$deadline" ]; do
    local healthy=0
    local body
    if body=$(curl --fail --silent --show-error --max-time 5 "${ops_url}/health" 2>/dev/null) \
        && [ "$body" = "Healthy" ]; then
      if metrics=$(curl --fail --silent --show-error --max-time 5 "${ops_url}/metrics" 2>/dev/null) \
          && printf '%s' "$metrics" | grep -q "ledger_build_info{.*version=\"${version}\".*}"; then
        healthy=1
      fi
    fi

    if [ "$healthy" -eq 1 ] && [ "$check_grafana" = "1" ]; then
      curl --fail --silent --show-error --max-time 5 "http://127.0.0.1:3000/api/health" >/dev/null 2>&1 || healthy=0
    fi
    if [ "$healthy" -eq 1 ] && [ "$check_prometheus" = "1" ]; then
      curl --fail --silent --show-error --max-time 5 "http://127.0.0.1:9090/-/ready" >/dev/null 2>&1 || healthy=0
    fi

    if [ "$healthy" -eq 1 ]; then
      return 0
    fi
    sleep 2
  done

  return 1
}

# Installs the active release's Grafana and Prometheus provisioning files.
# Prometheus's configuration is validated with promtool before it is
# installed. Returns via the two reference-name variables whether each
# service's configuration actually changed, so the caller only restarts or
# reloads a service whose configuration content changed.
ledger_install_provisioning() {
  local release_dir="$1"
  local -n _ledger_grafana_changed="$2"
  local -n _ledger_prometheus_changed="$3"

  _ledger_grafana_changed=0
  _ledger_prometheus_changed=0

  local grafana_ini="${release_dir}/deploy/provisioning/grafana/grafana.ini"
  if [ -f "$grafana_ini" ]; then
    if ! cmp -s "$grafana_ini" /etc/grafana/grafana.ini 2>/dev/null; then
      install -o root -g grafana -m 640 "$grafana_ini" /etc/grafana/grafana.ini
      _ledger_grafana_changed=1
    fi
    local provisioning_src="${release_dir}/deploy/provisioning/grafana/provisioning"
    if [ -d "$provisioning_src" ]; then
      rm -rf /etc/grafana/provisioning
      cp -a "$provisioning_src" /etc/grafana/provisioning
      _ledger_grafana_changed=1
    fi
  fi

  local prometheus_yml="${release_dir}/deploy/provisioning/prometheus/prometheus.yml"
  if [ -f "$prometheus_yml" ]; then
    promtool check config "$prometheus_yml" \
      || ledger_die "prometheus.yml from ${release_dir} failed promtool check config"
    if ! cmp -s "$prometheus_yml" /etc/prometheus/prometheus.yml 2>/dev/null; then
      install -o root -g prometheus -m 640 "$prometheus_yml" /etc/prometheus/prometheus.yml
      _ledger_prometheus_changed=1
    fi
  fi
}

# Orchestrates the full activation of an already-verified release: make sure
# the certificate the application presents to the reverse proxy exists,
# unpack, migrate (with a pre-migration backup) when needed, atomically
# activate, install provisioning, restart, health-check, and automatically
# roll back unless a migration ran. Always reports the outcome by textfile
# metrics and email, and prunes old releases on success.
#
# The certificate comes first on purpose: the application refuses to start
# without it, and a release that migrated the database cannot be rolled back
# automatically, so a missing certificate must stop the install before
# anything is changed.
ledger_install_verified_release() {
  local tag="$1"
  local version="$2"
  local artifact="$3"
  local active_version="$4"
  local releases_dir="$5"
  local current_link="$6"
  local state_dir="$7"
  local keep_releases="$8"
  local ops_url="$9"
  local health_timeout="${10}"

  local started_at
  started_at="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"

  ledger_ensure_backend_tls

  local staging_dir="${releases_dir}/.staging-${version}"
  rm -rf "$staging_dir"
  mkdir -p "$staging_dir"

  if ! unzip -q "$artifact" -d "$staging_dir"; then
    rm -rf "$staging_dir"
    ledger_die "failed to unpack the verified artifact for ${tag}"
  fi

  local manifest_version
  manifest_version="$(jq -r '.version // empty' "${staging_dir}/release-manifest.json" 2>/dev/null || true)"
  if [ "$manifest_version" != "$version" ]; then
    rm -rf "$staging_dir"
    ledger_die "release-manifest.json version '${manifest_version}' does not match tag ${tag}"
  fi

  mkdir -p "$releases_dir"
  # A directory for this version that is not the active release was left by
  # an earlier attempt that failed before activating it (for example at the
  # pre-migration backup). Releases are immutable, so the freshly verified
  # copy replaces it; the active release itself is never replaced.
  local target="${releases_dir}/${version}"
  if [ -e "$target" ]; then
    if [ "$(readlink -f "$target")" = "$(readlink -f "$current_link" 2>/dev/null || true)" ]; then
      rm -rf "$staging_dir"
      ledger_die "${tag} is already the active release"
    fi
    ledger_log "replacing ${target}, left by an earlier attempt that did not activate it"
    rm -rf "$target"
  fi
  mv -T "$staging_dir" "$target"

  local applied_file
  applied_file="$(mktemp)"
  ledger_read_applied_migrations "$applied_file"

  local pending
  local pending_status=0
  pending="$(ledger_pending_migrations "${releases_dir}/${version}/release-manifest.json" "$applied_file")" \
    || pending_status=$?
  rm -f "$applied_file"

  if [ "$pending_status" -eq 2 ]; then
    ledger_die "database has migrations not present in ${tag}'s manifest, refusing to install"
  fi

  local migrated=0
  if [ -n "$pending" ]; then
    migrated=1
    if ! systemctl start --wait ledger-backup@pre-migration.service; then
      ledger_die "pre-migration backup failed, aborting before touching the running release"
    fi
    systemctl stop ledger.service

    local extract_dir
    extract_dir="$(mktemp -d)"
    chown ledger_migrator:ledger_migrator "$extract_dir" 2>/dev/null || true

    if ! DOTNET_BUNDLE_EXTRACT_BASE_DIR="$extract_dir" runuser -u ledger_migrator -- \
        "${releases_dir}/${version}/efbundle" \
        --connection "Host=/var/run/postgresql;Database=ledger;Username=ledger_migrator"; then
      rm -rf "$extract_dir"
      ledger_die "migration bundle failed for ${tag}, deployment left stopped for manual investigation"
    fi
    rm -rf "$extract_dir"
  fi

  ledger_activate_release "$version" "$releases_dir" "$current_link" "$state_dir"

  local grafana_changed=0
  local prometheus_changed=0
  ledger_install_provisioning "${releases_dir}/${version}" grafana_changed prometheus_changed

  systemctl restart ledger.service
  if [ "$grafana_changed" = "1" ]; then
    systemctl restart grafana-server
  fi
  if [ "$prometheus_changed" = "1" ]; then
    systemctl reload prometheus
  fi

  local result="success"
  if ! ledger_wait_for_health "$ops_url" "$version" "$health_timeout" "$grafana_changed" "$prometheus_changed"; then
    local decision
    decision="$(ledger_rollback_decision "$migrated")"
    if [ "$decision" = "rollback" ] && [ -n "$active_version" ]; then
      ledger_activate_release "$active_version" "$releases_dir" "$current_link" "$state_dir"
      local rollback_grafana=0
      local rollback_prometheus=0
      ledger_install_provisioning "${releases_dir}/${active_version}" rollback_grafana rollback_prometheus
      systemctl restart ledger.service
      if [ "$rollback_grafana" = "1" ]; then
        systemctl restart grafana-server
      fi
      if [ "$rollback_prometheus" = "1" ]; then
        systemctl reload prometheus
      fi
      ledger_wait_for_health "$ops_url" "$active_version" "$health_timeout" "$rollback_grafana" "$rollback_prometheus" || true
      result="rolled_back"
    else
      result="failed"
    fi
  fi

  local finished_at
  finished_at="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
  local reported_version="$version"
  [ "$result" = "rolled_back" ] && reported_version="$active_version"

  ledger_write_deploy_run_metrics "$result" "$(date -u +%s)" "$reported_version"
  ledger_notify_email "deploy ${result}: ${tag}" \
    "$(ledger_render_deploy_email_body "$tag" "$result" "$started_at" "$finished_at")"

  if [ "$result" = "success" ]; then
    ledger_prune_releases "$releases_dir" "$current_link" "$state_dir" "$keep_releases"
  fi

  case "$result" in
    success|rolled_back) return 0 ;;
    *) return 1 ;;
  esac
}

# Reactivates an existing releases/VERSION directory (a numeric version, no
# leading v) after confirming the database's applied migrations are a
# subset of that release's manifest, reinstalls its provisioning, restarts
# and health-checks it.
ledger_rollback_release() {
  local version="$1"
  local releases_dir="$2"
  local current_link="$3"
  local state_dir="$4"
  local ops_url="$5"
  local health_timeout="$6"

  local target="${releases_dir}/${version}"
  [ -d "$target" ] || ledger_die "cannot roll back to ${version}: ${target} does not exist"

  local applied_file
  applied_file="$(mktemp)"
  ledger_read_applied_migrations "$applied_file"

  local pending_status=0
  ledger_pending_migrations "${target}/release-manifest.json" "$applied_file" >/dev/null || pending_status=$?
  rm -f "$applied_file"

  if [ "$pending_status" -eq 2 ]; then
    ledger_die "refusing to roll back to ${version}: the database has migrations not present in its manifest"
  fi

  ledger_activate_release "$version" "$releases_dir" "$current_link" "$state_dir"

  local grafana_changed=0
  local prometheus_changed=0
  ledger_install_provisioning "$target" grafana_changed prometheus_changed

  systemctl restart ledger.service
  if [ "$grafana_changed" = "1" ]; then
    systemctl restart grafana-server
  fi
  if [ "$prometheus_changed" = "1" ]; then
    systemctl reload prometheus
  fi

  local result="success"
  if ! ledger_wait_for_health "$ops_url" "$version" "$health_timeout" "$grafana_changed" "$prometheus_changed"; then
    result="failed"
  fi

  ledger_write_deploy_run_metrics "$result" "$(date -u +%s)" "$version"
  ledger_notify_email "rollback ${result}: ${version}" \
    "$(ledger_render_deploy_email_body "$version" "$result" "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$(date -u '+%Y-%m-%dT%H:%M:%SZ')")"

  [ "$result" = "success" ]
}
