# API Coverage — Enable Banking (account information for ING NL)

> Full coverage by default. Opt-outs are explicit, reasoned decisions.

Source of the capability list: the official OpenAPI file `https://enablebanking.com/docs/api/reference/enablebanking-api.yaml` (paths and methods enumerated during planning, 2026-09-30). The integration is `Ledger.Service/Ingestion/EnableBanking` (plan 02-13) behind the provider-neutral `IBankDataProvider`; the outbound allow-list admits exactly the INTEGRATE rows below.

| capability | decision | reason |
|---|---|---|
| application.get (GET /application) | INTEGRATE | |
| aspsps.list (GET /aspsps) | INTEGRATE | |
| auth.start (POST /auth) | INTEGRATE | |
| sessions.create (POST /sessions) | INTEGRATE | |
| sessions.get (GET /sessions/{session_id}) | OPT-OUT | not needed: consent problems surface as session error codes on the daily account-data calls, which already flip the consent state |
| sessions.delete (DELETE /sessions/{session_id}) | INTEGRATE | |
| accounts.details (GET /accounts/{account_id}/details) | OPT-OUT | not needed: the session response already carries every account field the link step shows (masked IBAN, type, bank name) |
| accounts.balances (GET /accounts/{account_id}/balances) | INTEGRATE | |
| accounts.transactions (GET /accounts/{id}/transactions, incl. paging) | INTEGRATE | |
| accounts.transaction-details (GET /accounts/{id}/transactions/{tid}) | OPT-OUT | not needed: the transaction list returns every retained field, and per-transaction calls would spend the per-account daily call budget |
| payments.create (POST /payments) | OPT-OUT | explicitly out of scope: bank access is read-only; no code path may initiate a payment |
| payments.get (GET /payments/{payment_id}) | OPT-OUT | explicitly out of scope: bank access is read-only |
| payments.delete (DELETE /payments/{payment_id}) | OPT-OUT | explicitly out of scope: bank access is read-only |
| payments.submit (POST /payments/{payment_id}/submit) | OPT-OUT | explicitly out of scope: bank access is read-only |
| payments.transactions (GET /payments/{payment_id}/transactions/{transaction_id}) | OPT-OUT | explicitly out of scope: bank access is read-only |
| psu-headers (Psu-Ip-Address, Psu-User-Agent on operator-triggered fetches) | INTEGRATE | |
