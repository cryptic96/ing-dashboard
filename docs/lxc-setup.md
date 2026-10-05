# Building and locking down the ledger host

This describes every one-time, manual step to take a fresh container from
nothing to a locked-down, running platform: creating it, provisioning it,
custodying its secrets, setting up Grafana and the reverse proxy, cutting
the first release, and proving the result. Every path, hostname and
address below is a placeholder — replace them with the server's own
values, and use `{braces}` values as a reminder of what still needs
filling in.

## What runs here, and what is reachable from where

The container runs the application, PostgreSQL, Grafana and Prometheus.
PostgreSQL has no network listener at all — only a local socket. Prometheus
and the application's own operational endpoint listen on loopback only, with
no route through the reverse proxy. Only the Grafana dashboard and the REST
API are reachable, and only from the home network and VPN, through the
existing reverse proxy. Nothing here is reachable from the public internet.

## Prerequisites

- A Proxmox host with an existing reverse proxy container (Traefik, with a
  certificate resolver already configured) and an existing outbound mail
  relay container.
- Local DNS control on the home router, to point internal hostnames at the
  reverse proxy.
- A password manager, for every secret this guide produces.
- A workstation with `age` installed, for the backup encryption key pair.

## 1. Create the container

On the Proxmox host, create an unprivileged container from an Ubuntu 24.04
template:

```bash
pct create {ctid} {storage}:vztmpl/ubuntu-24.04-standard_{arch}.tar.zst \
  --hostname ledger \
  --unprivileged 1 \
  --features nesting=1 \
  --cores 2 \
  --memory 2048 \
  --swap 512 \
  --rootfs {storage}:16 \
  --net0 name=eth0,bridge={bridge},ip={address}/24,gw={gateway} \
  --onboot 1
pct start {ctid}
```

## 2. Get the scripts onto the container

The first time, clone the public repository at its latest release tag
inside the container. After the first release is installed, provisioning
can instead be re-run from `/opt/ledger/current/deploy` — the installed
tree always carries the same scripts as the release it activated.

```bash
git clone --branch {latest-tag} https://example.com/example-owner/example-repo.git /root/ledger-src
cd /root/ledger-src
```

## 3. First provisioning run

Run the provisioning script as root. The first run only creates a
configuration file and stops:

```bash
./deploy/provision.sh
```

This creates `/etc/ledger/provision.conf` from its example. Open it and
fill in every value: the reverse proxy's address, the admin/VPN subnets
allowed to reach SSH, the Grafana and API hostnames, the mail relay, the
alert address, and the repository this container pulls releases from. Then
run provisioning again:

```bash
./deploy/provision.sh
```

This installs every package, service, systemd unit and firewall rule, sets
up PostgreSQL with its least-privilege roles, and brings up every platform
service. It also generates a Data Protection certificate and prompts for the
backup encryption key (see step 5) if one isn't configured yet.

## 4. Copy the Data Protection certificate and password into the password manager

Provisioning generates `/etc/ledger/dataprotection.pfx` and writes its
password into `/etc/ledger/ledger.env`. These protect the application's own
encryption key ring; losing them makes existing encrypted data
unrecoverable. Copy both the certificate file and the
`DataProtection__CertificatePassword` line from `ledger.env` into the
password manager now, before any real data exists. Neither is included in
database backups, on purpose — a backup and its Data Protection material
must never be recoverable from the same place.

## 5. Generate the backup encryption key pair

Backups are encrypted to a public key; the container only ever holds that
public key, so it can encrypt a backup but never decrypt one. On your own
workstation — not on the container:

```bash
age-keygen -o household-ledger-backup-key.txt
```

Store the entire file's contents in the password manager, then delete it
from the workstation. When provisioning (step 3) prompts for the backup
recipient, paste only the public key line (it starts with `age1`) — never
the private key.

## 6. Grafana admin and viewer accounts

Once Grafana is running, create its accounts from a terminal on the
container:

```bash
./deploy/provision.sh --only 60-grafana-accounts
```

This renames the built-in admin account away from its default login and
prompts for a strong password, then creates one Viewer account per
household member. Every password is typed at a hidden prompt and never
appears on the command line or in a log. Store the new admin login and
password, and each viewer's login and password, in the password manager.

## 7. Install the Traefik route

On the existing reverse proxy container, copy
`deploy/traefik/ledger.yml.example` into its dynamic configuration
directory, fill in the real LAN/VPN subnets, hostnames and the ledger
container's address, and rename it to drop the `.example` suffix.

## 8. Add local DNS records

In the home router, point the Grafana hostname and the API hostname at the
reverse proxy's address.

## 9. Apply the GitHub repository settings

Follow [the repository settings guide](github-repository-settings.md) once,
before the first release.

## 10. Cut the first release

Follow [the releasing guide](releasing.md) to tag, build and approve the
first release. Then watch the container pick it up:

```bash
journalctl -u ledger-deploy-poll -f
```

## 11. Create API keys

Once the application is running, create a named key per client (see
[the REST API guide](rest-api.md)):

```bash
ledger-apikey create operator
```

## 12. Run the on-host selfcheck

```bash
ledger-selfcheck --grafana-admin --restart-check
```

This proves the running container end to end: services and timers,
PostgreSQL's socket-only and per-role isolation, file permissions,
secrets hygiene (no GitHub credential, no stored backup identity, no
secret-shaped text in the journal or `/var/log`), the bank key file's
permissions, the Amsterdam time zone data, the firewall, the app's health and authentication, backup freshness, Grafana's
lockdown and account roles, and every Prometheus target. `--restart-check`
also restarts the application and requires it to stay healthy, proving the
Data Protection key ring survives a restart.

## 13. Take a backup and rehearse a restore

```bash
systemctl start ledger-backup@nightly.service
```

Then follow [the backup and restore guide](backup-restore.md) to perform
the restore drill at least once, with the private key supplied only from
the password manager.

## 14. External reachability test

From outside the home network and its VPN (for example, a phone on mobile
data with the VPN off), confirm the Grafana and API hostnames do not
answer at all. From inside the home network, confirm a REST call without a
key returns `401`.

## 15. Database access for the operator

There is no network route to PostgreSQL, by design. Reach it by SSHing into
the container and using `psql` there as the `postgres` OS user, or by
forwarding the same Unix socket over that SSH connection for a GUI tool
(DBeaver, pgAdmin). Never add a TCP listener to reach it any other way.

## 16. Logs

Every platform component logs through `journald`:

```bash
journalctl -u ledger -f
journalctl -u ledger-deploy-poll -f
journalctl -u ledger-backup@nightly -f
journalctl -u postgresql -f
journalctl -u grafana-server -f
journalctl -u prometheus -f
```

## 17. When to re-run provisioning

Provisioning is idempotent — re-run `./deploy/provision.sh` any time a
package pin, systemd unit or configuration template changes, or to pick up
a new server-side value after editing `/etc/ledger/provision.conf`. It
never overwrites the Data Protection certificate, the application
environment file, or the backup recipients file once they exist, and it
only re-runs the Grafana account setup when explicitly asked with
`--only 60-grafana-accounts`.

## Bank link key

The application reads bank transactions through an account-information
aggregator. The aggregator identifies the application by a private key that
signs each request, so the key has to exist on the container before the bank
accounts can be linked. It is generated on the container itself and never
travels.

Run these as root on the container:

```bash
ledger-bank-key generate
ledger-bank-key show-certificate
ledger-bank-key configure --application-id 00000000-0000-0000-0000-000000000000 \
  --redirect-url https://ledger.example.com/api/v1/bank/callback
```

- `generate` creates a 4096-bit RSA key protected by a random password, a
  ten-year self-signed certificate and the matching public key. It prints
  the certificate and the public key, never the password, and refuses to
  run when a key already exists, so an existing key is never overwritten.
- `show-certificate` prints the certificate and the public key again. The
  certificate (or the public key, depending on what the aggregator's control
  panel asks for) is what you upload there when registering the application.
  The private key is never uploaded.
- `configure` validates and stores the application id the control panel
  shows, selects the aggregator as the transaction source and stores the
  callback URL. The URL must be `https` and end in `/api/v1/bank/callback`
  on the hostname the API is reached through. Restart the application
  afterwards with `systemctl restart ledger.service`.

The files live in `/etc/ledger`:

| File | Mode | Content |
| --- | --- | --- |
| `enablebanking-key.pem` | 640, `root:ledger` | the password-protected private key |
| `enablebanking-cert.pem` | 644 | the certificate |
| `enablebanking-public.pem` | 644 | the public key |

The key's password is written to `/etc/ledger/ledger.env` next to the key's
path. Copy the key file and the env file into the password manager right
after generating them, exactly like the Data Protection certificate in step
4: database backups never contain either, on purpose. `ledger-selfcheck`
checks the key file's mode and owner and looks for the key, its password and
every other secret shape in the journal and under `/var/log`.

## What this guide never does

Prometheus, the application's operational endpoint and PostgreSQL are never
given a route through the reverse proxy, a public DNS record or a network
listener — they stay loopback-only or socket-only for the container's
entire life. Nothing in this guide gives the container a GitHub credential
or a runner registration; it only ever pulls already-published, already-
attested releases.
