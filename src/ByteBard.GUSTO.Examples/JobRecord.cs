namespace ByteBard.GUSTO.Examples;

public class JobRecord : IJobStorageRecord
{
    public Guid TrackingId { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? ExecuteAfter { get; set; }
    public DateTime? ExpireOn { get; set; }
    public bool IsComplete { get; set; }
    public string JobType { get; set; }
    public string MethodName { get; set; }
    public string ArgumentsJson { get; set; }
    public JobStatus Status { get; set; }
    public Guid ParentJobId { get; set; }
}

public enum JobStatus
{
    Ready,
    WaitingForParent,
}
