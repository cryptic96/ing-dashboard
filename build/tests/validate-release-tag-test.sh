#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="$REPO_ROOT/build/validate-release-tag.sh"

WORK_DIR="$(mktemp -d)"
cleanup() {
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT

REPO_DIR="$WORK_DIR/repo"
mkdir -p "$REPO_DIR"
cd "$REPO_DIR"

git init --quiet --initial-branch=main .
git config user.email "test@example.com"
git config user.name "Test User"

git commit --quiet --allow-empty -m "initial commit"
OLDER_MAIN_COMMIT="$(git rev-parse HEAD)"

git commit --quiet --allow-empty -m "second commit"
LATEST_MAIN_COMMIT="$(git rev-parse HEAD)"

git checkout --quiet -b feature
git commit --quiet --allow-empty -m "unmerged feature work"
FEATURE_COMMIT="$(git rev-parse HEAD)"

git checkout --quiet main

# validate-release-tag.sh checks reachability against origin/main by default;
# a plain local repo has no real remote, so point it at this same repo.
git remote add origin "$REPO_DIR"
git update-ref refs/remotes/origin/main refs/heads/main

# Tags used by the "accepted format" cases below.
git tag v0.1.0 "$LATEST_MAIN_COMMIT"
git tag v1.2.3 "$LATEST_MAIN_COMMIT"
git tag v10.20.30 "$LATEST_MAIN_COMMIT"
git tag v0.2.0 "$OLDER_MAIN_COMMIT"
git tag v0.3.0 "$FEATURE_COMMIT"

run_gate() {
  (
    unset TAG GITHUB_SHA MAIN_REF
    local kv name value
    for kv in "$@"; do
      name="${kv%%=*}"
      value="${kv#*=}"
      printf -v "$name" '%s' "$value"
      # shellcheck disable=SC2163 # intentional: $name holds the name of the variable to export
      export "$name"
    done
    "$SCRIPT"
  )
}

STDOUT_FILE="$WORK_DIR/stdout"
STDERR_FILE="$WORK_DIR/stderr"

# Verifies exit status, stdout and (for failures) an ::error:: stderr line.
# Exits non-zero immediately on the first mismatch.
check() {
  local desc="$1" expect_status="$2" expect_stdout="$3"
  shift 3

  echo "=== $desc ==="

  local actual_status=0
  if run_gate "$@" >"$STDOUT_FILE" 2>"$STDERR_FILE"; then
    actual_status=0
  else
    actual_status=$?
  fi

  local stdout stderr
  stdout="$(cat "$STDOUT_FILE")"
  stderr="$(cat "$STDERR_FILE")"

  if [ "$expect_status" -eq 0 ] && [ "$actual_status" -ne 0 ]; then
    echo "FAIL ($desc): expected success, got exit $actual_status. stderr=[$stderr]" >&2
    exit 1
  fi

  if [ "$expect_status" -ne 0 ] && [ "$actual_status" -eq 0 ]; then
    echo "FAIL ($desc): expected failure, got success. stdout=[$stdout]" >&2
    exit 1
  fi

  if [ "$expect_status" -eq 0 ] && [ "$stdout" != "$expect_stdout" ]; then
    echo "FAIL ($desc): expected stdout '$expect_stdout', got '$stdout'" >&2
    exit 1
  fi

  if [ "$expect_status" -ne 0 ]; then
    if [ -n "$stdout" ]; then
      echo "FAIL ($desc): expected empty stdout on failure, got '$stdout'" >&2
      exit 1
    fi
    if ! grep -q '::error::' "$STDERR_FILE"; then
      echo "FAIL ($desc): expected an ::error:: line on stderr, got '$stderr'" >&2
      exit 1
    fi
  fi

  echo "PASS: $desc"
}

# --- Accepted strict-semver tag formats, each on a commit reachable from main ---
check "accepted v0.1.0 on latest main commit" 0 "version=0.1.0" "TAG=v0.1.0"
check "accepted v1.2.3 on latest main commit" 0 "version=1.2.3" "TAG=v1.2.3"
check "accepted v10.20.30 on latest main commit" 0 "version=10.20.30" "TAG=v10.20.30"
check "accepted v0.2.0 on an older main commit" 0 "version=0.2.0" "TAG=v0.2.0"

# --- Rejected tag formats (fail the strict regex before any git lookup) ---
check "rejected: empty TAG" 1 "" "TAG="
check "rejected: missing v prefix" 1 "" "TAG=1.2.3"
check "rejected: missing patch component" 1 "" "TAG=v1.2"
check "rejected: extra fourth component" 1 "" "TAG=v1.2.3.4"
check "rejected: leading zero in major" 1 "" "TAG=v01.2.3"
check "rejected: leading zero in minor" 1 "" "TAG=v1.02.3"
check "rejected: pre-release suffix" 1 "" "TAG=v1.2.3-rc.1"
check "rejected: build metadata suffix" 1 "" "TAG=v1.2.3+build.5"
check "rejected: uppercase V prefix" 1 "" "TAG=V1.2.3"

TRAILING_NEWLINE_TAG=$'v1.2.3\n'
check "rejected: trailing newline" 1 "" "TAG=${TRAILING_NEWLINE_TAG}"

check "rejected: semicolon and shell command" 1 "" "TAG=v1.2.3; rm -rf /"
check "rejected: embedded space" 1 "" "TAG=v1.2.3 v1.2.3"

# --- Reachability-from-main and commit-identity checks ---
check "rejected: tag on unmerged feature branch" 1 "" "TAG=v0.3.0"
check "rejected: GITHUB_SHA differs from tag commit" 1 "" "TAG=v1.2.3" "GITHUB_SHA=$OLDER_MAIN_COMMIT"
check "accepted: GITHUB_SHA matches tag commit" 0 "version=1.2.3" "TAG=v1.2.3" "GITHUB_SHA=$LATEST_MAIN_COMMIT"

echo "All validate-release-tag.sh cases passed."
