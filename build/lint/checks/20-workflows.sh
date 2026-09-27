#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$REPO_ROOT"

FIXTURES_DIR="$REPO_ROOT/build/lint/fixtures"
WORKFLOWS_DIR="$REPO_ROOT/.github/workflows"
ZIZMOR_CONFIG="$REPO_ROOT/.github/zizmor.yml"

workflow_files_in() {
  local dir="$1"
  shopt -s nullglob
  local files=("$dir"/*.yml "$dir"/*.yaml)
  shopt -u nullglob
  printf '%s\n' "${files[@]}"
}

workflows_exist() {
  local dir="$1"
  local files
  files="$(workflow_files_in "$dir")"
  [ -n "$files" ]
}

zizmor_extra_args() {
  if [ -n "${GH_TOKEN:-}" ]; then
    printf '%s\n' "--config" ".github/zizmor.yml"
  else
    printf '%s\n' "--offline" "--config" ".github/zizmor.yml"
  fi
}

run_zizmor_on() {
  local mount_dir="$1" relative_file="$2"
  local -a extra=()
  while IFS= read -r arg; do
    extra+=("$arg")
  done < <(zizmor_extra_args)
  # shellcheck disable=SC2086
  if $LINT_COMPOSE -v "$mount_dir:/repo:ro" zizmor "${extra[@]}" "$relative_file" >/dev/null 2>&1; then
    return 0
  fi
  return 1
}

run_actionlint_on() {
  local mount_dir="$1" relative_file="$2"
  # shellcheck disable=SC2086
  if $LINT_COMPOSE -v "$mount_dir:/repo:ro" actionlint "$relative_file" >/dev/null 2>&1; then
    return 0
  fi
  return 1
}

check_fixture_rejected() {
  local fixture="$1" tool="$2"
  local tmp
  tmp="$(mktemp -d)"
  mkdir -p "$tmp/.github/workflows"
  cp "$ZIZMOR_CONFIG" "$tmp/.github/zizmor.yml"
  cp "$FIXTURES_DIR/$fixture" "$tmp/.github/workflows/$fixture"
  # actionlint's image runs as a non-root "guest" user; mktemp -d defaults to
  # 0700, which that user cannot even traverse once bind-mounted in.
  chmod -R a+rX "$tmp"

  local rejected=1
  case "$tool" in
    zizmor)
      if ! run_zizmor_on "$tmp" ".github/workflows/$fixture"; then
        rejected=0
      fi
      ;;
    either)
      if ! run_zizmor_on "$tmp" ".github/workflows/$fixture"; then
        rejected=0
      elif ! run_actionlint_on "$tmp" ".github/workflows/$fixture"; then
        rejected=0
      fi
      ;;
  esac

  rm -rf "$tmp"
  return "$rejected"
}

check_fixture_accepted() {
  local fixture="$1"
  local tmp
  tmp="$(mktemp -d)"
  mkdir -p "$tmp/.github/workflows"
  cp "$ZIZMOR_CONFIG" "$tmp/.github/zizmor.yml"
  cp "$FIXTURES_DIR/$fixture" "$tmp/.github/workflows/$fixture"
  chmod -R a+rX "$tmp"

  local accepted=0
  if ! run_zizmor_on "$tmp" ".github/workflows/$fixture"; then
    accepted=1
  elif ! run_actionlint_on "$tmp" ".github/workflows/$fixture"; then
    accepted=1
  fi

  rm -rf "$tmp"
  return "$accepted"
}

self_test() {
  local failures=0

  if ! check_fixture_rejected "short-sha-pin.yml" "zizmor"; then
    echo "self-test failed: short-sha-pin.yml fixture was not rejected by zizmor" >&2
    failures=1
  fi

  if ! check_fixture_rejected "tag-pin.yml" "zizmor"; then
    echo "self-test failed: tag-pin.yml fixture was not rejected by zizmor" >&2
    failures=1
  fi

  if ! check_fixture_rejected "run-interpolation.yml" "either"; then
    echo "self-test failed: run-interpolation.yml fixture was not rejected by zizmor or actionlint" >&2
    failures=1
  fi

  if ! check_fixture_accepted "run-only.yml"; then
    echo "self-test failed: run-only.yml fixture was rejected by zizmor or actionlint" >&2
    failures=1
  fi

  local empty_tmp
  empty_tmp="$(mktemp -d)"
  if workflows_exist "$empty_tmp/.github/workflows"; then
    echo "self-test failed: an empty directory was reported as containing workflows" >&2
    failures=1
  fi
  rm -rf "$empty_tmp"

  return "$failures"
}

if ! self_test; then
  echo "FAIL: 20-workflows self-test did not behave as expected" >&2
  exit 1
fi

if ! workflows_exist "$WORKFLOWS_DIR"; then
  echo "no workflows found under .github/workflows" >&2
  exit 1
fi

mapfile -t real_workflow_files < <(workflow_files_in "$WORKFLOWS_DIR" | while IFS= read -r f; do
  [ -n "$f" ] && printf '.github/workflows/%s\n' "$(basename "$f")"
done)

status=0

echo "Running actionlint on ${#real_workflow_files[@]} workflow(s)"
# shellcheck disable=SC2086
if ! $LINT_COMPOSE actionlint "${real_workflow_files[@]}"; then
  status=1
fi

echo "Running zizmor on ${#real_workflow_files[@]} workflow(s)"
mapfile -t extra_args < <(zizmor_extra_args)
# shellcheck disable=SC2086
if ! $LINT_COMPOSE zizmor "${extra_args[@]}" "${real_workflow_files[@]}"; then
  status=1
fi

exit "$status"
