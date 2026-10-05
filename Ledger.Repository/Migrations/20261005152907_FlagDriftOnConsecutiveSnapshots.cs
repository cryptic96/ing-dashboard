using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Repository.Migrations
{
    /// <inheritdoc />
    public partial class FlagDriftOnConsecutiveSnapshots : Migration
    {
        /// <summary>
        /// The account status view with the reconciliation column changed to follow the flagged-drift rule. The shown balance
        /// is reported as reconciled when its snapshot matched, as not reconciled only when it drifted and the previous checked
        /// snapshot of the same kind drifted as well, and as unknown otherwise. A first mismatch is therefore never shown as a
        /// failure: it waits for the next day's snapshot to confirm it, so a card payment still in progress cannot raise an
        /// alarm. Each snapshot still records its own exact result. The columns, their order and their types are unchanged.
        /// </summary>
        private const string ReportingAccountStatusView = """
            CREATE OR REPLACE VIEW reporting.account_status AS
            SELECT
                a.account_key,
                COALESCE(a.display_name, a.account_key) AS account_name,
                (
                    SELECT max(r.finished_at)
                    FROM public.sync_runs r
                    WHERE r.bank_connection_id = c.id
                      AND r.outcome = 'succeeded'
                ) AS last_success_at,
                CASE
                    WHEN c.status = 'revoked' THEN 'revoked'
                    WHEN c.status = 'superseded' THEN 'superseded'
                    WHEN c.status = 'provider_expired' OR c.valid_until <= now() THEN 'expired'
                    WHEN c.valid_until - now() < interval '14 days' THEN 'expiring'
                    ELSE 'linked'
                END AS consent_state,
                GREATEST(0, floor(extract(epoch FROM (c.valid_until - now())) / 86400))::integer AS consent_days_left,
                b.amount AS balance_amount,
                b.currency AS balance_currency,
                to_char(b.snapshot_date, 'YYYY-MM-DD') AS balance_date,
                CASE
                    WHEN b.reconciled IS TRUE THEN 'yes'
                    WHEN b.reconciled IS FALSE AND p.reconciled IS FALSE THEN 'no'
                    ELSE 'unknown'
                END AS balance_reconciled,
                (
                    SELECT count(*)
                    FROM public.transactions t
                    WHERE t.account_id = a.id
                      AND t.status = 'pending'
                      AND t.match_flag = 'ambiguous'
                )::integer AS flagged_count
            FROM public.accounts a
            JOIN public.bank_connections c ON c.id = a.bank_connection_id
            LEFT JOIN LATERAL (
                SELECT s.amount, s.currency, s.snapshot_date, s.reconciled, s.balance_kind, s.created_at
                FROM public.balance_snapshots s
                WHERE s.account_id = a.id
                ORDER BY
                    s.snapshot_date DESC,
                    CASE s.balance_kind
                        WHEN 'closing_booked' THEN 0
                        WHEN 'interim_booked' THEN 1
                        ELSE 2
                    END,
                    s.balance_kind
                LIMIT 1
            ) b ON true
            LEFT JOIN LATERAL (
                SELECT q.reconciled
                FROM public.balance_snapshots q
                WHERE q.account_id = a.id
                  AND q.balance_kind = b.balance_kind
                  AND q.reconciled IS NOT NULL
                  AND (q.snapshot_date, q.created_at) < (b.snapshot_date, b.created_at)
                ORDER BY q.snapshot_date DESC, q.created_at DESC
                LIMIT 1
            ) p ON true
            WHERE a.sync_enabled;
            """;

        /// <summary>The account status view as it was before the flagged-drift rule: the shown snapshot's own verdict.</summary>
        private const string PreviousAccountStatusView = """
            CREATE OR REPLACE VIEW reporting.account_status AS
            SELECT
                a.account_key,
                COALESCE(a.display_name, a.account_key) AS account_name,
                (
                    SELECT max(r.finished_at)
                    FROM public.sync_runs r
                    WHERE r.bank_connection_id = c.id
                      AND r.outcome = 'succeeded'
                ) AS last_success_at,
                CASE
                    WHEN c.status = 'revoked' THEN 'revoked'
                    WHEN c.status = 'superseded' THEN 'superseded'
                    WHEN c.status = 'provider_expired' OR c.valid_until <= now() THEN 'expired'
                    WHEN c.valid_until - now() < interval '14 days' THEN 'expiring'
                    ELSE 'linked'
                END AS consent_state,
                GREATEST(0, floor(extract(epoch FROM (c.valid_until - now())) / 86400))::integer AS consent_days_left,
                b.amount AS balance_amount,
                b.currency AS balance_currency,
                to_char(b.snapshot_date, 'YYYY-MM-DD') AS balance_date,
                CASE b.reconciled
                    WHEN true THEN 'yes'
                    WHEN false THEN 'no'
                    ELSE 'unknown'
                END AS balance_reconciled,
                (
                    SELECT count(*)
                    FROM public.transactions t
                    WHERE t.account_id = a.id
                      AND t.status = 'pending'
                      AND t.match_flag = 'ambiguous'
                )::integer AS flagged_count
            FROM public.accounts a
            JOIN public.bank_connections c ON c.id = a.bank_connection_id
            LEFT JOIN LATERAL (
                SELECT s.amount, s.currency, s.snapshot_date, s.reconciled
                FROM public.balance_snapshots s
                WHERE s.account_id = a.id
                ORDER BY
                    s.snapshot_date DESC,
                    CASE s.balance_kind
                        WHEN 'closing_booked' THEN 0
                        WHEN 'interim_booked' THEN 1
                        ELSE 2
                    END,
                    s.balance_kind
                LIMIT 1
            ) b ON true
            WHERE a.sync_enabled;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ReportingAccountStatusView);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PreviousAccountStatusView);
        }
    }
}
