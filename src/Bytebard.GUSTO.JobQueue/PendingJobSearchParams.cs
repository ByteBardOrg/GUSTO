namespace Bytebard.GUSTO;

using System.Linq.Expressions;

public struct JobSearchParams<TStorageRecord> where TStorageRecord : IJobStorageRecord
{
    public Expression<Func<TStorageRecord, bool>> Match { get; internal set; }

    /// <summary>
    /// the number of records to fetch
    /// </summary>
    public int Limit { get; internal set; }

    /// <summary>
    /// cancellation token
    /// </summary>
    public CancellationToken CancellationToken { get; internal set; }
}