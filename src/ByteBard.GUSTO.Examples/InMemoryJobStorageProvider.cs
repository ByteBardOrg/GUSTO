namespace ByteBard.GUSTO.Examples;

using Newtonsoft.Json;

public class InMemoryJobStorageProvider : IJobStorageProvider<JobRecord>
{
    private readonly List<JobRecord> _jobs = new();
    private readonly object _lock = new();

    public Task StoreJobAsync(JobRecord jobStorageRecord, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _jobs.Add(jobStorageRecord);
        }
        return Task.CompletedTask;
    }

    public Task<IEnumerable<JobRecord>> GetBatchAsync(JobSearchParams<JobRecord> parameters, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var results = _jobs.Where(j => j.Status == JobStatus.Ready).Where(parameters.Match.Compile()).Take(parameters.Limit);
            return Task.FromResult(results);
        }
    }

    public Task MarkJobAsCompleteAsync(JobRecord jobStorageRecord, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.TrackingId == jobStorageRecord.TrackingId);
            if (job != null)
            {
                job.IsComplete = true;

                ProcessContinuations(job.TrackingId, successful: true);
            }
        }
        return Task.CompletedTask;
    }

    public Task CancelJobAsync(Guid trackingId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _jobs.RemoveAll(j => j.TrackingId == trackingId);
        }
        return Task.CompletedTask;
    }

    public Task OnHandlerExecutionFailureAsync(JobRecord jobStorageRecord, Exception exception, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            jobStorageRecord.ExecuteAfter = DateTime.UtcNow.AddMinutes(5);
        }
        return Task.CompletedTask;
    }

    private void ProcessContinuations(Guid completedJobId, bool successful)
    {
        var continuations = _jobs.Where(j => 
            j.Status == JobStatus.WaitingForParent && 
            j.ParentJobId == completedJobId).ToList();

        foreach (var continuation in continuations)
        {
            if (successful)
            {
                continuation.Status = JobStatus.Ready;
                continuation.ExecuteAfter = DateTime.UtcNow;
                
                // don't forget to store the change if moving to persisted storage.
            }
        }
    }
}
