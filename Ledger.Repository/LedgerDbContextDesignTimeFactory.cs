using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ledger.Repository;

/// <summary>Builds a design-time <see cref="LedgerDbContext"/> for EF Core tooling, connecting as the migrator role over the Unix socket.</summary>
public class LedgerDbContextDesignTimeFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    /// <inheritdoc />
    public LedgerDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LedgerDbContext>();
        optionsBuilder.UseNpgsql("Host=/var/run/postgresql;Database=ledger;Username=ledger_migrator");

        return new LedgerDbContext(optionsBuilder.Options);
    }
}
