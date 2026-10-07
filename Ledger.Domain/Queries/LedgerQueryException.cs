namespace Ledger.Domain.Queries;

/// <summary>
/// A question that cannot be answered as asked, for example an impossible period or a filter that is too long. The message is
/// fixed text meant for Claude and never repeats what the caller sent.
/// </summary>
public class LedgerQueryException(string message) : Exception(message);
