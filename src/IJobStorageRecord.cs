namespace Bytebard.GUSTO;

public interface IJobStorageRecord
{
    Guid TrackingId { get; set; }

    DateTime CreatedOn { get; set; }

    DateTime? ExecuteAfter { get; set; }

    DateTime? ExpireOn { get; set; }

    bool IsComplete { get; set; }

    public string JobType { get; set; }

    public string MethodName { get; set; }

    public string ArgumentsJson { get; set; }
}