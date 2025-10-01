namespace ByteBard.GUSTO;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class JobQueueExtensions
{
    public static IServiceCollection AddGusto<TStorageRecord, TStorageProvider>(
        this IServiceCollection services,
        IConfiguration configuration,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TStorageRecord : class, IJobStorageRecord, new()
        where TStorageProvider : class, IJobStorageProvider<TStorageRecord>
    {
        services.Configure<GustoConfig>(configuration.GetSection(GustoConfig.ConfigurationSection));

        services.Add(new ServiceDescriptor(typeof(JobQueue<TStorageRecord>), typeof(JobQueue<TStorageRecord>), lifetime));
        services.Add(new ServiceDescriptor(typeof(IJobStorageProvider<TStorageRecord>), typeof(TStorageProvider), lifetime));

        services.AddHostedService<JobQueueWorker<TStorageRecord>>();

        return services;
    }
}
