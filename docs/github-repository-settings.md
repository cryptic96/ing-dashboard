# GitHub repository settings the release pipeline relies on

The release workflow assumes a set of repository-side controls exist. None
of them can be set from inside the repository itself — they live in GitHub's
own settings and must be applied once, by hand or with the commands below,
before the first real release is cut. Every command uses `gh api` and the
placeholders `{owner}/{repo}` for the repository's own slug; substitute the
real values when running them. A "read back" command is listed next to each
control so the setting can be re-checked at any time without opening the
web UI.

## Tag ruleset restricting tag creation, update and deletion

**UI path:** Settings → Rules → Rulesets → New ruleset → New tag ruleset.

Only the repository's own admin should be able to create, move or delete a
release tag. This is what stops a tag from ever pointing anywhere the
release workflow didn't put it.

Apply:

```bash
gh api --method POST /repos/{owner}/{repo}/rulesets \
  -f name='release tags' \
  -f target='tag' \
  -f enforcement='active' \
  -f 'conditions[ref_name][include][]=refs/tags/v*' \
  -f 'rules[][type]=creation' \
  -f 'rules[][type]=update' \
  -f 'rules[][type]=deletion' \
  -f 'bypass_actors[][actor_type]=RepositoryRole' \
  -F 'bypass_actors[][actor_id]=5'
```

`actor_id: 5` is the repository role ID for Admin — the only bypass actor,
so nobody else can create, move or delete a `v*` tag.

Read back:

```bash
gh api /repos/{owner}/{repo}/rulesets --jq '.[] | select(.name == "release tags")'
```

## Deploy environment with a required reviewer and a tag-only deployment policy

**UI path:** Settings → Environments → New environment (name it `deploy`) →
configure required reviewers and deployment branches and tags.

The publish job in the release workflow targets the `deploy` environment.
Nothing in that job runs until this reviewer approves it, and the
environment only accepts deployments from a tag matching a release, never
from a branch.

Apply:

```bash
gh api --method PUT /repos/{owner}/{repo}/environments/deploy \
  -F 'reviewers[][type]=User' \
  -F 'reviewers[][id]=<operator-user-id>' \
  -F 'prevent_self_review=false' \
  -F 'deployment_branch_policy[protected_branches]=false' \
  -F 'deployment_branch_policy[custom_branch_policies]=true'

gh api --method POST /repos/{owner}/{repo}/environments/deploy/deployment-branch-policies \
  -f name='v*.*.*' \
  -f type='tag'
```

`prevent_self_review: false` is deliberate: with a single operator, nobody
else exists to approve the deployment, so self-review must stay allowed or
every release would be permanently stuck waiting for a second person.
`<operator-user-id>` is the numeric user ID of the account that will
approve deployments — look it up with `gh api /users/<username> --jq .id`.

Read back:

```bash
gh api /repos/{owner}/{repo}/environments/deploy
gh api /repos/{owner}/{repo}/environments/deploy/deployment-branch-policies
```

## Approval required for all outside contributors' workflow runs

**UI path:** Settings → Actions → General → Fork pull request workflows →
"Require approval for all outside collaborators".

The default only requires approval from first-time contributors. Since
this repository is public, every workflow run triggered by a pull request
from an account without write access must wait for an explicit approval,
every time — not just their first pull request.

Apply:

```bash
gh api --method PUT /repos/{owner}/{repo}/actions/permissions/fork-pr-contributor-approval \
  -f approval_policy='all_external_contributors'
```

Read back:

```bash
gh api /repos/{owner}/{repo}/actions/permissions/fork-pr-contributor-approval
```

## Read-only default token permissions, no pull request approvals

**UI path:** Settings → Actions → General → Workflow permissions.

Every workflow's default `GITHUB_TOKEN` should start with read-only access
unless a job explicitly requests more (as the release workflow's `build`
and `publish` jobs do, scoped to exactly what each needs). Workflows must
also never be able to approve a pull request themselves.

Apply:

```bash
gh api --method PUT /repos/{owner}/{repo}/actions/permissions/workflow \
  -f default_workflow_permissions='read' \
  -F can_approve_pull_request_reviews=false
```

Read back:

```bash
gh api /repos/{owner}/{repo}/actions/permissions/workflow
```

## Actions must be pinned to a full-length commit SHA

**UI path:** Settings → Actions → General → "Require actions to be pinned
to a full-length commit SHA".

This is the repository-side backstop behind the lint suite's own hash-pin
policy — even a change that slipped past local checks cannot register a
mutable-tag action reference against this repository.

Apply (read the current settings first so `allowed_actions` isn't
accidentally reset):

```bash
gh api /repos/{owner}/{repo}/actions/permissions

gh api --method PUT /repos/{owner}/{repo}/actions/permissions \
  -F enabled=true \
  -f allowed_actions='all' \
  -F sha_pinning_required=true
```

Read back:

```bash
gh api /repos/{owner}/{repo}/actions/permissions
```

## Secret scanning, push protection and Dependabot security updates

**UI path:** Settings → Code security → Secret scanning and Dependabot.

Apply:

```bash
gh api --method PATCH /repos/{owner}/{repo} \
  -F 'security_and_analysis[secret_scanning][status]=enabled' \
  -F 'security_and_analysis[secret_scanning_push_protection][status]=enabled' \
  -F 'security_and_analysis[dependabot_security_updates][status]=enabled'
```

Read back:

```bash
gh api /repos/{owner}/{repo} --jq .security_and_analysis
```

## Dependabot alerts

**UI path:** Settings → Code security → Dependabot alerts.

This is the alert feed itself (a vulnerable dependency is reported), which
is distinct from the security updates setting above (an automatic pull
request fixing it).

Apply:

```bash
gh api --method PUT /repos/{owner}/{repo}/vulnerability-alerts
```

Read back:

```bash
gh api /repos/{owner}/{repo}/vulnerability-alerts
```

A `204` response means alerts are enabled; a `404` means they are not.

## Immutable releases

**UI path:** Settings → General → "Enable immutable releases".

Once a release is published, its tag, its assets and its attestation can
no longer be altered or deleted, only superseded by a new release. This is
what makes the offline attestation check in `docs/releasing.md`
trustworthy over time — a published release can't quietly change under
anyone.

Apply:

```bash
gh api --method PUT /repos/{owner}/{repo}/immutable-releases
```

Read back:

```bash
gh api /repos/{owner}/{repo}/immutable-releases
```

## No registered self-hosted runners

**UI path:** Settings → Actions → Runners.

Nothing in this repository's CI or release pipeline ever runs on
self-hosted infrastructure — every job in every workflow targets a
GitHub-hosted runner. There is no setting to "turn off" here; this is a
standing fact to re-check periodically, since a self-hosted runner
registered later (even unintentionally) would reintroduce exactly the risk
this pipeline was designed to avoid.

Read back (should always return an empty list):

```bash
gh api /repos/{owner}/{repo}/actions/runners --jq '.runners'
```

## Optional: account-level commit metadata

**UI path:** account Settings → Emails → "Keep my email addresses
private", then set the resulting `@users.noreply.github.com` address as
the local `git config user.email`.

Every commit and tag pushed to this public repository has its author
metadata visible to anyone who clones it. Using GitHub's private commit
email setting means a real personal address never enters git history in
the first place — safer than trying to scrub it out later.
