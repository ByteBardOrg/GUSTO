namespace Bytebard.GUSTO;

public class JobQueueConfig
{
    public const string ConfigurationSection = "JobQueue";

    public int BatchSize { get; set; } = 10;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    public int Concurrency { get; set; } = Environment.ProcessorCount;
}