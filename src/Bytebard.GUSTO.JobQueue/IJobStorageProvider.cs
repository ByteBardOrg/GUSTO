namespace Bytebard.GUSTO;

/// <summary>
/// Defines methods for persisting and managing job records in a storage provider, typically a NoSQL database.
/// </summary>
/// <typeparam name="TStorageRecord">The type of the storage record that implements <see cref="IJobStorageRecord"/>.</typeparam>
public interface IJobStorageProvider<TStorageRecord> where TStorageRecord : IJobStorageRecord
{
    /// <summary>
    /// Persists the provided job storage record to the underlying data store.
    /// </summary>
    /// <param name="jobStorageRecord">
    /// The job storage record containing the command object and related metadata to be saved.
    /// </param>
    /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
    Task StoreJobAsync(TStorageRecord jobStorageRecord, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a batch of job storage records that match the given search criteria.
    /// </summary>
    /// <param name="parameters">
    /// Search parameters to use when querying for the next batch of jobs.
    /// </param>
    /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
    Task<IEnumerable<TStorageRecord>> GetBatchAsync(JobSearchParams<TStorageRecord> parameters, CancellationToken cancellationToken);

    /// <summary>
    /// Mark the specified job as complete. This can involve either updating the record's
    /// <see cref="IJobStorageRecord.IsComplete"/> property to true or replacing the record in the store.
    /// </summary>
    /// <param name="jobStorageRecord">The job storage record to mark as complete.</param>
    /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
    Task MarkJobAsCompleteAsync(TStorageRecord jobStorageRecord, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels a job by either deleting the associated record from storage or marking it as complete using its tracking ID.
    /// </summary>
    /// <param name="trackingId">
    /// The unique tracking ID of the job to cancel. Typically found in <see cref="IJobStorageRecord.TrackingId"/>.
    /// </param>
    /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
    Task CancelJobAsync(Guid trackingId, CancellationToken cancellationToken);

    /// <summary>
    /// Called when the job's associated command handler throws an exception during execution.
    /// This method allows failed jobs to be rescheduled or handled differently to prevent retry loops.
    /// </summary>
    /// <param name="jobStorageRecord">The job storage record that failed to execute.</param>
    /// <param name="exception">The exception thrown by the command handler.</param>
    /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
    /// <remarks>
    /// When a job fails, it is retried immediately. However, repeated failures may block queue progress.
    /// To mitigate this, you can reschedule the failed job by updating its
    /// <see cref="IJobStorageRecord.ExecuteAfter"/> property to a future time.
    /// </remarks>
    Task OnHandlerExecutionFailureAsync(TStorageRecord jobStorageRecord, Exception exception, CancellationToken cancellationToken);
}
