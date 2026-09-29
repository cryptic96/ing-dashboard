#!/usr/bin/env bash
# Shared functions behind encrypted database backups: recipient validation,
# backup file naming and grandfather-father-son retention selection. Sourced
# by deploy/bin/ledger-backup and deploy/bin/ledger-restore, never executed
# directly.

if [ -n "${LEDGER_BACKUP_SH_LOADED:-}" ]; then
  return 0
fi
LEDGER_BACKUP_SH_LOADED=1

# Refuses a recipients file that is missing, empty, contains a blank line
# only, contains anything that is not an age1... public key, or contains
# what looks like an age private identity. Blank lines are otherwise
# allowed and ignored.
ledger_backup_validate_recipients() {
  local file="$1"

  if [ ! -f "$file" ]; then
    ledger_die "recipients file not found: $file"
  fi

  # A rejected line is never echoed: it may be a pasted private identity, so
  # errors name only the line number. The identity marker is searched for
  # anywhere in the line, so a stray leading character cannot hide it.
  local line found=0 line_number=0
  while IFS= read -r line || [ -n "$line" ]; do
    line_number=$((line_number + 1))
    [ -z "$line" ] && continue

    if [[ "${line^^}" == *AGE-SECRET-KEY-* ]]; then
      ledger_die "recipients file $file line ${line_number} looks like a private age identity, refusing (the line is not shown)"
    fi

    if [[ ! "$line" =~ ^age1[0-9a-z]+$ ]]; then
      ledger_die "recipients file $file line ${line_number} is not an age public key (the line is not shown)"
    fi

    found=1
  done < "$file"

  if [ "$found" -eq 0 ]; then
    ledger_die "recipients file $file contains no age public keys"
  fi
}

# Prints the backup file name for the given reason (nightly or
# pre-migration) and Unix epoch seconds.
ledger_backup_filename() {
  local reason="$1"
  local epoch="$2"
  printf 'ledger-%s-%s.dump.age\n' "$(date -u -d "@${epoch}" +%Y%m%dT%H%M%SZ)" "$reason"
}

# Reads candidate backup file names on stdin (one per line) and prints, one
# per line, the names that grandfather-father-son retention selects for
# deletion. Keeps exactly the newest name per day for the 7 newest days,
# the newest name per ISO week for the 4 newest weeks, the newest name per
# month for the 12 newest months, and the single newest pre-migration name
# overall; deletes every other name that matches the backup pattern. Names
# that do not match the pattern are never reported, in either direction.
# The result does not depend on the order names are given in.
ledger_backup_select_deletions() {
  local line date_part time_part reason iso epoch week
  local -a names=() epochs=() reasons=() days=() weeks=() months=()

  while IFS= read -r line || [ -n "$line" ]; do
    [ -z "$line" ] && continue
    if [[ "$line" =~ ^ledger-([0-9]{8})T([0-9]{6})Z-(nightly|pre-migration)\.dump\.age$ ]]; then
      date_part="${BASH_REMATCH[1]}"
      time_part="${BASH_REMATCH[2]}"
      reason="${BASH_REMATCH[3]}"
      iso="${date_part:0:4}-${date_part:4:2}-${date_part:6:2}T${time_part:0:2}:${time_part:2:2}:${time_part:4:2}Z"
      read -r epoch week < <(date -u -d "$iso" '+%s %G-%V')

      names+=("$line")
      epochs+=("$epoch")
      reasons+=("$reason")
      days+=("$date_part")
      weeks+=("$week")
      months+=("${date_part:0:6}")
    fi
  done

  [ "${#names[@]}" -eq 0 ] && return 0

  local -a order
  mapfile -t order < <(
    for i in "${!names[@]}"; do
      printf '%s\t%s\t%s\n' "${epochs[$i]}" "${names[$i]}" "$i"
    done | sort -t "$(printf '\t')" -k1,1nr -k2,2r | cut -f3
  )

  local newest_premigration_idx=""
  local i
  for i in "${order[@]}"; do
    if [ "${reasons[$i]}" = "pre-migration" ]; then
      newest_premigration_idx="$i"
      break
    fi
  done

  local days_kept=0 weeks_kept=0 months_kept=0 premigration_kept=0
  local -A seen_days=() seen_weeks=() seen_months=()

  for i in "${order[@]}"; do
    local keep=0

    if [ -z "${seen_days[${days[$i]}]:-}" ] && [ "$days_kept" -lt 7 ]; then
      keep=1
      seen_days[${days[$i]}]=1
      days_kept=$((days_kept + 1))
    elif [ -z "${seen_weeks[${weeks[$i]}]:-}" ] && [ "$weeks_kept" -lt 4 ]; then
      keep=1
      seen_weeks[${weeks[$i]}]=1
      weeks_kept=$((weeks_kept + 1))
    elif [ -z "${seen_months[${months[$i]}]:-}" ] && [ "$months_kept" -lt 12 ]; then
      keep=1
      seen_months[${months[$i]}]=1
      months_kept=$((months_kept + 1))
    elif [ -n "$newest_premigration_idx" ] && [ "$i" = "$newest_premigration_idx" ] && [ "$premigration_kept" -eq 0 ]; then
      keep=1
      premigration_kept=1
    fi

    if [ "$keep" -eq 0 ]; then
      printf '%s\n' "${names[$i]}"
    fi
  done
}
