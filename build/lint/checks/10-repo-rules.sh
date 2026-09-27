#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$REPO_ROOT"

# Each pattern below is written so its own source line cannot match the thing
# it detects (grouped alternations keep a shared suffix outside every branch;
# the directory patterns bracket a single letter to break contiguity).
REQUIREMENT_KEY_PATTERN='\b(SEC|OPS|API|DASH|INGEST|CAT|PLAN|ADV|WEB|REF)-[0-9]{2}\b'
DECISION_ID_PATTERN='\bD-[0-9]{2}\b'
PHASE_WORD_PATTERN='\b[Pp]hase[-_ ]?[0-9]+\b'
PLANNING_FILE_PATTERN='\b(PROJECT|REQUIREMENTS|ROADMAP|STATE|RESEARCH|CONTEXT|PLAN|SPEC)\.md\b'
PLANNING_DIR_PATTERN='\.plannin[g]/'
CLAUDE_DIR_PATTERN='\.claud[e]/'
EXCLUDE_PATH_PATTERN="^${PLANNING_DIR_PATTERN}|^${CLAUDE_DIR_PATTERN}"

CS_LINE_COMMENT_PATTERN='^[[:space:]]*//([^/]|$)|[;{)][[:space:]]*//([^/]|$)'

RUNS_ON_ALLOWED='ubuntu-24.04'

assert_clean_planning_references() {
  local -a files=("$@")
  [ "${#files[@]}" -eq 0 ] && return 0
  local pattern
  local violations=0
  for pattern in "$REQUIREMENT_KEY_PATTERN" "$DECISION_ID_PATTERN" "$PHASE_WORD_PATTERN" \
    "$PLANNING_FILE_PATTERN" "$PLANNING_DIR_PATTERN"; do
    if grep -nE "$pattern" "${files[@]}" 2>/dev/null; then
      violations=1
    fi
  done
  [ "$violations" -eq 0 ]
}

assert_clean_cs_comments() {
  local -a files=("$@")
  [ "${#files[@]}" -eq 0 ] && return 0
  if grep -nE "$CS_LINE_COMMENT_PATTERN" "${files[@]}" 2>/dev/null; then
    return 1
  fi
  return 0
}

assert_clean_runs_on() {
  local dir="$1"
  shopt -s nullglob
  local files=("$dir"/*.yml "$dir"/*.yaml)
  shopt -u nullglob
  [ "${#files[@]}" -eq 0 ] && return 0
  local bad=0
  local f line value
  for f in "${files[@]}"; do
    while IFS= read -r line; do
      value="$(printf '%s' "$line" | sed -E 's/^[[:space:]]*runs-on:[[:space:]]*//; s/[[:space:]]*$//; s/^"//; s/"$//')"
      if [ "$value" != "$RUNS_ON_ALLOWED" ]; then
        echo "$f: disallowed runs-on value: $value" >&2
        bad=1
      fi
    done < <(grep -hE '^[[:space:]]*runs-on:' "$f" || true)
  done
  return "$bad"
}

self_test() {
  local tmp
  tmp="$(mktemp -d)"
  local bad_file="$tmp/bad.txt"
  local good_file="$tmp/good.txt"
  local failed=0

  printf 'Just plain prose with no planning references.\n' >"$good_file"
  if ! assert_clean_planning_references "$good_file"; then
    echo "self-test failed: a clean file was flagged as containing a planning reference" >&2
    failed=1
  fi

  printf '%s%s\n' "SEC" "-01 example requirement key" >"$bad_file"
  if assert_clean_planning_references "$bad_file"; then
    echo "self-test failed: a synthetic requirement key was not detected" >&2
    failed=1
  fi

  printf '%s%s\n' "D" "-08 example decision id" >"$bad_file"
  if assert_clean_planning_references "$bad_file"; then
    echo "self-test failed: a synthetic decision id was not detected" >&2
    failed=1
  fi

  printf '%s %s\n' "Phase" "3 rollout" >"$bad_file"
  if assert_clean_planning_references "$bad_file"; then
    echo "self-test failed: a synthetic phase reference was not detected" >&2
    failed=1
  fi

  printf 'See %s%s for details\n' "ROADMAP" ".md" >"$bad_file"
  if assert_clean_planning_references "$bad_file"; then
    echo "self-test failed: a synthetic planning filename was not detected" >&2
    failed=1
  fi

  printf 'Look under %s%s%s\n' "." "planning" "/" >"$bad_file"
  if assert_clean_planning_references "$bad_file"; then
    echo "self-test failed: a synthetic planning directory reference was not detected" >&2
    failed=1
  fi

  local bad_cs="$tmp/Bad.cs"
  local good_cs="$tmp/Good.cs"
  printf 'namespace Example;\n%s explanation\npublic class Foo { }\n' "// inline" >"$bad_cs"
  printf 'namespace Example;\n/// <summary>Doc comment.</summary>\npublic class Foo { }\n' >"$good_cs"

  if assert_clean_cs_comments "$bad_cs"; then
    echo "self-test failed: a // line comment was not detected" >&2
    failed=1
  fi

  if ! assert_clean_cs_comments "$good_cs"; then
    echo "self-test failed: an /// doc comment was incorrectly flagged" >&2
    failed=1
  fi

  mkdir -p "$tmp/workflows"
  printf 'jobs:\n  build:\n    runs-on: %s\n' "$RUNS_ON_ALLOWED" >"$tmp/workflows/good.yml"
  printf 'jobs:\n  build:\n    runs-on: self-hosted\n' >"$tmp/workflows/bad.yml"

  if assert_clean_runs_on "$tmp/workflows"; then
    echo "self-test failed: a disallowed runs-on value was not detected" >&2
    failed=1
  fi

  rm -f "$tmp/workflows/bad.yml"
  if ! assert_clean_runs_on "$tmp/workflows"; then
    echo "self-test failed: an allowed runs-on value was incorrectly flagged" >&2
    failed=1
  fi

  rm -rf "$tmp"
  return "$failed"
}

if ! self_test; then
  echo "FAIL: 10-repo-rules self-test did not behave as expected" >&2
  exit 1
fi

mapfile -t tracked_files < <(git ls-files | grep -vE "$EXCLUDE_PATH_PATTERN" || true)
mapfile -t cs_files < <(git ls-files '*.cs' | grep -vE "$EXCLUDE_PATH_PATTERN" || true)

overall_ok=1

if ! assert_clean_planning_references "${tracked_files[@]}"; then
  overall_ok=0
fi

if ! assert_clean_cs_comments "${cs_files[@]}"; then
  overall_ok=0
fi

if [ -d "$REPO_ROOT/.github/workflows" ]; then
  if ! assert_clean_runs_on "$REPO_ROOT/.github/workflows"; then
    overall_ok=0
  fi
fi

[ "$overall_ok" -eq 1 ]
