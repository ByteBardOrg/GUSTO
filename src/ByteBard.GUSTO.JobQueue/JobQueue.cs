namespace ByteBard.GUSTO;

using System.Reflection;
using System.Linq.Expressions;
using Newtonsoft.Json;

public class JobQueue<TStorageRecord> where TStorageRecord : IJobStorageRecord, new()
{
    private static readonly JsonSerializerSettings _settings = new()
    {
        TypeNameHandling = TypeNameHandling.All,
        Formatting = Formatting.None
    };

    public IJobStorageProvider<TStorageRecord> StorageProvider { get; }

    public JobQueue(IJobStorageProvider<TStorageRecord> storageProvider)
    {
        StorageProvider = storageProvider;
    }

    public async Task<Guid> EnqueueAsync<T>(Expression<Func<T, Task>> methodCall, DateTime? executeAfter = null, CancellationToken cancellationToken = default)
    {
        var record = ConstructRecordFromExpression(methodCall, executeAfter);
        await StorageProvider.StoreJobAsync(record, cancellationToken);
        return record.TrackingId;
    }

    public async Task<Guid> EnqueueAsync(Expression<Func<Task>> methodCall, DateTime? executeAfter = null, CancellationToken cancellationToken = default)
    {
        var record = ConstructRecordFromExpression(methodCall, executeAfter);

        await StorageProvider.StoreJobAsync(record, cancellationToken);
        return record.TrackingId;
    }
    public TStorageRecord ConstructRecordFromExpression<T>(Expression<Func<T, Task>> methodCall, DateTime? executeAfter) => ConstructRecordFromExpression(methodCall.Body, executeAfter);
    public TStorageRecord ConstructRecordFromExpression(Expression<Func<Task>> methodCall, DateTime? executeAfter) => ConstructRecordFromExpression(methodCall.Body, executeAfter);
    
    public TStorageRecord ConstructRecordFromExpression(Expression expression, DateTime? executeAfter)
    {
        var methodCallExpression = (MethodCallExpression)expression;
        var method = methodCallExpression.Method;
        var arguments = methodCallExpression.Arguments.Select(arg => Expression.Lambda(arg).Compile().DynamicInvoke()).ToArray();
    
        Type targetType = methodCallExpression.Object switch
        {
            ConstantExpression c when c.Value != null => c.Value.GetType(),
            MemberExpression m => Expression.Lambda(m).Compile().DynamicInvoke()?.GetType() ?? method.DeclaringType,
            _ => method.DeclaringType
        };

        var record = new TStorageRecord
        {
            TrackingId = Guid.NewGuid(),
            CreatedOn = DateTime.UtcNow,
            ExecuteAfter = executeAfter ?? DateTime.UtcNow,
            JobType = targetType?.AssemblyQualifiedName,
            MethodName = method.Name,
            ArgumentsJson = JsonConvert.SerializeObject(arguments, _settings),
            IsComplete = false
        };
        
        return record;
    }
}
