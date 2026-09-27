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

        SnakeCaseNaming.Apply(modelBuilder);
    }
}
