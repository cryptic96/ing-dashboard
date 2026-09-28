#!/usr/bin/env bash
set -euo pipefail

# Read-only verification of the repository-side controls the release
# pipeline depends on. Every check below is a GET call through gh api; none
# of them ever changes a setting. Prints one PASS/FAIL line per control and
# exits 1 if any control failed. Meant to be re-run at any time by whoever
# holds the repository owner's gh login, on a workstation only -- never
# installed on the deployment target.

OVERALL=0

pass() {
  printf 'PASS: %s\n' "$1"
}

fail() {
  printf 'FAIL: %s\n' "$1"
  OVERALL=1
}

REPO="$(gh repo view --json nameWithOwner --jq '.nameWithOwner')"

check_tag_ruleset() {
  local label="tag ruleset restricts creation, update and deletion of v* tags to the admin role"
  local ids
  if ! ids="$(gh api "repos/$REPO/rulesets" --jq '.[] | select(.target == "tag") | .id' 2>/dev/null)"; then
    fail "$label (could not list rulesets)"
    return
  fi
  if [ -z "$ids" ]; then
    fail "$label (no ruleset targets tags)"
    return
  fi
  local id detail matched=0
  for id in $ids; do
    detail="$(gh api "repos/$REPO/rulesets/$id" 2>/dev/null)" || continue
    if printf '%s' "$detail" | jq -e '
        .enforcement == "active"
        and ((.conditions.ref_name.include // []) | any(. == "refs/tags/v*"))
        and ((.rules // []) | any(.type == "creation"))
        and ((.rules // []) | any(.type == "update"))
        and ((.rules // []) | any(.type == "deletion"))
        and ((.bypass_actors // []) | length == 1)
        and ((.bypass_actors // [])[0].actor_type == "RepositoryRole")
        and ((.bypass_actors // [])[0].actor_id == 5)
      ' >/dev/null; then
      matched=1
      break
    fi
  done
  if [ "$matched" -eq 1 ]; then
    pass "$label"
  else
    fail "$label"
  fi
}

check_deploy_environment_reviewer() {
  local label="deploy environment requires at least one reviewer"
  local detail
  if ! detail="$(gh api "repos/$REPO/environments/deploy" 2>/dev/null)"; then
    fail "$label (environment 'deploy' not found)"
    return
  fi
  if printf '%s' "$detail" | jq -e '
      (.protection_rules // []) | any(
        .type == "required_reviewers" and ((.reviewers // []) | length) >= 1
      )
    ' >/dev/null; then
    pass "$label"
  else
    fail "$label"
  fi
}

check_deploy_environment_tag_policy() {
  local label="deploy environment's deployment policy is limited to v*.*.* tags"
  local env_detail policies
  if ! env_detail="$(gh api "repos/$REPO/environments/deploy" 2>/dev/null)"; then
    fail "$label (environment 'deploy' not found)"
    return
  fi
  if ! printf '%s' "$env_detail" | jq -e '.deployment_branch_policy.custom_branch_policies == true' >/dev/null; then
    fail "$label (custom deployment branch/tag policies not enabled)"
    return
  fi
  if ! policies="$(gh api "repos/$REPO/environments/deploy/deployment-branch-policies" 2>/dev/null)"; then
    fail "$label (could not read deployment-branch-policies)"
    return
  fi
  if printf '%s' "$policies" | jq -e '
      (.branch_policies // []) | any(.type == "tag" and .name == "v*.*.*")
    ' >/dev/null; then
    pass "$label"
  else
    fail "$label"
  fi
}

check_fork_pr_approval() {
  local label="approval is required for every outside contributor's workflow run"
  local detail
  if ! detail="$(gh api "repos/$REPO/actions/permissions/fork-pr-contributor-approval" 2>/dev/null)"; then
    fail "$label (could not read fork-pr-contributor-approval)"
    return
  fi
  if printf '%s' "$detail" | jq -e '.approval_policy == "all_external_contributors"' >/dev/null; then
    pass "$label"
  else
    fail "$label"
  fi
}

check_default_workflow_permissions() {
  local label="default workflow token is read-only and cannot approve pull requests"
  local detail
  if ! detail="$(gh api "repos/$REPO/actions/permissions/workflow" 2>/dev/null)"; then
    fail "$label (could not read actions/permissions/workflow)"
    return
  fi
  if printf '%s' "$detail" | jq -e '
      .default_workflow_permissions == "read" and .can_approve_pull_request_reviews == false
    ' >/dev/null; then
    pass "$label"
  else
    fail "$label"
  fi
}

check_sha_pinning_required() {
  local label="actions must be pinned to a full-length commit SHA"
  local detail
  if ! detail="$(gh api "repos/$REPO/actions/permissions" 2>/dev/null)"; then
    fail "$label (could not read actions/permissions)"
    return
  fi
  if printf '%s' "$detail" | jq -e '.sha_pinning_required == true' >/dev/null; then
    pass "$label"
  else
    fail "$label"
  fi
}

check_secret_scanning() {
  local label="secret scanning and push protection are enabled"
  local detail
  if ! detail="$(gh api "repos/$REPO" --jq '.security_and_analysis' 2>/dev/null)"; then
    fail "$label (could not read security_and_analysis)"
    return
  fi
  if printf '%s' "$detail" | jq -e '
      .secret_scanning.status == "enabled" and .secret_scanning_push_protection.status == "enabled"
    ' >/dev/null; then
    pass "$label"
  else
    fail "$label"
  fi
}

check_dependabot_security_updates() {
  local label="Dependabot security updates are enabled"
  local detail
  if ! detail="$(gh api "repos/$REPO" --jq '.security_and_analysis' 2>/dev/null)"; then
    fail "$label (could not read security_and_analysis)"
    return
  fi
  if printf '%s' "$detail" | jq -e '.dependabot_security_updates.status == "enabled"' >/dev/null; then
    pass "$label"
  else
    fail "$label"
  fi
}

check_vulnerability_alerts() {
  local label="Dependabot vulnerability alerts are enabled"
  # A 204 response means enabled; a 404 means disabled. gh api's own exit
  # status already reflects that distinction (0 for 2xx, non-zero for 4xx).
  if gh api "repos/$REPO/vulnerability-alerts" >/dev/null 2>&1; then
    pass "$label"
  else
    fail "$label"
  fi
}

check_immutable_releases() {
  local label="immutable releases are enabled"
  local detail
  if ! detail="$(gh api "repos/$REPO/immutable-releases" 2>/dev/null)"; then
    fail "$label (could not read immutable-releases)"
    return
  fi
  if printf '%s' "$detail" | jq -e '.enabled == true' >/dev/null; then
    pass "$label"
  else
    fail "$label"
  fi
}

check_no_registered_runners() {
  local label="zero self-hosted runners are registered"
  local detail
  if ! detail="$(gh api "repos/$REPO/actions/runners" 2>/dev/null)"; then
    fail "$label (could not read actions/runners)"
    return
  fi
  if printf '%s' "$detail" | jq -e '((.runners // []) | length) == 0' >/dev/null; then
    pass "$label"
  else
    fail "$label"
  fi
}

check_tag_ruleset
check_deploy_environment_reviewer
check_deploy_environment_tag_policy
check_fork_pr_approval
check_default_workflow_permissions
check_sha_pinning_required
check_secret_scanning
check_dependabot_security_updates
check_vulnerability_alerts
check_immutable_releases
check_no_registered_runners

exit "$OVERALL"
