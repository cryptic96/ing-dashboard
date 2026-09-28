#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CHECKS_DIR="$REPO_ROOT/build/lint/checks"

cd "$REPO_ROOT"

# A linked git worktree's .git is a pointer file whose gitdir lives under the
# main checkout's .git/worktrees/<name>, at an absolute host path outside
# this repo root. Bind-mounting the repo alone leaves that path missing
# inside the container, so any git-aware tool (gitleaks, zizmor's own
# discovery) sees "not a git repository". When the common dir resolves
# outside the repo root, mount it back in at the identical absolute path so
# the container's view of the filesystem matches the host's.
GIT_COMMON_DIR="$(git rev-parse --git-common-dir 2>/dev/null || true)"
EXTRA_MOUNT=""
if [ -n "$GIT_COMMON_DIR" ]; then
  GIT_COMMON_DIR_ABS="$(cd "$GIT_COMMON_DIR" && pwd)"
  case "$GIT_COMMON_DIR_ABS" in
    "$REPO_ROOT" | "$REPO_ROOT"/*) ;;
    *) EXTRA_MOUNT="-v $GIT_COMMON_DIR_ABS:$GIT_COMMON_DIR_ABS:ro" ;;
  esac
fi

# shellcheck disable=SC2086
export LINT_COMPOSE="docker compose -f build/lint/compose.yaml run --rm --no-deps -T $EXTRA_MOUNT"

all_names=()
all_scripts=()

shopt -s nullglob
for script in "$CHECKS_DIR"/[0-9][0-9]-*.sh; do
  [ -x "$script" ] || continue
  base="$(basename "$script")"
  name="${base#[0-9][0-9]-}"
  name="${name%.sh}"
  all_names+=("$name")
  all_scripts+=("$script")
done
shopt -u nullglob

if [ "${#all_names[@]}" -eq 0 ]; then
  echo "No lint checks found under build/lint/checks" >&2
  exit 1
fi

selected_names=()
selected_scripts=()

if [ "$#" -eq 0 ]; then
  selected_names=("${all_names[@]}")
  selected_scripts=("${all_scripts[@]}")
else
  for requested in "$@"; do
    found=""
    for i in "${!all_names[@]}"; do
      if [ "${all_names[$i]}" = "$requested" ]; then
        selected_names+=("$requested")
        selected_scripts+=("${all_scripts[$i]}")
        found=1
        break
      fi
    done
    if [ -z "$found" ]; then
      echo "Unknown check: $requested" >&2
      echo "Available checks: ${all_names[*]}" >&2
      exit 1
    fi
  done
fi

results=()
overall_status=0

for i in "${!selected_names[@]}"; do
  name="${selected_names[$i]}"
  script="${selected_scripts[$i]}"
  echo "==> Running check: $name"
  if "$script"; then
    results+=("PASS $name")
  else
    results+=("FAIL $name")
    overall_status=1
  fi
done

echo ""
echo "Lint summary:"
for result in "${results[@]}"; do
  echo "  $result"
done

exit "$overall_status"
