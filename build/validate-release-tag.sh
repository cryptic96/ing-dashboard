#!/usr/bin/env bash
set -euo pipefail

# Reads TAG (required), GITHUB_SHA (optional) and MAIN_REF (default
# origin/main) from the environment only. On success, prints
# "version=X.Y.Z" (no leading v) to stdout and exits 0. On any failure,
# stdout is empty, an "::error::" line is written to stderr, and the
# process exits non-zero. The raw TAG value is never used in an error
# message until it has already passed the strict semver check below.

TAG="${TAG:-}"
MAIN_REF="${MAIN_REF:-origin/main}"

if [ -z "$TAG" ]; then
  echo "::error::TAG environment variable is required and must not be empty" >&2
  exit 1
fi

if ! [[ "$TAG" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
  echo "::error::Tag is not a strict semver tag (expected vMAJOR.MINOR.PATCH, no leading zeros, no pre-release or build metadata)" >&2
  exit 1
fi

VERSION="${TAG#v}"

TAG_COMMIT="$(git rev-parse --verify --quiet "refs/tags/${TAG}^{commit}")" || {
  echo "::error::Tag '$TAG' does not resolve to a commit" >&2
  exit 1
}

if [ -n "${GITHUB_SHA:-}" ] && [ "$TAG_COMMIT" != "$GITHUB_SHA" ]; then
  echo "::error::Tag '$TAG' commit does not match GITHUB_SHA" >&2
  exit 1
fi

if ! git merge-base --is-ancestor "$TAG_COMMIT" "$MAIN_REF" 2>/dev/null; then
  echo "::error::Tag '$TAG' commit is not reachable from $MAIN_REF" >&2
  exit 1
fi

printf 'version=%s\n' "$VERSION"
