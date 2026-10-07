using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Repository.Migrations
{
    /// <inheritdoc />
    public partial class CheckVerdictsAcrossRecentSnapshots : Migration
    {
        /// <summary>
        /// The account status view with the reconciliation column decided the same way the metric decides it: the latest
        /// snapshot that carries a verdict counts, whichever kind or day that is, so a day without a verdict no longer shows as
        /// unknown while the metric still reports the older verdict. A mismatch is shown as not reconciled only when the
        /// previous verdict of the same kind is from at most three days earlier and mismatched as well, so two mismatches weeks
        /// apart are not read as persistence. The shown balance is still the latest snapshot's. The columns, their order and
        /// their types are unchanged. The three-day limit is the same one the status store applies to the metric.
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
                    WHEN v.reconciled IS TRUE THEN 'yes'
                    WHEN v.reconciled IS FALSE AND p.reconciled IS FALSE THEN 'no'
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
                SELECT s.amount, s.currency, s.snapshot_date
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
                SELECT s.reconciled, s.balance_kind, s.snapshot_date, s.created_at
                FROM public.balance_snapshots s
                WHERE s.account_id = a.id
                  AND s.reconciled IS NOT NULL
                ORDER BY s.snapshot_date DESC, s.created_at DESC
                LIMIT 1
            ) v ON true
            LEFT JOIN LATERAL (
                SELECT q.reconciled
                FROM public.balance_snapshots q
                WHERE q.account_id = a.id
                  AND q.balance_kind = v.balance_kind
                  AND q.reconciled IS NOT NULL
                  AND (q.snapshot_date, q.created_at) < (v.snapshot_date, v.created_at)
                  AND q.snapshot_date >= v.snapshot_date - 3
                ORDER BY q.snapshot_date DESC, q.created_at DESC
                LIMIT 1
            ) p ON true
            WHERE a.sync_enabled;
            """;

        /// <summary>The account status view as it was before: the verdict of the latest snapshot row, with no limit on how long ago the previous verdict was.</summary>
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
