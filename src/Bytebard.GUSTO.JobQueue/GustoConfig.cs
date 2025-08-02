namespace Bytebard.GUSTO;

public class GustoConfig
{
    public const string ConfigurationSection = "Gusto";

    public int BatchSize { get; set; } = 10;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    public int Concurrency { get; set; } = Environment.ProcessorCount;
}
