#!/usr/bin/env bash
# Verification, activation, rollback and reporting functions for the deploy
# installer. Sourced, never executed directly.

if [ -n "${LEDGER_DEPLOY_SH_LOADED:-}" ]; then
  return 0
fi
LEDGER_DEPLOY_SH_LOADED=1

# Verifies a downloaded release artifact against its Sigstore bundle, fully
# offline: no GitHub API call is made and no GitHub credential is read or
# used. Runs with GH_TOKEN, GITHUB_TOKEN and GH_ENTERPRISE_TOKEN unset and a
# fresh, empty GH_CONFIG_DIR so no ambient credential can influence the
# result. On success, prints the certificate's sourceRepositoryDigest.
# Returns non-zero on any failure, including an unreachable Sigstore
# instance.
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
