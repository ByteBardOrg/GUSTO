namespace ByteBard.GUSTO;

using System.Reflection;
using System.Linq.Expressions;
using System.Diagnostics;
using Newtonsoft.Json;

public class JobQueue<TStorageRecord> where TStorageRecord : IJobStorageRecord, new()
{
    private static readonly ActivitySource ActivitySource = new(GustoTelemetry.ActivitySourceName);
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
        return await EnqueueAsync(new EnqueueOptions { ExecuteAfter = executeAfter }, methodCall, cancellationToken);
    }

    public async Task<Guid> EnqueueAsync(Expression<Func<Task>> methodCall, DateTime? executeAfter = null, CancellationToken cancellationToken = default)
    {
        return await EnqueueAsync(new EnqueueOptions { ExecuteAfter = executeAfter }, methodCall, cancellationToken);
    }

    public Task<Guid> EnqueueAsync<T>(EnqueueOptions options, Expression<Func<T, Task>> methodCall, CancellationToken cancellationToken = default)
        => EnqueueCoreAsync(methodCall.Body, options ?? throw new ArgumentNullException(nameof(options)), cancellationToken);

    public Task<Guid> EnqueueAsync(EnqueueOptions options, Expression<Func<Task>> methodCall, CancellationToken cancellationToken = default)
        => EnqueueCoreAsync(methodCall.Body, options ?? throw new ArgumentNullException(nameof(options)), cancellationToken);

    private async Task<Guid> EnqueueCoreAsync(Expression expression, EnqueueOptions options, CancellationToken cancellationToken)
    {
        using var activity = options.ParentContext is ActivityContext parentContext
            ? ActivitySource.StartActivity("EnqueueJob", ActivityKind.Producer, parentContext)
            : ActivitySource.StartActivity("EnqueueJob", ActivityKind.Producer);

        try
        {
            var propagationContext = GetPropagationContext(activity, options.ParentContext);
            var record = ConstructRecordFromExpression(expression, options.ExecuteAfter, propagationContext);
            if (activity is { IsAllDataRequested: true })
            {
                activity.SetTag("job.tracking_id", record.TrackingId);
                activity.SetTag("job.type", record.JobType);
                activity.SetTag("job.method", record.MethodName);
            }

            await StorageProvider.StoreJobAsync(record, cancellationToken);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return record.TrackingId;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            throw;
        }
    }
    public TStorageRecord ConstructRecordFromExpression<T>(Expression<Func<T, Task>> methodCall, DateTime? executeAfter) => ConstructRecordFromExpression(methodCall.Body, executeAfter);
    public TStorageRecord ConstructRecordFromExpression(Expression<Func<Task>> methodCall, DateTime? executeAfter) => ConstructRecordFromExpression(methodCall.Body, executeAfter);
    
    public TStorageRecord ConstructRecordFromExpression(Expression expression, DateTime? executeAfter)
        => ConstructRecordFromExpression(expression, executeAfter, null);

    private TStorageRecord ConstructRecordFromExpression(Expression expression, DateTime? executeAfter, ActivityContext? propagationContext)
    {
        var methodCallExpression = (MethodCallExpression)expression;
        var method = methodCallExpression.Method;
        var arguments = methodCallExpression.Arguments.Select(arg => Expression.Lambda(arg).Compile().DynamicInvoke()).ToArray();
        arguments = NormalizeCancellationTokenArguments(method, arguments);
    
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
            ArgumentsJson = JobPayloadSerializer.Serialize(
                arguments,
                propagationContext is ActivityContext context ? $"00-{context.TraceId}-{context.SpanId}-{(byte)context.TraceFlags:x2}" : null,
                propagationContext?.TraceState,
                _settings),
            IsComplete = false
        };
        
        return record;
    }

    private static ActivityContext? GetPropagationContext(Activity? producerActivity, ActivityContext? explicitParent)
    {
        if (producerActivity is { IdFormat: ActivityIdFormat.W3C } && IsValid(producerActivity.Context))
        {
            return producerActivity.Context;
        }

        if (explicitParent is ActivityContext parent)
        {
            return IsValid(parent) ? parent : null;
        }

        var ambient = Activity.Current;
        return ambient is { IdFormat: ActivityIdFormat.W3C } && IsValid(ambient.Context)
            ? ambient.Context
            : null;
    }

    private static bool IsValid(ActivityContext context)
        => context.TraceId != default && context.SpanId != default;

    private static object?[] NormalizeCancellationTokenArguments(System.Reflection.MethodInfo method, object?[] arguments)
    {
        var parameters = method.GetParameters();
        var length = Math.Min(arguments.Length, parameters.Length);

        if (length == 0)
        {
            return arguments;
        }

        object?[]? normalizedArguments = null;

        for (var i = 0; i < length; i++)
        {
            if (parameters[i].ParameterType != typeof(CancellationToken))
            {
                continue;
            }

            normalizedArguments ??= (object?[])arguments.Clone();
            normalizedArguments[i] = default(CancellationToken);
        }

        return normalizedArguments ?? arguments;
    }
}
