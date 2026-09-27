#!/usr/bin/env bash
# Proves that a genuinely attested public artifact verifies successfully, and
# that a tampered artifact, a mismatched repository, a mismatched signer
# workflow or a mismatched source ref is refused before anything is unpacked.
# Downloads one real, public, non-secret release asset over HTTPS to exercise
# the check against real bytes; the fixture's own metadata (URL, checksum,
# repository, signer workflow, source ref) is recorded alongside this test.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
FIXTURE_DIR="${SCRIPT_DIR}/fixtures"

# shellcheck source=deploy/lib/common.sh
source "${REPO_ROOT}/deploy/lib/common.sh"
# shellcheck source=deploy/lib/deploy.sh
source "${REPO_ROOT}/deploy/lib/deploy.sh"
# shellcheck source=deploy/tests/fixtures/public-attested-artifact.env
source "${FIXTURE_DIR}/public-attested-artifact.env"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "${WORK_DIR}"' EXIT

FAILURES=0

check() {
  local description="$1"
  local expected="$2"
  local actual="$3"
  if [ "$actual" = "$expected" ]; then
    printf 'PASS: %s\n' "$description"
  else
    printf 'FAIL: %s (expected %s, got %s)\n' "$description" "$expected" "$actual"
    FAILURES=$((FAILURES + 1))
  fi
}

ARTIFACT_PATH="${WORK_DIR}/artifact.bin"

ledger_log "downloading fixture artifact"
curl --fail --silent --show-error --location --max-time 60 -o "$ARTIFACT_PATH" "$ARTIFACT_URL"

ACTUAL_SHA256="$(sha256sum "$ARTIFACT_PATH" | awk '{print $1}')"
if [ "$ACTUAL_SHA256" != "$ARTIFACT_SHA256" ]; then
  ledger_die "downloaded fixture artifact does not match the recorded checksum"
fi

BUNDLE_PATH="${FIXTURE_DIR}/public-attested-artifact.sigstore.jsonl"

TAMPERED_PATH="${WORK_DIR}/tampered.bin"
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

run_verify() {
  local artifact="$1" repo="$2" signer_workflow="$3" source_ref="$4"
  if GH_TOKEN='' GITHUB_TOKEN='' GH_ENTERPRISE_TOKEN='' ledger_verify_attestation \
      "$artifact" "$BUNDLE_PATH" "$repo" "$signer_workflow" "$source_ref" >/dev/null 2>&1; then
    printf '0'
  else
    printf '1'
  fi
}

check "genuine artifact verifies" "0" \
  "$(run_verify "$ARTIFACT_PATH" "$REPO" "$SIGNER_WORKFLOW" "$SOURCE_REF")"

check "tampered artifact is refused" "1" \
  "$(run_verify "$TAMPERED_PATH" "$REPO" "$SIGNER_WORKFLOW" "$SOURCE_REF")"

check "mismatched repository is refused" "1" \
  "$(run_verify "$ARTIFACT_PATH" "cli/other" "$SIGNER_WORKFLOW" "$SOURCE_REF")"

check "mismatched signer workflow is refused" "1" \
  "$(run_verify "$ARTIFACT_PATH" "$REPO" ".github/workflows/release.yml" "$SOURCE_REF")"

check "mismatched source ref is refused" "1" \
  "$(run_verify "$ARTIFACT_PATH" "$REPO" "$SIGNER_WORKFLOW" "refs/tags/v0.0.0")"

DIGEST="$(ledger_verify_attestation "$ARTIFACT_PATH" "$BUNDLE_PATH" "$REPO" "$SIGNER_WORKFLOW" "$SOURCE_REF")"
check "reported source digest matches the fixture" "$SOURCE_DIGEST" "$DIGEST"

check "commit on branch succeeds for the attested digest" "0" \
  "$( ledger_commit_on_branch "$REPO" "$SOURCE_DIGEST" "trunk" >/dev/null 2>&1; echo $? )"

check "commit on branch fails for an unrelated commit" "1" \
  "$( ledger_commit_on_branch "$REPO" "0000000000000000000000000000000000000000" "trunk" >/dev/null 2>&1; echo $? )"

check "no ambient token influenced verification" "" \
  "${GH_TOKEN:-}${GITHUB_TOKEN:-}${GH_ENTERPRISE_TOKEN:-}"

# Installer level: a tampered artifact must be refused before anything is
# unpacked, and must leave no releases directory and no staging directory
# behind, under a relocated test root.
INSTALL_ROOT="${WORK_DIR}/install-root"
mkdir -p "$INSTALL_ROOT/etc/ledger"
FROM_DIR="${WORK_DIR}/from-dir"
mkdir -p "$FROM_DIR"
cp "$TAMPERED_PATH" "${FROM_DIR}/ledger-2.101.0.zip"
cp "$BUNDLE_PATH" "${FROM_DIR}/ledger-2.101.0.zip.sigstore.json"

CONF_PATH="${INSTALL_ROOT}/etc/ledger/deploy.conf"
cat > "$CONF_PATH" <<EOF_CONF
LEDGER_GITHUB_REPO=${REPO}
LEDGER_SIGNER_WORKFLOW=${SIGNER_WORKFLOW}
EOF_CONF
chmod 600 "$CONF_PATH"

INSTALL_EXIT=0
LEDGER_DEPLOY_ROOT="$INSTALL_ROOT" LEDGER_DEPLOY_CONF="$CONF_PATH" \
  GH_TOKEN='' GITHUB_TOKEN='' GH_ENTERPRISE_TOKEN='' \
  "${REPO_ROOT}/deploy/bin/ledger-deploy" install v2.101.0 --from-dir "$FROM_DIR" \
  >"${WORK_DIR}/install.log" 2>&1 || INSTALL_EXIT=$?

check "install exits non-zero on a tampered artifact" "1" "$( [ "$INSTALL_EXIT" -ne 0 ] && echo 1 || echo 0 )"
check "no releases directory was created" "0" "$( [ -e "${INSTALL_ROOT}/opt/ledger/releases/2.101.0" ] && echo 1 || echo 0 )"
check "no staging directory was created" "0" \
  "$( find "${INSTALL_ROOT}/opt/ledger/releases" -maxdepth 1 -name '.staging-*' 2>/dev/null | grep -qc . && echo 1 || echo 0 )"

# Poll behaviour: a stub releases/latest document naming the already-active
# version does nothing; one naming a newer version calls install with that
# tag (proven by the log line emitted before installation is attempted).
POLL_ROOT="${WORK_DIR}/poll-root"
mkdir -p "${POLL_ROOT}/etc/ledger" "${POLL_ROOT}/opt/ledger/releases/1.0.0"
ln -s "${POLL_ROOT}/opt/ledger/releases/1.0.0" "${POLL_ROOT}/opt/ledger/current"

POLL_CONF="${POLL_ROOT}/etc/ledger/deploy.conf"
cat > "$POLL_CONF" <<EOF_CONF
LEDGER_GITHUB_REPO=${REPO}
LEDGER_SIGNER_WORKFLOW=${SIGNER_WORKFLOW}
LEDGER_TEXTFILE_DIR=${POLL_ROOT}/var/lib/prometheus/node-exporter
EOF_CONF
chmod 600 "$POLL_CONF"

SAME_VERSION_STUB="${WORK_DIR}/releases-latest-same.json"
printf '{"tag_name": "v1.0.0"}' > "$SAME_VERSION_STUB"

LEDGER_DEPLOY_ROOT="$POLL_ROOT" LEDGER_DEPLOY_CONF="$POLL_CONF" \
  LEDGER_POLL_LATEST_URL="file://${SAME_VERSION_STUB}" \
  "${REPO_ROOT}/deploy/bin/ledger-deploy" poll >"${WORK_DIR}/poll-same.log" 2>&1
check "poll does nothing when the latest tag matches the active version" "0" \
  "$( grep -qc 'installing' "${WORK_DIR}/poll-same.log" && echo 1 || echo 0 )"

NEWER_VERSION_STUB="${WORK_DIR}/releases-latest-newer.json"
printf '{"tag_name": "v9.9.9"}' > "$NEWER_VERSION_STUB"

LEDGER_DEPLOY_ROOT="$POLL_ROOT" LEDGER_DEPLOY_CONF="$POLL_CONF" \
  LEDGER_POLL_LATEST_URL="file://${NEWER_VERSION_STUB}" \
  "${REPO_ROOT}/deploy/bin/ledger-deploy" poll >"${WORK_DIR}/poll-newer.log" 2>&1 || true
check "poll calls install when a newer tag is found" "1" \
  "$( grep -qc 'newer release v9.9.9 found, installing' "${WORK_DIR}/poll-newer.log" && echo 1 || echo 0 )"

if [ "$FAILURES" -ne 0 ]; then
  printf '%d check(s) failed\n' "$FAILURES" >&2
  exit 1
fi

printf 'All checks passed\n'
