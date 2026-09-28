# Cutting a release

This describes how to turn a merged change into a release the server can
install, what the automation refuses and why, and how to check a release's
authenticity yourself. It stops at the point a release is published; what
happens after that (the server pulling it, verifying it, installing it) is
covered in `docs/deploy.md`.

## What a release is

A release is a tag of the form `v<major>.<minor>.<patch>` (for example
`v1.4.0`) with three files attached:

- **`ledger-<version>.zip`** — the published application plus a
  self-contained database migration bundle and a small manifest describing
  which migrations it contains.
- **`ledger-<version>.zip.sha256`** — a checksum of that archive, so a
  transfer error is caught early.
- **`ledger-<version>.zip.sigstore.json`** — a signed attestation bundle
  proving which build produced the archive, from which commit and which
  workflow, so it can be checked without ever contacting the service that
  issued it.

## Cutting one

1. Merge the pull request containing the change onto the trunk branch, the
   same way as any other change.
2. On the trunk branch's latest commit, create an annotated tag matching
   `v<major>.<minor>.<patch>` exactly — no leading zeros, no pre-release or
   build-metadata suffix (`v1.4.0-rc.1` and `v1.4.0+build.2` are both
   refused, see below).
3. Push the tag. This starts a build on a hosted runner: it packages the
   application and its migration bundle, runs the test suite against a
   throwaway database, and attests the resulting archive's provenance. The
   result becomes a **draft** release — not publicly downloadable yet.
4. Approve the deploy environment on the resulting Actions run. This is the
   only manual step in the whole pipeline; a maintainer with access to the
   repository does this once per release.
5. Approving publishes the release. From here, the server's own poll timer
   picks it up, verifies it and installs it on its own schedule — nothing
   further happens inside the build system. Outcomes (success, failure, a
   rollback) show up by email and as metrics, exactly as described in
   `docs/deploy.md`.

## What the pipeline refuses, and why

- **A tag that isn't reachable from the trunk branch.** Every release must
  trace back to a change that actually went through review and merge —
  never a tag created on some other branch or a detached commit.
- **A tag whose commit doesn't match the commit that triggered the build.**
  A tag is only trusted for the exact commit it points at when the build
  started; nothing is re-tagged or re-pointed after the fact.
- **A tag that isn't strict semver.** `v1.2` and `v1.2.3.4` are missing or
  extra components; `v01.2.3` has a leading zero; `v1.2.3-rc.1` and
  `v1.2.3+build.5` carry a pre-release or build-metadata suffix that this
  project doesn't use. All of these are refused before anything is built.
- **Publishing without approval.** The build produces only a draft;
  nothing becomes downloadable until a maintainer approves the deploy
  environment.
- **A tampered or mismatched archive at publish time.** Immediately before
  a release is made public, the pipeline re-checks the draft archive
  against its own attestation bundle — the same offline check described
  below — and refuses to publish if it doesn't match.

## Verifying a release yourself

Anyone can check that a published archive really was built by this
project's own pipeline, from the exact commit its tag names, without
holding any credential:

```bash
gh attestation verify ledger-<version>.zip \
  --bundle ledger-<version>.zip.sigstore.json \
  --repo <owner>/<repository> \
  --signer-workflow <owner>/<repository>/.github/workflows/release.yml \
  --source-ref refs/tags/v<version> \
  --deny-self-hosted-runners
```

This performs the check entirely from the two downloaded files — no network
call to the attestation service is needed once both files are on disk. It
fails if the archive was modified after being built, if it was built by a
different workflow, or if it claims to come from a different tag than the
one you downloaded it for.

## Rolling back

Cutting a release only ever moves forward. If a published release turns
out to be broken, see `docs/deploy.md` for how the server handles a failed
health check automatically, and how to trigger a manual rollback.
