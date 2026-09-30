using System;
using Ledger.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ledger.Repository.Migrations
{
    [DbContext(typeof(LedgerDbContext))]
    [Migration("20260930183122_AddBankAuthorizations")]
    partial class AddBankAuthorizations
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.12")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("Ledger.Repository.Entities.ApiKeyEntity", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created_at");

                    b.Property<string>("KeyId")
                        .IsRequired()
                        .HasMaxLength(16)
                        .HasColumnType("character varying(16)")
                        .HasColumnName("key_id");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)")
                        .HasColumnName("name");

                    b.Property<DateTimeOffset?>("RevokedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("revoked_at");

                    b.Property<byte[]>("SecretSha256")
                        .IsRequired()
                        .HasColumnType("bytea")
                        .HasColumnName("secret_sha256");

                    b.HasKey("Id")
                        .HasName("pk_api_keys");

                    b.HasIndex("KeyId")
                        .IsUnique()
                        .HasDatabaseName("ix_api_keys_key_id");

                    b.HasIndex("Name")
                        .IsUnique()
                        .HasDatabaseName("ix_api_keys_name")
                        .HasFilter("revoked_at IS NULL");

                    b.ToTable("api_keys");
                });

            modelBuilder.Entity("Ledger.Repository.Entities.BankAuthorizationEntity", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id");

                    b.Property<Guid?>("ConnectionId")
                        .HasColumnType("uuid")
                        .HasColumnName("connection_id");

                    b.Property<DateTimeOffset?>("ConsumedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("consumed_at");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created_at");

                    b.Property<DateTimeOffset>("ExpiresAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("expires_at");

                    b.Property<string>("ProviderAuthorizationId")
                        .IsRequired()
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)")
                        .HasColumnName("provider_authorization_id");

                    b.Property<string>("Purpose")
                        .IsRequired()
                        .HasMaxLength(16)
                        .HasColumnType("character varying(16)")
                        .HasColumnName("purpose");

                    b.Property<byte[]>("StateSha256")
                        .IsRequired()
                        .HasColumnType("bytea")
                        .HasColumnName("state_sha256");

                    b.HasKey("Id")
                        .HasName("pk_bank_authorizations");

                    b.HasIndex("ConnectionId")
                        .HasDatabaseName("ix_bank_authorizations_connection_id");

                    b.HasIndex("StateSha256")
                        .IsUnique()
                        .HasDatabaseName("ix_bank_authorizations_state_sha256");

                    b.ToTable("bank_authorizations", t =>
                        {
                            t.HasCheckConstraint("ck_bank_authorizations_purpose", "purpose IN ('link', 'renew')");
                        });
                });

            modelBuilder.Entity("Ledger.Repository.Entities.BankConnectionEntity", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id");

                    b.Property<string>("AspspCountry")
                        .IsRequired()
                        .HasMaxLength(2)
                        .HasColumnType("character varying(2)")
                        .HasColumnName("aspsp_country");

                    b.Property<string>("AspspName")
                        .IsRequired()
                        .HasMaxLength(128)
                        .HasColumnType("character varying(128)")
                        .HasColumnName("aspsp_name");

                    b.Property<DateTimeOffset>("AuthorizedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("authorized_at");

                    b.Property<DateTimeOffset?>("ClosedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("closed_at");

                    b.Property<string>("ConnectionKey")
                        .IsRequired()
                        .HasMaxLength(16)
                        .HasColumnType("character varying(16)")
                        .HasColumnName("connection_key");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created_at");

                    b.Property<string>("Provider")
                        .IsRequired()
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)")
                        .HasColumnName("provider");

                    b.Property<string>("SessionIdProtected")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("session_id_protected");

                    b.Property<string>("Status")
                        .IsRequired()
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)")
                        .HasColumnName("status");

                    b.Property<Guid?>("SupersededById")
                        .HasColumnType("uuid")
                        .HasColumnName("superseded_by_id");

                    b.Property<DateTimeOffset>("ValidUntil")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("valid_until");

                    b.HasKey("Id")
                        .HasName("pk_bank_connections");

                    b.HasIndex("ConnectionKey")
                        .IsUnique()
                        .HasDatabaseName("ix_bank_connections_connection_key");

                    b.HasIndex("SupersededById")
                        .HasDatabaseName("ix_bank_connections_superseded_by_id");

                    b.ToTable("bank_connections", t =>
                        {
                            t.HasCheckConstraint("ck_bank_connections_status", "status IN ('active', 'provider_expired', 'revoked', 'superseded')");
                        });
                });

            modelBuilder.Entity("Ledger.Repository.Entities.DataProtectionCanaryEntity", b =>
                {
                    b.Property<short>("Id")
                        .HasColumnType("smallint")
                        .HasColumnName("id");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created_at");

                    b.Property<byte[]>("PlaintextSha256")
                        .IsRequired()
                        .HasColumnType("bytea")
                        .HasColumnName("plaintext_sha256");

                    b.Property<string>("ProtectedPayload")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("protected_payload");

                    b.HasKey("Id")
                        .HasName("pk_data_protection_canary");

                    b.ToTable("data_protection_canary", t =>
                        {
                            t.HasCheckConstraint("ck_data_protection_canary_id", "id = 1");
                        });
                });

            modelBuilder.Entity("Ledger.Repository.Entities.LedgerAccountEntity", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id");

                    b.Property<string>("AccountKey")
                        .IsRequired()
                        .HasMaxLength(16)
                        .HasColumnType("character varying(16)")
                        .HasColumnName("account_key");

                    b.Property<Guid>("BankConnectionId")
                        .HasColumnType("uuid")
                        .HasColumnName("bank_connection_id");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created_at");

                    b.Property<string>("Currency")
                        .IsRequired()
                        .HasMaxLength(3)
                        .HasColumnType("character(3)")
                        .HasColumnName("currency")
                        .IsFixedLength();

                    b.Property<string>("DisplayName")
                        .HasColumnType("text")
                        .HasColumnName("display_name");

                    b.Property<string>("Iban")
                        .HasColumnType("text")
                        .HasColumnName("iban");

                    b.Property<string>("IdentificationHash")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("identification_hash");

                    b.Property<string>("Kind")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("kind");

                    b.Property<string>("Product")
                        .HasColumnType("text")
                        .HasColumnName("product");

                    b.Property<string>("Provider")
                        .IsRequired()
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)")
                        .HasColumnName("provider");

                    b.Property<string>("ProviderAccountUid")
                        .HasColumnType("text")
                        .HasColumnName("provider_account_uid");

                    b.Property<string>("ProviderName")
                        .HasColumnType("text")
                        .HasColumnName("provider_name");

                    b.Property<bool>("SyncEnabled")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(false)
                        .HasColumnName("sync_enabled");

                    b.HasKey("Id")
                        .HasName("pk_accounts");

                    b.HasIndex("AccountKey")
                        .IsUnique()
                        .HasDatabaseName("ix_accounts_account_key");

                    b.HasIndex("BankConnectionId")
                        .HasDatabaseName("ix_accounts_bank_connection_id");

                    b.HasIndex("Provider", "IdentificationHash")
                        .IsUnique()
                        .HasDatabaseName("ix_accounts_provider_identification_hash");

                    b.ToTable("accounts", t =>
                        {
                            t.HasCheckConstraint("ck_accounts_kind", "kind IN ('current', 'savings', 'card', 'other')");
                        });
                });

            modelBuilder.Entity("Ledger.Repository.Entities.LedgerTransactionEntity", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id");

                    b.Property<Guid>("AccountId")
                        .HasColumnType("uuid")
                        .HasColumnName("account_id");

                    b.Property<decimal>("Amount")
                        .HasColumnType("numeric(19,4)")
                        .HasColumnName("amount");

                    b.Property<DateTimeOffset?>("BookedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("booked_at");

                    b.Property<DateOnly?>("BookingDate")
                        .HasColumnType("date")
                        .HasColumnName("booking_date");

                    b.Property<string>("CounterpartyIban")
                        .HasColumnType("text")
                        .HasColumnName("counterparty_iban");

                    b.Property<string>("CounterpartyName")
                        .HasColumnType("text")
                        .HasColumnName("counterparty_name");

                    b.Property<string>("Currency")
                        .IsRequired()
                        .HasMaxLength(3)
                        .HasColumnType("character(3)")
                        .HasColumnName("currency")
                        .IsFixedLength();

                    b.Property<string>("Description")
                        .HasColumnType("text")
                        .HasColumnName("description");

                    b.Property<DateTimeOffset?>("DroppedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("dropped_at");

                    b.Property<DateTimeOffset>("FirstSeenAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("first_seen_at");

                    b.Property<string>("MatchFlag")
                        .HasColumnType("text")
                        .HasColumnName("match_flag");

                    b.Property<string>("Status")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("status");

                    b.Property<DateOnly?>("TransactionDate")
                        .HasColumnType("date")
                        .HasColumnName("transaction_date");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updated_at");

                    b.Property<DateOnly?>("ValueDate")
                        .HasColumnType("date")
                        .HasColumnName("value_date");

                    b.HasKey("Id")
                        .HasName("pk_transactions");

                    b.HasIndex("AccountId", "BookingDate")
                        .HasDatabaseName("ix_transactions_account_id_booking_date");

                    b.HasIndex("AccountId", "Status")
                        .HasDatabaseName("ix_transactions_account_id_status");

                    b.ToTable("transactions", t =>
                        {
                            t.HasCheckConstraint("ck_transactions_currency", "currency ~ '^[A-Z]{3}$'");

                            t.HasCheckConstraint("ck_transactions_match_flag", "match_flag IS NULL OR match_flag IN ('ambiguous')");

                            t.HasCheckConstraint("ck_transactions_status", "status IN ('pending', 'booked', 'dropped')");
                        });
                });

            modelBuilder.Entity("Ledger.Repository.Entities.SyncRunEntity", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id");

                    b.Property<Guid>("BankConnectionId")
                        .HasColumnType("uuid")
                        .HasColumnName("bank_connection_id");

                    b.Property<int>("CallsMade")
                        .HasColumnType("integer")
                        .HasColumnName("calls_made");

                    b.Property<int>("Dropped")
                        .HasColumnType("integer")
                        .HasColumnName("dropped");

                    b.Property<DateTimeOffset?>("FinishedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("finished_at");

                    b.Property<int>("Flagged")
                        .HasColumnType("integer")
                        .HasColumnName("flagged");

                    b.Property<int>("Inserted")
                        .HasColumnType("integer")
                        .HasColumnName("inserted");

                    b.Property<string>("Outcome")
                        .HasColumnType("text")
                        .HasColumnName("outcome");

                    b.Property<string>("ProviderError")
                        .HasMaxLength(64)
                        .HasColumnType("character varying(64)")
                        .HasColumnName("provider_error");

                    b.Property<DateTimeOffset>("StartedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("started_at");

                    b.Property<string>("Trigger")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("trigger");

                    b.Property<int>("Updated")
                        .HasColumnType("integer")
                        .HasColumnName("updated");

                    b.HasKey("Id")
                        .HasName("pk_sync_runs");

                    b.HasIndex("BankConnectionId")
                        .IsUnique()
                        .HasDatabaseName("ux_sync_runs_unfinished_per_connection")
                        .HasFilter("finished_at IS NULL");

                    b.ToTable("sync_runs", t =>
                        {
                            t.HasCheckConstraint("ck_sync_runs_outcome", "outcome IS NULL OR outcome IN ('succeeded', 'failed_transient', 'failed_rate_limited', 'failed_consent', 'failed_provider_auth', 'failed_malformed', 'quota_exhausted', 'abandoned')");

                            t.HasCheckConstraint("ck_sync_runs_trigger", "trigger IN ('scheduled', 'retry', 'post_link', 'manual')");
                        });
                });

            modelBuilder.Entity("Ledger.Repository.Entities.TransactionPayloadEntity", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id");

                    b.Property<DateTimeOffset>("ObservedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("observed_at");

                    b.Property<string>("Payload")
                        .IsRequired()
                        .HasColumnType("jsonb")
                        .HasColumnName("payload");

                    b.Property<byte[]>("PayloadSha256")
                        .IsRequired()
                        .HasColumnType("bytea")
                        .HasColumnName("payload_sha256");

                    b.Property<Guid>("SyncRunId")
                        .HasColumnType("uuid")
                        .HasColumnName("sync_run_id");

                    b.Property<Guid>("TransactionId")
                        .HasColumnType("uuid")
                        .HasColumnName("transaction_id");

                    b.HasKey("Id")
                        .HasName("pk_transaction_payloads");

                    b.HasIndex("SyncRunId")
                        .HasDatabaseName("ix_transaction_payloads_sync_run_id");

                    b.HasIndex("TransactionId", "ObservedAt")
                        .HasDatabaseName("ix_transaction_payloads_transaction_id_observed_at");

                    b.ToTable("transaction_payloads");
                });

            modelBuilder.Entity("Ledger.Repository.Entities.TransactionRefEntity", b =>
                {
                    b.Property<Guid>("AccountId")
                        .HasColumnType("uuid")
                        .HasColumnName("account_id");

                    b.Property<string>("Ref")
                        .HasColumnType("text")
                        .HasColumnName("ref");

                    b.Property<DateTimeOffset>("FirstSeenAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("first_seen_at");

                    b.Property<string>("FirstStatus")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("first_status");

                    b.Property<Guid>("TransactionId")
                        .HasColumnType("uuid")
                        .HasColumnName("transaction_id");

                    b.HasKey("AccountId", "Ref")
                        .HasName("pk_transaction_refs");

                    b.HasIndex("TransactionId")
                        .HasDatabaseName("ix_transaction_refs_transaction_id");

                    b.ToTable("transaction_refs", t =>
                        {
                            t.HasCheckConstraint("ck_transaction_refs_first_status", "first_status IN ('pending', 'booked', 'dropped')");
                        });
                });

            modelBuilder.Entity("Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("FriendlyName")
                        .HasColumnType("text")
                        .HasColumnName("friendly_name");

                    b.Property<string>("Xml")
                        .HasColumnType("text")
                        .HasColumnName("xml");

                    b.HasKey("Id")
                        .HasName("pk_data_protection_keys");

                    b.ToTable("data_protection_keys");
                });

            modelBuilder.Entity("Ledger.Repository.Entities.BankAuthorizationEntity", b =>
                {
                    b.HasOne("Ledger.Repository.Entities.BankConnectionEntity", null)
                        .WithMany()
                        .HasForeignKey("ConnectionId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .HasConstraintName("fk_bank_authorizations_bank_connections_connection_id");
                });

            modelBuilder.Entity("Ledger.Repository.Entities.BankConnectionEntity", b =>
                {
                    b.HasOne("Ledger.Repository.Entities.BankConnectionEntity", null)
                        .WithMany()
                        .HasForeignKey("SupersededById")
                        .OnDelete(DeleteBehavior.Restrict)
                        .HasConstraintName("fk_bank_connections_bank_connections_superseded_by_id");
                });

            modelBuilder.Entity("Ledger.Repository.Entities.LedgerAccountEntity", b =>
                {
                    b.HasOne("Ledger.Repository.Entities.BankConnectionEntity", null)
                        .WithMany()
                        .HasForeignKey("BankConnectionId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired()
                        .HasConstraintName("fk_accounts_bank_connections_bank_connection_id");
                });

            modelBuilder.Entity("Ledger.Repository.Entities.LedgerTransactionEntity", b =>
                {
                    b.HasOne("Ledger.Repository.Entities.LedgerAccountEntity", null)
                        .WithMany()
                        .HasForeignKey("AccountId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired()
                        .HasConstraintName("fk_transactions_accounts_account_id");
                });

            modelBuilder.Entity("Ledger.Repository.Entities.SyncRunEntity", b =>
                {
                    b.HasOne("Ledger.Repository.Entities.BankConnectionEntity", null)
                        .WithMany()
                        .HasForeignKey("BankConnectionId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired()
                        .HasConstraintName("fk_sync_runs_bank_connections_bank_connection_id");
                });

            modelBuilder.Entity("Ledger.Repository.Entities.TransactionPayloadEntity", b =>
                {
                    b.HasOne("Ledger.Repository.Entities.SyncRunEntity", null)
                        .WithMany()
                        .HasForeignKey("SyncRunId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired()
                        .HasConstraintName("fk_transaction_payloads_sync_runs_sync_run_id");

                    b.HasOne("Ledger.Repository.Entities.LedgerTransactionEntity", null)
                        .WithMany()
                        .HasForeignKey("TransactionId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired()
                        .HasConstraintName("fk_transaction_payloads_transactions_transaction_id");
                });

            modelBuilder.Entity("Ledger.Repository.Entities.TransactionRefEntity", b =>
                {
                    b.HasOne("Ledger.Repository.Entities.LedgerAccountEntity", null)
                        .WithMany()
                        .HasForeignKey("AccountId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired()
                        .HasConstraintName("fk_transaction_refs_accounts_account_id");

                    b.HasOne("Ledger.Repository.Entities.LedgerTransactionEntity", null)
                        .WithMany()
                        .HasForeignKey("TransactionId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired()
                        .HasConstraintName("fk_transaction_refs_transactions_transaction_id");
                });
#pragma warning restore 612, 618
        }
    }
}
