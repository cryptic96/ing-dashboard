---
phase: 01-secure-platform-release-pipeline
plan: 03
subsystem: infra
tags: [github-actions, sigstore, gh-cli, actionlint, zizmor, semver, attestation]

requires:
  - phase: 01-secure-platform-release-pipeline
    provides: "build/package-release.sh, build/lint.sh (workflows/repo-rules/secrets/shell/script-tests), ci.yml's pinned checkout/setup-dotnet SHAs and digest-pinned postgres service block"
provides:
  - "build/validate-release-tag.sh: strict vMAJOR.MINOR.PATCH + main-reachability + GITHUB_SHA-match gate, env-only input"
  - ".github/workflows/release.yml: tag-triggered build (validate, package, test, attest, draft) and deploy-environment-gated publish (re-verify, publish)"
  - "docs/releasing.md and docs/github-repository-settings.md: operator-facing release and GitHub-settings documentation"
affects: ["the go-live plan that cuts the first real release and applies the documented GitHub-side settings"]

actuals:
  tokens: 6141
  tasks: 2
  commits: 2

tech-stack:
  added:
    - actions/attest-build-provenance@4d101475d8b20a2381f78447822ac1eab6504dd8 (v4.2.2)
  patterns:
    - "Tag value reaches shell only through env:; every git/gh call in release.yml quotes the resulting shell variable, never a bare ${{ }} expression"
    - "Draft-then-environment-gated-publish: the build job never marks a release public; only the publish job (bound to the deploy environment) does, after re-proving the attestation"

key-files:
  created:
    - build/validate-release-tag.sh
    - build/tests/validate-release-tag-test.sh
    - .github/workflows/release.yml
    - docs/releasing.md
    - docs/github-repository-settings.md
  modified: []

decisions:
  - "Used gh release create/upload/download/edit (pre-installed on GitHub-hosted runners) instead of a third-party release action, avoiding an extra action to pin and matching the plan's own action text"
  - "gh attestation verify's --source-ref uses refs/tags/$TAG, not refs/heads/main, per the plan's own recorded assumption that a tag-triggered build's attestation certificate records the triggering ref (refs/tags/v{version}), never refs/heads/main; main-membership is proven separately by validate-release-tag.sh's merge-base check"
  - "The draft-release step distinguishes an existing draft (upload --clobber) from an existing published release (fail) via gh release view --json isDraft, since gh release create refuses outright if the tag's release already exists in either state"

requirements-completed: [SEC-07, SEC-08, SEC-09]

coverage:
  - id: D1
    description: "build/validate-release-tag.sh rejects every malformed or off-main tag and prints version=X.Y.Z on success, reading TAG only from the environment"
    requirement: "SEC-07"
    verification:
      - kind: integration
        ref: "build/tests/validate-release-tag-test.sh (19 cases: accepted formats, rejected formats, GITHUB_SHA mismatch, unmerged-feature-branch rejection)"
        status: pass
    human_judgment: false
  - id: D2
    description: "release.yml's build job packages, tests, attests and creates a draft release; every uses: is SHA-pinned, workflow permissions are least-privilege, no run: block contains an expression, and no caches are used"
    requirement: "SEC-09"
    verification:
      - kind: other
        ref: "build/lint.sh workflows repo-rules secrets (actionlint + zizmor + repo-rules runs-on/comment scan + gitleaks full history)"
        status: pass
      - kind: other
        ref: "acceptance-criteria greps: environment: deploy (1), --draft/--draft=false, --deny-self-hosted-runners (1), --source-ref \"refs/tags/$TAG\" (1), sigstore.json (3), no run:-line expressions, no cache mentions, runs-on: ubuntu-24.04 (2)"
        status: pass
    human_judgment: false
  - id: D3
    description: "release.yml's publish job is gated on the deploy environment and re-verifies the draft artifact's attestation (signer workflow, source ref, no self-hosted runner) before publishing; nothing in the workflow reaches the LXC"
    requirement: "SEC-08"
    verification:
      - kind: other
        ref: "grep -c 'environment: deploy' == 1; grep -c -- '--draft=false' == 1; grep -c -- '--deny-self-hosted-runners' == 1; no deploy/install job present"
        status: pass
    human_judgment: false
  - id: D4
    description: "docs/github-repository-settings.md documents every GitHub-side control the pipeline depends on, each with a gh api apply command and a read-back command, verified against GitHub's own published OpenAPI schema"
    verification:
      - kind: other
        ref: "grep -ciE 'ruleset|required reviewer|outside contributor|push protection|immutable' docs/github-repository-settings.md == 12 (>=5 required)"
        status: pass
    human_judgment: true
    rationale: "The gh api commands are schema-verified against GitHub's published OpenAPI description but were never executed against a real repository (the orchestrator's override forbids changing GitHub settings from this worktree) — the operator must apply them once in the go-live plan and confirm each read-back matches."
  - id: D5
    description: "The real tag-triggered workflow run (build succeeding, environment approval gating publish, a real published release) is exercised end to end"
    verification: []
    human_judgment: true
    rationale: "Tags only exist on main and no push/workflow-trigger is permitted from this worktree per the orchestrator's override; this plan's own <verification> section defers the real run to the go-live plan."

duration: ~20min
completed: 2026-09-27
status: complete
---

# Phase 01 Plan 03: Tag-Triggered Release Workflow with Attestation and Gated Publish Summary

**A strict semver-and-main-reachability gate (`build/validate-release-tag.sh`) backs a two-job release workflow: `build` packages, tests, attests and drafts a release; `publish`, gated on the `deploy` environment, re-verifies the draft's attestation offline before making it public — plus an operator checklist for every GitHub-side setting the pipeline assumes.**

## Performance

- **Duration:** ~20 min
- **Tasks:** 2
- **Files created:** 5

## Accomplishments

- `build/validate-release-tag.sh` reads `TAG`/`GITHUB_SHA`/`MAIN_REF` from the environment only, rejects every malformed or non-main-reachable tag, and never echoes an unvalidated tag value — proven against 19 cases including a trailing newline, an embedded shell command, and a tag on an unmerged feature branch, in a throwaway git repository built and torn down by the test itself.
- `.github/workflows/release.yml`'s `build` job runs on `ubuntu-24.04`, reuses the pinned `checkout`/`setup-dotnet` actions and the digest-pinned PostgreSQL 18 service block from `ci.yml`, validates the tag before doing anything else, packages and tests the release, attests build provenance with `actions/attest-build-provenance`, and creates (or updates) a **draft** release carrying the zip, its checksum and its Sigstore bundle — never publishing directly.
- The `publish` job is bound to the `deploy` GitHub Environment (a human approval gate), downloads the draft's three assets with no checkout, checks the checksum, re-runs `gh attestation verify --bundle` with `--signer-workflow`, `--source-ref refs/tags/$TAG` and `--deny-self-hosted-runners`, and only then flips the release to published and latest. GitHub Actions execution ends there; nothing in either job ever reaches the household's server.
- `docs/releasing.md` gives a plain-language account of what a release is, how to cut one, what the pipeline refuses and why, and how anyone can verify a published release themselves with `gh attestation verify --bundle`.
- `docs/github-repository-settings.md` lists every repository-side control the pipeline assumes — the tag ruleset, the `deploy` environment's reviewer and tag-only deployment policy, outside-contributor workflow approval, read-only default workflow tokens, SHA-pinning enforcement, secret scanning/push protection/Dependabot, immutable releases, and the standing "no registered runners" fact — each with a `gh api` command to apply it and a matching command to read it back, verified against GitHub's own published REST API schema.

## Task Commits

1. **Task 1: Strict release-tag gate with tests** - `e508755` (feat)
2. **Task 2: Release workflow with attestation, draft release and environment-gated publish, plus operator docs** - `68c8c54` (feat)

## Files Created/Modified

- `build/validate-release-tag.sh` - Strict semver + main-reachability + commit-identity gate; env-only input, error-first exit
- `build/tests/validate-release-tag-test.sh` - Self-contained test building its own throwaway git repository (main + unmerged feature branch), covering every case in the plan's behavior block
- `.github/workflows/release.yml` - `build` (validate, package, test, attest, draft) and `publish` (deploy-environment-gated re-verify and publish) jobs
- `docs/releasing.md` - Operator-facing guide to cutting a release, what's refused, and self-verification
- `docs/github-repository-settings.md` - Every GitHub-side control the pipeline depends on, with apply/read-back `gh api` commands

## Decisions Made

See frontmatter `decisions:` for the full list. The one with the widest blast radius: `gh attestation verify`'s `--source-ref` argument is `refs/tags/$TAG`, not `refs/heads/main` — this follows the plan's own recorded assumption (a tag-triggered build's attestation certificate records the ref that triggered the build, which is the tag itself) and keeps the "was this built from main" question answered by `validate-release-tag.sh`'s separate `merge-base --is-ancestor` check instead.

## Deviations from Plan

None — plan executed exactly as written. Every acceptance-criteria grep, `build/lint.sh workflows repo-rules secrets`, and the full `build/lint.sh` (all five checks) were run and passed before this summary was written.

## Issues Encountered

- This sandbox's `grep` is `ugrep`, which (unlike GNU grep) treats a `$` not at the very end of the pattern as an anchor rather than literal in some contexts, causing a manual acceptance-criteria check (`--source-ref "refs/tags/$TAG"`) to falsely report zero matches. Confirmed the actual file content is correct by escaping the `$` for this local tool; GitHub-hosted runners use GNU grep and are unaffected. No code or doc change was needed — this was a false alarm in my own verification tooling, not a defect in the deliverable.

## User Setup Required

None from this worktree. `docs/github-repository-settings.md`'s `gh api` commands must be run once by the operator against the real repository before the first real release is cut — this is explicitly deferred to the go-live plan, consistent with this plan's own scope (author files and verify locally; no GitHub-side changes from here).

## Known Stubs

None. Every artifact the plan's `must_haves` lists is implemented and verified: the gate script and its test, the two-job workflow with attestation and gated publish, and both operator docs.

## Next Phase Readiness

- The go-live plan can cut the first real tag once the operator has applied `docs/github-repository-settings.md`'s settings (tag ruleset, `deploy` environment, fork-PR approval, workflow token permissions, SHA-pinning enforcement, secret scanning/push protection/Dependabot, immutable releases) and confirmed zero registered runners.
- `release.yml`'s contracts (workflow path, job names `build`/`publish`, environment name `deploy`, release tag `v{version}`, asset names `ledger-{version}.zip`/`.zip.sha256`/`.zip.sigstore.json`, attestation source ref `refs/tags/v{version}`) match exactly what plan 01-04's installer already expects — no changes needed on that side.
- No blockers for this plan's own scope.

## Self-Check: PASSED

- `build/validate-release-tag.sh`, `build/tests/validate-release-tag-test.sh`, `.github/workflows/release.yml`, `docs/releasing.md`, `docs/github-repository-settings.md` — all confirmed present on disk
- Commit `e508755` — present in `git log`
- Commit `68c8c54` — present in `git log`
- `bash build/tests/validate-release-tag-test.sh` — 19/19 cases passed, re-verified immediately before writing this summary
- `build/lint.sh` (all five checks: repo-rules, workflows, shell, secrets, script-tests) — re-verified exit 0 immediately before writing this summary

---
*Phase: 01-secure-platform-release-pipeline*
*Completed: 2026-09-27*
