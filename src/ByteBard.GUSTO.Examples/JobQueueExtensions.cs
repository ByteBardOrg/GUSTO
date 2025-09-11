using System.Linq.Expressions;

namespace ByteBard.GUSTO.Examples;

public static class JobQueueExtensions
{
    public static async Task<Guid> ContinueWithAsync(this JobQueue<JobRecord> queue, Guid parentJobId, Expression<Func<Task>> methodCall, CancellationToken cancellationToken = default)
    {
        var record = queue.ConstructRecordFromExpression(methodCall, null);
        record.Status = JobStatus.WaitingForParent;
        record.ParentJobId = parentJobId;

        await queue.StorageProvider.StoreJobAsync(record, cancellationToken);
        return record.TrackingId;
    }
}
