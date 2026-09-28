#!/usr/bin/env bash
# Workstation-side proof that a published release verifies exactly the way
# the LXC installer verifies it. Takes one strict vMAJOR.MINOR.PATCH tag,
# resolves the repository with the authenticated gh CLI (read-only), then
# downloads the release zip and its Sigstore bundle straight from the public
# release download URL with curl and no credential of any kind: no
# Authorization header, no gh download, no token. It then sources the
# installer's own verification functions and calls them exactly as the
# installer does, and finally proves that a copy of the same zip with one
# byte flipped is refused. Prints PASS or FAIL for every step and exits 1 on
# the first failure.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

STRICT_SEMVER_TAG_REGEX='^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
SIGNER_WORKFLOW=".github/workflows/release.yml"
MAIN_BRANCH="main"

usage() {
  echo "Usage: verify-published-release.sh vMAJOR.MINOR.PATCH" >&2
}

pass() {
  printf 'PASS: %s\n' "$1"
}

fail() {
  printf 'FAIL: %s\n' "$1"
  exit 1
}

TAG="${1:-}"
if [ -z "$TAG" ]; then
  usage
  exit 1
fi

if ! [[ "$TAG" =~ $STRICT_SEMVER_TAG_REGEX ]]; then
  fail "'$TAG' is a strict vMAJOR.MINOR.PATCH tag"
fi
pass "'$TAG' is a strict vMAJOR.MINOR.PATCH tag"
VERSION="${TAG#v}"

# shellcheck source=deploy/lib/common.sh
source "${REPO_ROOT}/deploy/lib/common.sh"
# shellcheck source=deploy/lib/deploy.sh
source "${REPO_ROOT}/deploy/lib/deploy.sh"

WORK_DIR="$(mktemp -d)"
cleanup() {
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT

REPO=""
if ! REPO="$(env -u GH_TOKEN -u GITHUB_TOKEN -u GH_ENTERPRISE_TOKEN \
    gh repo view --json nameWithOwner --jq '.nameWithOwner' 2>/dev/null)" || [ -z "$REPO" ]; then
  fail "resolve the repository with gh repo view"
fi
pass "resolved repository ${REPO}"

ARTIFACT_NAME="ledger-${VERSION}.zip"
BUNDLE_NAME="${ARTIFACT_NAME}.sigstore.json"
ARTIFACT_PATH="${WORK_DIR}/${ARTIFACT_NAME}"
BUNDLE_PATH="${WORK_DIR}/${BUNDLE_NAME}"
DOWNLOAD_BASE="https://github.com/${REPO}/releases/download/${TAG}"

if ! curl --fail --silent --show-error --location --max-time 120 \
    -o "$ARTIFACT_PATH" "${DOWNLOAD_BASE}/${ARTIFACT_NAME}"; then
  fail "download ${ARTIFACT_NAME} from the public release page"
fi
pass "downloaded ${ARTIFACT_NAME} from the public release page"

if ! curl --fail --silent --show-error --location --max-time 120 \
    -o "$BUNDLE_PATH" "${DOWNLOAD_BASE}/${BUNDLE_NAME}"; then
  fail "download ${BUNDLE_NAME} from the public release page"
fi
pass "downloaded ${BUNDLE_NAME} from the public release page"

DIGEST=""
if ! DIGEST="$(ledger_verify_attestation "$ARTIFACT_PATH" "$BUNDLE_PATH" "$REPO" "$SIGNER_WORKFLOW" "refs/tags/${TAG}")"; then
  fail "verify the published attestation for ${ARTIFACT_NAME}"
fi
pass "verified the published attestation (source digest: ${DIGEST})"

if ! ledger_commit_on_branch "$REPO" "$DIGEST" "$MAIN_BRANCH"; then
  fail "confirm the attested commit ${DIGEST} is on ${MAIN_BRANCH}"
fi
pass "confirmed the attested commit ${DIGEST} is on ${MAIN_BRANCH}"

TAMPERED_PATH="${WORK_DIR}/tampered-${ARTIFACT_NAME}"
cp "$ARTIFACT_PATH" "$TAMPERED_PATH"
python3 - "$TAMPERED_PATH" <<'EOF_PY'
import sys
path = sys.argv[1]
with open(path, "r+b") as handle:
    handle.seek(100)
    byte = handle.read(1)
    handle.seek(100)
    handle.write(bytes([byte[0] ^ 0xFF]))
EOF_PY

if ledger_verify_attestation "$TAMPERED_PATH" "$BUNDLE_PATH" "$REPO" "$SIGNER_WORKFLOW" "refs/tags/${TAG}" >/dev/null 2>&1; then
  fail "refuse a one-byte-modified copy of ${ARTIFACT_NAME}"
fi
pass "refused a one-byte-modified copy of ${ARTIFACT_NAME}"

echo "all checks passed for ${TAG}"
