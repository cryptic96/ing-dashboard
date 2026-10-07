# Connecting Claude to the ledger

This guide sets up the one internet-facing part of the platform, the MCP
endpoint, and connects Claude to it: Claude Code, claude.ai in the browser,
Claude Desktop and the mobile apps. Every hostname and address below is a
placeholder (`example.com`, and the documentation ranges `192.0.2.0/24` and
`198.51.100.0/24`); replace them with the household's own values.

## What Claude can do

Claude gets four read-only tools. It cannot change anything, categorise
anything or start a payment.

| Tool | What it is for |
| --- | --- |
| `ledger_overview` | Which accounts are synced, how fresh the data is and how far the history reaches. Claude calls it first. |
| `money_totals` | Every "how much" question: totals computed on the server, by period, per currency. |
| `search_transactions` | Capped lists of individual transactions, with a note whenever the list was cut short. |
| `find_counterparties` | How a merchant or person is spelled in the bank data, with their totals. |

Examples of questions that work: "How much did we spend in August?", "What
did we pay to the supermarket last quarter?", "What came in on the joint
account this year?", "How is the data synced, and when was the last sync?"

Things to know about the answers:

- Totals are grouped by counterparty name until categories exist, and Claude
  says so in the answer.
- Every total states its period (in the Amsterdam calendar), how many
  transactions it covers and what it left out, such as pending items or
  transfers between the household's own accounts. Claude never adds amounts
  up itself.
- Account numbers are masked in everything Claude sees: the counterparty's
  account is shown as its first two letters and last four characters, and an
  account number written inside a description or a counterparty name is
  masked the same way, however it is spelled. The rest of the text is shown
  as the bank sent it.

## How it is exposed

The platform uses one dedicated hostname for Claude, for example
`mcp.example.com`. It is separate from the dashboard and REST API
hostnames, which stay reachable from the home network and the VPN only.

On the MCP hostname the reverse proxy routes only the paths below and nothing
else:

| Paths | Who can reach them |
| --- | --- |
| `/mcp`, the protected-resource document under `/.well-known/`, the two authorization-server discovery documents under `/.well-known/` and `/connect/token` | Anthropic's published outbound address range, the home network and the VPN |
| `/connect/authorize` and everything under `/account/` (sign-in and consent) | The home network and the VPN only |

Anthropic's cloud makes every call a connector needs, so those paths must
be public to its range. Signing in is different: it happens in your own
browser, so the sign-in pages answer only to the home network and VPN, and
nobody on the internet can even load the login form.

The address list is defence in depth. The real control is OAuth: every
`/mcp` request needs a valid access token for this exact server, issued
after a password, an authenticator code and an explicit consent. The
application repeats the host and path rules a second time and answers `404`
for anything else on the MCP hostname.

## One-time setup

### 1. Switch the endpoint on

The application serves the MCP endpoint and the sign-in pages only when
`/etc/ledger/ledger.env` holds the public address and the networks that may
sign in:

```
OAuth__PublicBaseUrl=https://mcp.example.com
OAuth__SignInNetworks__0=192.0.2.0/24
OAuth__SignInNetworks__1=198.51.100.0/24
```

A freshly provisioned host gets these lines from `LEDGER_MCP_DOMAIN` and
`LEDGER_ADMIN_SSH_SOURCES` in `/etc/ledger/provision.conf`. On a host that
already has an environment file, add them by hand and restart the
application with `systemctl restart ledger.service`. Use the real hostname:
the application refuses to start with an `example.*` hostname, a trailing
slash or a path in the public address.

### 2. DNS

Two records give the hostname one meaning everywhere, so clients see one
issuer and one resource address:

- **Public:** an `A` record to the home network's public IPv4 address. It
  must be DNS-only, not proxied by the DNS provider, and there must be no
  `AAAA` record. Anthropic resolves the name first and refuses private,
  CGNAT, loopback, mixed or IPv6-only answers.
- **Local:** a record on the home router pointing the same hostname at the
  reverse proxy, so the home network and VPN clients reach it directly.

Confirm from outside with:

```bash
dig +short A mcp.example.com @192.0.2.53
dig +short AAAA mcp.example.com @192.0.2.53
```

The first must print the public address, the second nothing.

Two facts about the home connection cannot be checked from the repository,
so confirm them once:

- The router must forward port 443 to the reverse proxy **without source
  address translation**. The address list judges the address of the
  connecting peer, so a translation in front of the proxy makes every client
  look identical.
- The public address must not be CGNAT (a shared provider address). If the
  provider's WAN address differs from the address the internet sees, or sits
  in `100.64.0.0/10`, Anthropic cannot reach the endpoint.

The reverse proxy's certificate resolver must also be able to issue a
certificate for the new hostname.

### 3. Install the routers

`deploy/traefik/ledger.yml.example` already holds the MCP middleware and
both routers. Copy it into the reverse proxy's dynamic configuration
directory as described in [the host setup guide](lxc-setup.md), and fill in
the real hostnames, subnets and the ledger host's address.

Roll the public router out in two stages:

1. **Staged.** Before going public, change the `ledger-mcp-public` router's
   first middleware from `ledger-mcp-allow` to `ledger-lan-vpn-only`. Now
   nothing on the MCP hostname answers outside the home network and VPN, and
   you can prove the whole flow safely.
2. **Go-live.** Change it back to `ledger-mcp-allow` (see "Going public"
   below).

The reverse proxy must not hold back or collect responses for `/mcp`: replies
are streamed, and a response-collecting middleware stalls them. Do not add
one in front of that router.

### 4. Enrol a login

The login is the household member who may approve Claude's access. On the
ledger host:

```bash
sudo ledger-login create example-login
sudo ledger-login confirm-totp example-login 123456
```

`create` asks for a password twice without echo. Take it from the password
manager. It then shows the authenticator secret once. Put it in an
authenticator app or the password manager and nowhere else. `confirm-totp`
switches the login on after its first code, here `123456`, taken from the
authenticator. Adding a second household member is just another `create`.
The other commands (`reset-totp`, `set-password`, `list`, `remove`) are in
[the monitoring guide](monitoring.md#claude-access).

### 5. Connect Claude Code (from home or the VPN)

Claude Code signs in from your own machine, so do this on the home network
or over the VPN:

```bash
claude mcp add --transport http --client-id ledger-claude-code \
  --callback-port 33418 ledger https://mcp.example.com/mcp
```

Then open `/mcp` inside Claude Code and choose to authenticate. Your browser
opens the sign-in page (password, authenticator code), then a consent page
naming Claude Code. After you approve, ask something simple such as "give me
an overview of the ledger".

## Going public

Do this only after the connection from step 5 works and
`ledger-selfcheck` on the host reports no failures, including its checks
that no extra login can use sudo and that the MCP boundary answers as
designed.

1. Confirm Anthropic's address range is still current:

   ```bash
   LEDGER_LINT_NETWORK=1 bash build/tests/anthropic-ranges-network-test.sh
   ```

2. Change the public router's first middleware to `ledger-mcp-allow`.
3. From a machine on the home network or VPN:

   ```bash
   build/check-exposure.sh --from inside \
     --mcp-host mcp.example.com \
     --api-host ledger-api.example.com \
     --grafana-host grafana.example.com
   ```

4. From outside, such as a laptop tethered to a phone with the VPN off:

   ```bash
   build/check-exposure.sh --from outside \
     --mcp-host mcp.example.com \
     --api-host ledger-api.example.com \
     --grafana-host grafana.example.com
   ```

   Outside, every MCP, OAuth and sign-in path answers `403` (the proxy
   refuses it, because the address is not Anthropic's), every other path on
   the MCP hostname answers `404`, and the dashboard and REST hostnames
   answer `403`. A connection failure on the MCP hostname is also a failure:
   Anthropic cannot reach it either.

Both runs must end with no failures.

## Connect claude.ai

Do this once, from a desktop browser on the home network or the VPN:

1. In claude.ai open Settings, then Connectors, and add a custom connector.
2. Enter the URL `https://mcp.example.com/mcp`.
3. Open Advanced settings and choose **Use your own OAuth client**.
4. Enter the client ID `ledger-claude-hosted` and leave the secret empty.
5. Sign in on the page that opens (password, authenticator code) and approve
   the consent page, which names Claude and claude.ai.

Claude's authentication settings cannot be edited after the connector is
added; to change them, remove the connector and add it again.

### Claude Desktop and the mobile apps

Every custom connector, in claude.ai, Claude Desktop and the mobile apps
alike, is used from Anthropic's cloud, not from your device. Desktop and
the phone apps therefore use the same connector with no setup of their own,
and they work from anywhere, including mobile data without the VPN.

### Re-authorising

Anthropic's cloud does everything except the sign-in step, which happens in
your own browser. Whenever Claude asks you to sign in again, do it from a
browser on the home network or the VPN. On a phone, turn the VPN on first.

## Day to day

- Access tokens last 15 minutes and are renewed automatically. A connection
  that stays unused for about 90 days has to be signed in again.
- Approving a new connection sends an alert email, so an unexpected
  connection is noticed. The alerts, the metrics and the kill switch for
  revoking every connection at once are in
  [the monitoring guide](monitoring.md#claude-access).
- Until the ledger marks which text comes from the bank, keep ledger
  conversations in claude.ai separate from other connectors such as mail and
  calendar. Text in a transaction description is written by whoever sent the
  payment.

## Keeping Anthropic's range current

The template admits Anthropic's published outbound range, `160.79.104.0/21`,
on the public paths. Anthropic states that these addresses will not change
without notice. Before each release, run the range check from step 1 of
"Going public". It compares the template with Anthropic's IP address page
and fails when a range is missing from it. Addresses the page lists as
phased out must never appear in the template, and a test over the template
enforces that.

## Troubleshooting

- **Claude says it could not reach the server.** Check the DNS records
  first: the public `A` record must exist, must not be proxied, and there
  must be no `AAAA` record. Then check for a CGNAT address, a router that
  does not forward port 443, and an address list that is still the staged
  home-and-VPN-only one. Run `build/check-exposure.sh --from outside`.
- **The sign-in page says not found.** The browser is not on the home
  network or VPN, or the address in `OAuth__SignInNetworks__N` does not
  cover it. On a phone, turn the VPN on.
- **`invalid_target`, or Claude refuses the server.** The connector URL
  differs from `OAuth__PublicBaseUrl`. They must match exactly, in lower
  case, with no trailing slash.
- **Issuer mismatch.** The local DNS record is missing or resolves to
  something other than the reverse proxy, so the discovery documents name a
  different address than the one the client used.
- **Claude asks to reconnect.** The connection was revoked (for example
  after `sudo ledger-grants revoke-all`, or after a login's authenticator or
  password was reset or the login removed), or it has been unused for about
  90 days. Sign in again from the home network or VPN.
- **`ledger-selfcheck` fails on a login.** An account other than root can
  use sudo or has user id 0. Remove the account, or list a login that is
  meant to keep sudo in `LEDGER_SUDO_ALLOWED_USERS` in
  `/etc/ledger/provision.conf`.
