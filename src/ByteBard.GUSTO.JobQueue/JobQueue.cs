namespace ByteBard.GUSTO;

using System.Linq.Expressions;
using Newtonsoft.Json;

public class JobQueue<TStorageRecord> where TStorageRecord : IJobStorageRecord, new()
{
    private static JsonSerializerSettings _settings = new()
    {
        TypeNameHandling = TypeNameHandling.All,
        Formatting = Formatting.None
    };

    private readonly IJobStorageProvider<TStorageRecord> _storageProvider;

    public JobQueue(IJobStorageProvider<TStorageRecord> storageProvider)
    {
        _storageProvider = storageProvider;
    }

    public async Task<Guid> EnqueueAsync<T>(Expression<Func<T, Task>> methodCall, DateTime? executeAfter = null, CancellationToken cancellationToken = default)
    {
        var methodCallExpression = (MethodCallExpression)methodCall.Body;
        var method = methodCallExpression.Method;
        var arguments = methodCallExpression.Arguments.Select(arg => Expression.Lambda(arg).Compile().DynamicInvoke()).ToArray();
        var record = new TStorageRecord
        {
            TrackingId = Guid.NewGuid(),
            CreatedOn = DateTime.UtcNow,
            ExecuteAfter = executeAfter ?? DateTime.UtcNow,
            JobType = typeof(T).AssemblyQualifiedName,
            MethodName = method.Name,
            ArgumentsJson = JsonConvert.SerializeObject(arguments, _settings),
            IsComplete = false
        };
        

        await _storageProvider.StoreJobAsync(record, cancellationToken);
        return record.TrackingId;
    }
}
