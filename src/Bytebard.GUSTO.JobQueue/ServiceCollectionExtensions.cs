namespace Bytebard.GUSTO;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class JobQueueExtensions
{
    public static IServiceCollection AddJobQueue<TStorageRecord, TStorageProvider>(
        this IServiceCollection services, IConfiguration configuration)
        where TStorageRecord : class, IJobStorageRecord
        where TStorageProvider : class, IJobStorageProvider<TStorageRecord>
    {
        services.Configure<JobQueueConfig>(configuration.GetSection(JobQueueConfig.ConfigurationSection));
        services.AddSingleton(typeof(JobQueue<>));
        services.AddSingleton<IJobStorageProvider<TStorageRecord>, TStorageProvider>();
        services.AddHostedService<JobQueueWorker<TStorageRecord>>();

        return services;
    }
}