# Backing up and restoring the database

This describes how the database is backed up automatically, what those
backups do and do not contain, and how to rehearse or perform a restore.
Every path, hostname and address below is a placeholder — replace them with
the server's own values.

## Setting up the key pair, once

Backups are encrypted to a public key. The server only ever holds that
public key, so it can encrypt a backup but never decrypt one. The matching
private key (the "identity") never touches the server at all.

1. On your own workstation — not on the server — generate a key pair:

   ```bash
   age-keygen -o household-ledger-backup-key.txt
   ```

   This prints the public key to the terminal and writes both the public
   and the private key into the file.

2. Open a password manager entry for the household ledger and store the
   entire contents of that file there, then delete the file from your
   workstation. The password manager is now the only place the private key
   lives.

3. Copy only the public key line (it starts with `age1`) into the server's
   recipients file, one key per line:

   ```bash
   echo 'age1examplepublickey...' > /etc/ledger/backup-recipients.txt
   ```

If the private key is ever lost, every existing backup becomes permanently
unreadable — there is no recovery path around the password manager. If it
is ever exposed, generate a new pair, replace the recipients file, and
treat every backup encrypted with the old key as no longer confidential.

## What a backup contains, and what it deliberately does not

Every backup is a single encrypted database dump — the transactions,
budgets, goals, categorisation rules and everything else that lives in the
`ledger` database, nothing more.

A backup never contains:

- the server's env file (database connection details, mail settings)
- the Data Protection certificate or its password
- the bank aggregator's key or session tokens
- any age identity, including the one the recipients file was generated
  from

Keep separate copies of those in the same password manager. They are
needed to stand the server back up from nothing; a database backup alone
is not enough for that.

## Schedule, naming and retention

A backup runs nightly, and once more automatically right before any
release that changes the database schema is installed. Each one is named
`ledger-<UTC timestamp>-<reason>.dump.age`, for example
`ledger-20260101T023000Z-nightly.dump.age`.

Old backups are pruned on a grandfather-father-son schedule: the newest
backup for each of the 7 most recent days, the 4 most recent weeks and the
12 most recent months are kept, along with the single newest
pre-migration backup. Everything else is removed automatically.

## Where freshness shows up

Every backup run publishes its outcome as metrics: whether it succeeded,
how large the result was, and when the last one actually succeeded (kept
unchanged if a run fails, so staleness is visible even through repeated
failures). Alerts fire when no backup has succeeded recently, or when the
last run failed outright — the same place every other platform alert
appears.

## Restore drill (rehearsal, no risk to live data)

The drill decrypts a chosen backup into a separate, throwaway database,
compares it against the live one, and reports pass or fail — the live
database is never touched.

1. Copy the backup you want to test from the server, or run everything
   directly on the server as the drill only ever writes to its own scratch
   database.
2. Retrieve the private key from the password manager. Do not save it to a
   file on the server; paste it straight through standard input so it
   never lands on disk:

   ```bash
   ledger-restore --drill \
     --backup /var/backups/ledger/ledger-20260101T023000Z-nightly.dump.age \
     --identity /dev/stdin
   ```

   Paste the private key, then send end-of-input (Ctrl-D).
3. Read the report. A pass means the backup restores cleanly and its
   migration history, canary row and table counts match the live
   database (or are an expected, older subset of it). A failure needs
   investigating before you trust that backup.

Repeat this drill whenever you like — it is the "tested restore" this
setup relies on, and nothing about it can affect the running database.

## Live restore (replaces the running database)

Only do this to actually recover from data loss or corruption. It requires
typed confirmation, stops the application, and keeps the previous database
around under a timestamped name rather than deleting it outright.

1. Retrieve the private key from the password manager, as above.
2. Run:

   ```bash
   ledger-restore --live \
     --backup /var/backups/ledger/ledger-20260101T023000Z-nightly.dump.age \
     --identity /dev/stdin
   ```

   Paste the private key, send end-of-input, then type `ledger` when asked
   to confirm.
3. If the server itself was rebuilt (rather than just its database), also
   restore the Data Protection certificate and its password from the
   password manager before this step — a database with no matching
   certificate cannot decrypt anything Data Protection previously
   encrypted.
4. Once the application reports healthy again, confirm the data looks
   right, then drop the previous, timestamped database the restore left
   behind.

## Accepted risk: backups share the server's own disk

Backups are stored on the same disk as the server itself. Losing that
disk, the server, or the host it runs on loses the live database and every
backup at the same time — there is currently no offsite or
outside-the-server copy. This is a deliberate, accepted tradeoff for now;
adding an offsite copy is a later decision, not a gap in what is documented
here.
