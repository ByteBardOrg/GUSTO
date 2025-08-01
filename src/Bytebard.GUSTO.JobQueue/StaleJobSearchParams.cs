namespace Bytebard.GUSTO;

using System.Linq.Expressions;

public struct StaleJobSearchParams<TStorageRecord> where TStorageRecord : IJobStorageRecord
{
    public Expression<Func<TStorageRecord, bool>> Match { get; internal set; }
    
    public CancellationToken CancellationToken { get; internal set; }
}