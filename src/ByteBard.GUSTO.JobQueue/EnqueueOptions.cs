namespace ByteBard.GUSTO;

using System.Diagnostics;

/// <summary>
/// Controls scheduling and trace parenting when a job is enqueued.
/// </summary>
public sealed class EnqueueOptions
{
    /// <summary>The earliest UTC time at which the job may execute.</summary>
    public DateTime? ExecuteAfter { get; set; }

    /// <summary>
    /// An explicit parent for the enqueue activity. When omitted, the current activity is used.
    /// </summary>
    public ActivityContext? ParentContext { get; set; }
}
