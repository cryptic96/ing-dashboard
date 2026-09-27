#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$REPO_ROOT"

GITLEAKS_CONFIG="$REPO_ROOT/.gitleaks.toml"

random_upper_letters() {
  local count="$1" result="" i idx
  local alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ"
  for ((i = 0; i < count; i++)); do
    idx=$((RANDOM % 26))
    result+="${alphabet:idx:1}"
  done
  printf '%s' "$result"
}

random_digits() {
  local count="$1" result="" i
  for ((i = 0; i < count; i++)); do
    result+="$((RANDOM % 10))"
  done
  printf '%s' "$result"
}

fake_github_token() {
  printf 'ghp_%s' "$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 36)"
}

fake_dutch_iban() {
  printf 'NL%s%s%s' "$(random_digits 2)" "$(random_upper_letters 4)" "$(random_digits 10)"
}

fake_private_ipv4() {
  printf '192.168.%d.%d' "$((RANDOM % 256))" "$((1 + RANDOM % 254))"
}

fake_non_example_email() {
  local user domain
  user="$(random_upper_letters 8 | tr '[:upper:]' '[:lower:]')"
  domain="$(random_upper_letters 6 | tr '[:upper:]' '[:lower:]')"
  printf '%s@%s.nl' "$user" "$domain"
}

gitleaks_git_mode() {
  local dir="$1"
  # --log-opts=HEAD scopes the scan to the current branch's reachable
  # history. Without it, gitleaks walks every ref sharing the underlying
  # git object database — including, in a linked-worktree setup, sibling
  # worktrees' branches, which are out of scope for this check.
  # shellcheck disable=SC2086
  if $LINT_COMPOSE -v "$dir:/repo:ro" gitleaks detect --source /repo --config /repo/.gitleaks.toml --redact --no-banner --log-opts="HEAD" >/dev/null 2>&1; then
    return 0
  fi
  return 1
}

gitleaks_dir_mode() {
  local dir="$1"
  # shellcheck disable=SC2086
  if $LINT_COMPOSE -v "$dir:/repo:ro" gitleaks detect --no-git --source /repo --config /repo/.gitleaks.toml --redact --no-banner >/dev/null 2>&1; then
    return 0
  fi
  return 1
}

assert_not_shallow() {
  local dir="$1"
  local is_shallow
  is_shallow="$(git -C "$dir" rev-parse --is-shallow-repository)"
  [ "$is_shallow" = "false" ]
}

make_throwaway_repo() {
  local dir="$1"
  mkdir -p "$dir"
  git -C "$dir" init -q
  git -C "$dir" config user.email "lint-self-test@example.com"
  git -C "$dir" config user.name "lint self-test"
  cp "$GITLEAKS_CONFIG" "$dir/.gitleaks.toml"
}

self_test() {
  local failed=0

  local clean_repo
  clean_repo="$(mktemp -d)"
  make_throwaway_repo "$clean_repo"
  {
    printf 'loopback address: 127.0.0.1\n'
    printf 'documentation address: 192.0.2.10\n'
    printf 'contact: someone@example.com\n'
  } >"$clean_repo/notes.txt"
  git -C "$clean_repo" add notes.txt .gitleaks.toml
  git -C "$clean_repo" commit -q -m "notes"

  if ! gitleaks_git_mode "$clean_repo"; then
    echo "self-test failed: loopback/documentation/example.com content was rejected" >&2
    failed=1
  fi
  if ! gitleaks_dir_mode "$clean_repo"; then
    echo "self-test failed: loopback/documentation/example.com content was rejected in dir mode" >&2
    failed=1
  fi
  rm -rf "$clean_repo"

  local token_repo
  token_repo="$(mktemp -d)"
  make_throwaway_repo "$token_repo"
  fake_github_token >"$token_repo/secret.txt"
  git -C "$token_repo" add secret.txt .gitleaks.toml
  git -C "$token_repo" commit -q -m "add token"
  git -C "$token_repo" rm -q secret.txt
  git -C "$token_repo" commit -q -m "remove token"

  if gitleaks_git_mode "$token_repo"; then
    echo "self-test failed: a token added then deleted in history was not detected" >&2
    failed=1
  fi

  local shallow_dir
  shallow_dir="$(mktemp -d)"
  if git clone -q --depth 1 "file://$token_repo" "$shallow_dir" 2>/dev/null; then
    if assert_not_shallow "$shallow_dir"; then
      echo "self-test failed: a shallow clone was not detected as shallow" >&2
      failed=1
    fi
  else
    echo "self-test failed: could not create a shallow clone to test against" >&2
    failed=1
  fi
  rm -rf "$shallow_dir" "$token_repo"

  local iban_repo
  iban_repo="$(mktemp -d)"
  make_throwaway_repo "$iban_repo"
  printf 'account: %s\n' "$(fake_dutch_iban)" >"$iban_repo/notes.txt"
  git -C "$iban_repo" add notes.txt .gitleaks.toml
  git -C "$iban_repo" commit -q -m "notes"
  if gitleaks_git_mode "$iban_repo"; then
    echo "self-test failed: a generated Dutch IBAN was not detected" >&2
    failed=1
  fi
  rm -rf "$iban_repo"

  local ip_repo
  ip_repo="$(mktemp -d)"
  make_throwaway_repo "$ip_repo"
  printf 'server: %s\n' "$(fake_private_ipv4)" >"$ip_repo/notes.txt"
  git -C "$ip_repo" add notes.txt .gitleaks.toml
  git -C "$ip_repo" commit -q -m "notes"
  if gitleaks_git_mode "$ip_repo"; then
    echo "self-test failed: a generated 192.168.x.y address was not detected" >&2
    failed=1
  fi
  rm -rf "$ip_repo"

  local email_repo
  email_repo="$(mktemp -d)"
  make_throwaway_repo "$email_repo"
  printf 'contact: %s\n' "$(fake_non_example_email)" >"$email_repo/notes.txt"
  git -C "$email_repo" add notes.txt .gitleaks.toml
  git -C "$email_repo" commit -q -m "notes"
  if gitleaks_git_mode "$email_repo"; then
    echo "self-test failed: a generated non-example-domain email was not detected" >&2
    failed=1
  fi
  rm -rf "$email_repo"

  return "$failed"
}

if ! self_test; then
  echo "FAIL: 40-secrets self-test did not behave as expected" >&2
  exit 1
fi

if ! assert_not_shallow "$REPO_ROOT"; then
  echo "refusing to scan a shallow clone; fetch full history first (git fetch --unshallow)" >&2
  exit 1
fi

status=0

echo "Running gitleaks over full git history"
if ! $LINT_COMPOSE gitleaks detect --source /repo --config .gitleaks.toml --redact --no-banner --log-opts="HEAD"; then
  status=1
fi

echo "Running gitleaks over the working tree"
if ! $LINT_COMPOSE gitleaks detect --no-git --source /repo --config .gitleaks.toml --redact --no-banner; then
  status=1
fi

exit "$status"
