#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$REPO_ROOT"

run_shellcheck() {
  local mount_dir="$1"
  shift
  # shellcheck disable=SC2086
  $LINT_COMPOSE -v "$mount_dir:/repo:ro" shellcheck -x "$@"
}

self_test() {
  local tmp
  tmp="$(mktemp -d)"
  local bad="$tmp/bad.sh"
  local good="$tmp/good.sh"
  local failed=0

  # shellcheck disable=SC2016
  printf '#!/usr/bin/env bash\necho $1\n' >"$bad"
  # shellcheck disable=SC2016
  printf '#!/usr/bin/env bash\nset -euo pipefail\necho "$1"\n' >"$good"

  if run_shellcheck "$tmp" "bad.sh" >/dev/null 2>&1; then
    echo "self-test failed: shellcheck did not flag an unquoted variable" >&2
    failed=1
  fi

  if ! run_shellcheck "$tmp" "good.sh" >/dev/null 2>&1; then
    echo "self-test failed: shellcheck rejected a clean script" >&2
    failed=1
  fi

  rm -rf "$tmp"
  return "$failed"
}

if ! self_test; then
  echo "FAIL: 30-shell self-test did not behave as expected" >&2
  exit 1
fi

shopt -s nullglob
candidate_files=(
  build/*.sh
  build/lint.sh
  build/lint/checks/*.sh
  build/tests/*.sh
  deploy/bin/*
  deploy/lib/*.sh
  deploy/provision.sh
  deploy/provision.d/*.sh
  deploy/tests/*.sh
)
shopt -u nullglob

existing_files=()
for f in "${candidate_files[@]}"; do
  [ -f "$f" ] && existing_files+=("$f")
done

if [ "${#existing_files[@]}" -eq 0 ]; then
  echo "no shell scripts found yet"
  exit 0
fi

mapfile -t unique_files < <(printf '%s\n' "${existing_files[@]}" | sort -u)

echo "Running shellcheck over ${#unique_files[@]} file(s)"
if ! run_shellcheck "$REPO_ROOT" "${unique_files[@]}"; then
  exit 1
fi
