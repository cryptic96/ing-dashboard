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
no route through the reverse proxy. The Grafana dashboard and the REST API
are reachable only from the home network and VPN, through the existing
reverse proxy, which reaches the application over an encrypted connection
(see step 7). The only part open to the internet is the MCP endpoint Claude
connects to, on its own hostname and behind a sign-in, described in [the
Claude connection guide](mcp.md).

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

The template also holds the routers for the MCP hostname: a public router
for exactly the MCP and OAuth paths, open to Anthropic's published range
plus the home network and VPN, and a sign-in router for the home network and
VPN only. Install the public router on the home-and-VPN-only middleware
first and switch it to Anthropic's range only at go-live; the staged
rollout is in [the Claude connection guide](mcp.md).

### Trust the ledger host's certificate

The hop from the reverse proxy to the application is encrypted. The
application serves HTTPS on port 5080 with a certificate that provisioning
generated on the ledger host (`/etc/ledger/backend-tls.crt`, valid for ten
years, naming only the internal name `ledger-backend`), and the route file
trusts exactly that one certificate. Anyone who intercepts the hop sees only
ciphertext, and nothing but the ledger host can pass as the application.
Certificate checking is never switched off.

1. On the ledger host, print the **public** certificate and its fingerprint.
   The private key (`/etc/ledger/backend-tls.key`) never leaves the ledger
   host and must not be copied anywhere:

   ```bash
   cat /etc/ledger/backend-tls.crt
   openssl x509 -in /etc/ledger/backend-tls.crt -noout -fingerprint -sha256
   ```

2. In the route file on the reverse proxy, paste the certificate (the lines
   from `-----BEGIN CERTIFICATE-----` to `-----END CERTIFICATE-----`) in
   place of the placeholder under `serversTransports`, keeping the
   indentation. The proxy reads the certificate from this file itself, so no
   other file is needed there.

3. Check that the proxy sees the same certificate the ledger host holds. On
   the reverse proxy host (the only machine the firewall lets reach port
   5080), print the fingerprint of what the application presents and compare
   it with the one from step 1:

   ```bash
   openssl s_client -connect 192.0.2.20:5080 -servername ledger-backend </dev/null 2>/dev/null \
     | openssl x509 -noout -fingerprint -sha256
   ```

The hop from the reverse proxy to Grafana on port 3000 is still plain HTTP.
It carries Grafana sign-ins and dashboard data, and encrypting it the same way
is tracked as a follow-up.

#### Rotating the certificate

Rotate the certificate if the private key may have been exposed, or well
before it expires (`ledger-selfcheck` warns thirty days ahead). Update the
proxy first, so there is never a moment when the application presents a
certificate the proxy does not trust:

1. On the ledger host, generate the new pair in a scratch directory instead
   of `/etc/ledger`:

   ```bash
   install -d -m 700 /root/backend-tls-new
   LEDGER_BACKEND_TLS_DIR=/root/backend-tls-new bash -c 'source /usr/local/lib/ledger/backend-tls.sh && ledger_ensure_backend_tls'
   ```

2. In the route file, add the new certificate as a second entry under
   `rootCAs`, next to the old one. The proxy now accepts either.
3. On the ledger host, move the old pair out of `/etc/ledger`, move the new
   pair in, and restart the application:

   ```bash
   install -d -m 700 /root/backend-tls-old
   mv /etc/ledger/backend-tls.crt /etc/ledger/backend-tls.key /root/backend-tls-old/
   mv /root/backend-tls-new/backend-tls.crt /root/backend-tls-new/backend-tls.key /etc/ledger/
   systemctl restart ledger
   ```

4. Run `ledger-selfcheck` and compare fingerprints as above. Once the API
   and the MCP endpoint answer through the proxy, remove the old entry from
   `rootCAs`, then destroy the old key with
   `shred -u /root/backend-tls-old/backend-tls.key` and delete the scratch
   directories.

#### Upgrading a host that has no certificate yet

A host built before the encrypted hop existed has no certificate, and the
release that adds it refuses to start without one. Because that release also
migrates the database, a failed start is not rolled back automatically, so
the certificate must exist before the release is approved:

1. Check out the release tag on the ledger host (see step 2) and re-run
   `./deploy/provision.sh`. This creates the certificate, if it is missing,
   and installs the current installer, which also creates it before starting
   any later release. It does not install a release or restart the
   application.
2. Print the certificate and update the route file as above, but do not save
   it on the reverse proxy yet: the proxy would then speak HTTPS to an
   application that still answers plain HTTP.
3. Approve the release. As soon as the installer reports success, save the
   updated route file on the reverse proxy. The REST API and the MCP endpoint
   answer with a bad gateway error for the minutes in between, so do this
   when nobody is using them. Grafana is unaffected.

Rolling back to a release from before the encrypted hop makes the application
answer plain HTTP again, so the route file has to go back to its previous
form at the same time.

## 8. Add local DNS records

In the home router, point the Grafana hostname, the API hostname and the
MCP hostname at the reverse proxy's address. The MCP hostname additionally
needs a public IPv4 `A` record that is DNS-only, with no `AAAA` record (see
[the Claude connection guide](mcp.md)).

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
lockdown and account roles, and every Prometheus target. It also fails when
any account other than root and the logins listed in
`LEDGER_SUDO_ALLOWED_USERS` can use sudo or has user id 0. The application's
proxy-facing port must speak only TLS: every probe of it trusts exactly the
certificate in `/etc/ledger/backend-tls.crt` and never skips certificate
checking, and the selfcheck fails when the port presents any other
certificate, when a plain-HTTP request to it gets an application response, or
when the certificate expires within thirty days. Once the MCP address is
configured, it also proves on the host, over that pinned connection, that
`/mcp` answers `401` with its discovery challenge, that the
protected-resource document names the configured address, that the REST
status path answers `404` on the MCP hostname and that the sign-in page
answers `404` to an address outside the home network and VPN. Tokens, token and code-verifier parameters and
authenticator enrolment links are added to the secret shapes it looks for in
the journal and under `/var/log`. `--restart-check`
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

Run the exposure check from a machine outside the home network and its VPN
(for example, a laptop tethered to a phone with the VPN off), and again from
inside:

```bash
build/check-exposure.sh --from outside \
  --mcp-host mcp.example.com \
  --api-host ledger-api.example.com \
  --grafana-host grafana.example.com

build/check-exposure.sh --from inside \
  --mcp-host mcp.example.com \
  --api-host ledger-api.example.com \
  --grafana-host grafana.example.com
```

Outside, the dashboard and REST hostnames answer `403`, every MCP, OAuth and
sign-in path of the MCP hostname answers `403` while the public router is
still on the home-and-VPN-only list (and after go-live too, for any address
that is not Anthropic's), and every other path answers `404`. Inside,
discovery, the `401` challenge on `/mcp`, the sign-in page and a REST call
without a key (`401`) behave as designed. Each run prints one line per
request and ends with a count of failures.

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

## Connect Claude

Once the host is locked down and the exposure check passes, follow [the
Claude connection guide](mcp.md) to enrol a login, connect Claude Code and
claude.ai, and go public with the MCP endpoint.

## What this guide never does

Prometheus, the application's operational endpoint and PostgreSQL are never
given a route through the reverse proxy, a public DNS record or a network
listener — they stay loopback-only or socket-only for the container's
entire life. Nothing in this guide gives the container a GitHub credential
or a runner registration; it only ever pulls already-published, already-
attested releases.
