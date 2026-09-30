using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.Repository.Conventions;
using Ledger.Repository.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Repository;

/// <summary>EF Core context for the ledger database, including the Data Protection key ring and the canary table.</summary>
public class LedgerDbContext(DbContextOptions<LedgerDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>The Data Protection key ring, persisted so it survives a restart or a redeploy.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    /// <summary>The single canary row proving the key ring can still decrypt.</summary>
    public DbSet<DataProtectionCanaryEntity> DataProtectionCanary { get; set; } = null!;

    /// <summary>Named per-client API keys: only a key id and a secret hash are stored, never a secret.</summary>
    public DbSet<ApiKeyEntity> ApiKeys { get; set; } = null!;

    /// <summary>Approved consents at a bank data provider, with the session id stored only in protected form.</summary>
    public DbSet<BankConnectionEntity> BankConnections { get; set; } = null!;

    /// <summary>Bank accounts, identified internally by id and across sessions by provider plus identification hash.</summary>
    public DbSet<LedgerAccountEntity> Accounts { get; set; } = null!;

    /// <summary>Ledger transactions, keyed on an internal id that never changes.</summary>
    public DbSet<LedgerTransactionEntity> Transactions { get; set; } = null!;

    /// <summary>Provider references, each resolving to exactly one ledger transaction. Append-only.</summary>
    public DbSet<TransactionRefEntity> TransactionRefs { get; set; } = null!;

    /// <summary>Raw provider payloads, one row per distinct observation of a transaction. Append-only.</summary>
    public DbSet<TransactionPayloadEntity> TransactionPayloads { get; set; } = null!;

    /// <summary>One row per sync attempt, with its counts and outcome.</summary>
    public DbSet<SyncRunEntity> SyncRuns { get; set; } = null!;

    /// <summary>Pending bank authorisations, each identified only by the SHA-256 of its one-time state.</summary>
    public DbSet<BankAuthorizationEntity> BankAuthorizations { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<DataProtectionCanaryEntity>(entity =>
        {
            entity.HasKey(canary => canary.Id);
            entity.Property(canary => canary.Id).HasColumnType("smallint").ValueGeneratedNever();
            entity.Property(canary => canary.ProtectedPayload).IsRequired();
            entity.Property(canary => canary.PlaintextSha256).IsRequired();
            entity.Property(canary => canary.CreatedAt).IsRequired();
            entity.ToTable(table => table.HasCheckConstraint("ck_data_protection_canary_id", "id = 1"));
        });

        modelBuilder.Entity<ApiKeyEntity>(entity =>
        {
            entity.HasKey(key => key.Id);
            entity.Property(key => key.Name).HasMaxLength(32).IsRequired();
            entity.Property(key => key.KeyId).HasMaxLength(16).IsRequired();
            entity.Property(key => key.SecretSha256).IsRequired();
            entity.Property(key => key.CreatedAt).IsRequired();
            entity.HasIndex(key => key.KeyId).IsUnique();
            entity.HasIndex(key => key.Name).IsUnique().HasFilter("revoked_at IS NULL");
        });

        ConfigureBankConnections(modelBuilder);
        ConfigureAccounts(modelBuilder);
        ConfigureTransactions(modelBuilder);
        ConfigureTransactionRefs(modelBuilder);
        ConfigureTransactionPayloads(modelBuilder);
        ConfigureSyncRuns(modelBuilder);
        ConfigureBankAuthorizations(modelBuilder);

        SnakeCaseNaming.Apply(modelBuilder);
    }

    private static void ConfigureBankConnections(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BankConnectionEntity>(entity =>
        {
            entity.HasKey(connection => connection.Id);
            entity.Property(connection => connection.ConnectionKey).HasMaxLength(16).IsRequired();
            entity.Property(connection => connection.Provider).HasMaxLength(32).IsRequired();
            entity.Property(connection => connection.AspspName).HasMaxLength(128).IsRequired();
            entity.Property(connection => connection.AspspCountry).HasMaxLength(2).IsRequired();
            entity.Property(connection => connection.Status).HasMaxLength(32).IsRequired();
            entity.Property(connection => connection.SessionIdProtected).IsRequired();
            entity.HasIndex(connection => connection.ConnectionKey).IsUnique();
            entity.HasOne<BankConnectionEntity>()
                .WithMany()
                .HasForeignKey(connection => connection.SupersededById)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_bank_connections_status",
                "status IN ('active', 'provider_expired', 'revoked', 'superseded')"));
        });
    }

    private static void ConfigureAccounts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LedgerAccountEntity>(entity =>
        {
            entity.HasKey(account => account.Id);
            entity.Property(account => account.AccountKey).HasMaxLength(16).IsRequired();
            entity.Property(account => account.Provider).HasMaxLength(32).IsRequired();
            entity.Property(account => account.IdentificationHash).IsRequired();
            entity.Property(account => account.Kind).HasConversion(new SnakeCaseEnumConverter<AccountKind>()).IsRequired();
            entity.Property(account => account.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            entity.Property(account => account.SyncEnabled).HasDefaultValue(false);
            entity.HasIndex(account => account.AccountKey).IsUnique();
            entity.HasIndex(account => new { account.Provider, account.IdentificationHash }).IsUnique();
            entity.HasOne<BankConnectionEntity>()
                .WithMany()
                .HasForeignKey(account => account.BankConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_accounts_kind",
                $"kind IN ({EnumText.CheckList<AccountKind>()})"));
        });
    }

    private static void ConfigureTransactions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LedgerTransactionEntity>(entity =>
        {
            entity.HasKey(transaction => transaction.Id);
            entity.Property(transaction => transaction.Status)
                .HasConversion(new SnakeCaseEnumConverter<LedgerTransactionStatus>())
                .IsRequired();
            entity.Property(transaction => transaction.BookingDate).HasColumnType("date");
            entity.Property(transaction => transaction.ValueDate).HasColumnType("date");
            entity.Property(transaction => transaction.TransactionDate).HasColumnType("date");
            entity.Property(transaction => transaction.Amount).HasColumnType("numeric(19,4)");
            entity.Property(transaction => transaction.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            entity.Property(transaction => transaction.MatchFlag).HasConversion(new SnakeCaseEnumConverter<MatchFlag>());
            entity.HasIndex(transaction => new { transaction.AccountId, transaction.BookingDate });
            entity.HasIndex(transaction => new { transaction.AccountId, transaction.Status });
            entity.HasOne<LedgerAccountEntity>()
                .WithMany()
                .HasForeignKey(transaction => transaction.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "ck_transactions_status",
                    $"status IN ({EnumText.CheckList<LedgerTransactionStatus>()})");
                table.HasCheckConstraint(
                    "ck_transactions_match_flag",
                    $"match_flag IS NULL OR match_flag IN ({EnumText.CheckList(MatchFlag.None)})");
                table.HasCheckConstraint("ck_transactions_currency", "currency ~ '^[A-Z]{3}$'");
            });
        });
    }

    private static void ConfigureTransactionRefs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TransactionRefEntity>(entity =>
        {
            entity.HasKey(reference => new { reference.AccountId, reference.Ref });
            entity.Property(reference => reference.Ref).IsRequired();
            entity.Property(reference => reference.FirstStatus)
                .HasConversion(new SnakeCaseEnumConverter<LedgerTransactionStatus>())
                .IsRequired();
            entity.HasOne<LedgerAccountEntity>()
                .WithMany()
                .HasForeignKey(reference => reference.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LedgerTransactionEntity>()
                .WithMany()
                .HasForeignKey(reference => reference.TransactionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_transaction_refs_first_status",
                $"first_status IN ({EnumText.CheckList<LedgerTransactionStatus>()})"));
        });
    }

    private static void ConfigureTransactionPayloads(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TransactionPayloadEntity>(entity =>
        {
            entity.HasKey(payload => payload.Id);
            entity.Property(payload => payload.Payload).HasColumnType("jsonb").IsRequired();
            entity.Property(payload => payload.PayloadSha256).IsRequired();
            entity.HasIndex(payload => new { payload.TransactionId, payload.ObservedAt });
            entity.HasOne<LedgerTransactionEntity>()
                .WithMany()
                .HasForeignKey(payload => payload.TransactionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SyncRunEntity>()
                .WithMany()
                .HasForeignKey(payload => payload.SyncRunId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureSyncRuns(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SyncRunEntity>(entity =>
        {
            entity.HasKey(run => run.Id);
            entity.Property(run => run.Trigger).HasConversion(new SnakeCaseEnumConverter<SyncTrigger>()).IsRequired();
            entity.Property(run => run.Outcome).HasConversion(new SnakeCaseEnumConverter<SyncOutcome>());
            entity.Property(run => run.ProviderError).HasMaxLength(64);
            entity.HasIndex(run => run.BankConnectionId)
                .IsUnique()
                .HasDatabaseName("ux_sync_runs_unfinished_per_connection")
                .HasFilter("finished_at IS NULL");
            entity.HasOne<BankConnectionEntity>()
                .WithMany()
                .HasForeignKey(run => run.BankConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "ck_sync_runs_trigger",
                    $"trigger IN ({EnumText.CheckList<SyncTrigger>()})");
                table.HasCheckConstraint(
                    "ck_sync_runs_outcome",
                    $"outcome IS NULL OR outcome IN ({EnumText.CheckList<SyncOutcome>()})");
            });
        });
    }

    private static void ConfigureBankAuthorizations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BankAuthorizationEntity>(entity =>
        {
            entity.HasKey(authorization => authorization.Id);
            entity.Property(authorization => authorization.StateSha256).IsRequired();
            entity.Property(authorization => authorization.Purpose).HasMaxLength(16).IsRequired();
            entity.Property(authorization => authorization.ProviderAuthorizationId).HasMaxLength(256).IsRequired();
            entity.HasIndex(authorization => authorization.StateSha256).IsUnique();
            entity.HasOne<BankConnectionEntity>()
                .WithMany()
                .HasForeignKey(authorization => authorization.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_bank_authorizations_purpose",
                "purpose IN ('link', 'renew')"));
        });
    }
}
