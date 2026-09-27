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

        SnakeCaseNaming.Apply(modelBuilder);
    }
}
