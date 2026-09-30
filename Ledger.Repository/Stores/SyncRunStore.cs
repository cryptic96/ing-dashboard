using Ledger.Domain.Ingestion;
using Ledger.Repository.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Repository.Stores;

/// <summary>Records sync runs; a partial unique index makes a second unfinished run for one connection impossible.</summary>
public class SyncRunStore(LedgerDbContext dbContext) : ISyncRunStore
{
    private const int MaxProviderErrorLength = 64;

    /// <inheritdoc />
    public async Task<Guid> StartAsync(
        Guid connectionId,
        SyncTrigger trigger,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var run = new SyncRunEntity
        {
            Id = Guid.CreateVersion7(),
            BankConnectionId = connectionId,
            Trigger = trigger,
            StartedAt = startedAt
        };

        dbContext.SyncRuns.Add(run);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(run).State = EntityState.Detached;
            throw new SyncAlreadyRunningException("A sync run for this connection is already in progress.");
        }

        return run.Id;
    }

    /// <inheritdoc />
    public async Task FinishAsync(Guid runId, SyncRunCompletion completion, CancellationToken cancellationToken)
    {
        var providerError = completion.ProviderError is { Length: > MaxProviderErrorLength }
            ? completion.ProviderError[..MaxProviderErrorLength]
            : completion.ProviderError;

        await dbContext.SyncRuns
            .Where(run => run.Id == runId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.FinishedAt, (DateTimeOffset?)completion.FinishedAt)
                    .SetProperty(run => run.Outcome, (SyncOutcome?)completion.Outcome)
                    .SetProperty(run => run.ProviderError, providerError)
                    .SetProperty(run => run.CallsMade, completion.CallsMade)
                    .SetProperty(run => run.Inserted, completion.Inserted)
                    .SetProperty(run => run.Updated, completion.Updated)
                    .SetProperty(run => run.Dropped, completion.Dropped)
                    .SetProperty(run => run.Flagged, completion.Flagged),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncRunSummary>> ListRunsSinceAsync(
        Guid connectionId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        return await dbContext.SyncRuns
            .AsNoTracking()
            .Where(run => run.BankConnectionId == connectionId && run.StartedAt >= since)
            .OrderBy(run => run.StartedAt)
            .Select(run => new SyncRunSummary(run.Trigger, run.StartedAt, run.FinishedAt, run.Outcome))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> HasUnfinishedRunAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        return dbContext.SyncRuns.AnyAsync(
            run => run.BankConnectionId == connectionId && run.FinishedAt == null,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> AbandonUnfinishedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await dbContext.SyncRuns
            .Where(run => run.FinishedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.FinishedAt, (DateTimeOffset?)now)
                    .SetProperty(run => run.Outcome, (SyncOutcome?)SyncOutcome.Abandoned),
                cancellationToken);
    }
}
