using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountStatusView : Migration
    {
        /// <summary>
        /// One row per selected account: when data last arrived, how long the consent lasts, the latest balance and whether
        /// it reconciles, and how many pending rows have an unclear match. It exposes no account number, session material or payload.
        /// </summary>
        private const string ReportingAccountStatusView = """
            CREATE VIEW reporting.account_status AS
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
            migrationBuilder.Sql("DROP VIEW reporting.account_status;");
        }
    }
}
