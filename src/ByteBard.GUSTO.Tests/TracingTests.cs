using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using ByteBard.GUSTO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public class TracingTests
{
    public sealed class Record : IJobStorageRecord
    {
        public Guid TrackingId { get; set; }
        public DateTime CreatedOn { get; set; }
        public DateTime? ExecuteAfter { get; set; }
        public DateTime? ExpireOn { get; set; }
        public bool IsComplete { get; set; }
        public string JobType { get; set; } = null!;
        public string MethodName { get; set; } = null!;
        public string ArgumentsJson { get; set; } = null!;
    }

    public sealed class Handler
    {
        internal static readonly ActivitySource Source = new("Gusto.Tests.Handler");
        internal static SemaphoreSlim Signal { get; set; } = new(0);

        public Task Run(string value)
        {
            using var activity = Source.StartActivity("HandlerChild");
            Signal.Release();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task EnqueueSerializesVersionedEnvelopeAndExplicitProducerParent()
    {
        Record? record = null;
        var storage = Substitute.For<IJobStorageProvider<Record>>();
        storage.When(x => x.StoreJobAsync(Arg.Any<Record>(), Arg.Any<CancellationToken>()))
            .Do(call => record = call.Arg<Record>());
        var queue = new JobQueue<Record>(storage);
        var parent = ActivityContext.Parse(
            "00-11111111111111111111111111111111-2222222222222222-01", "vendor=value");
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(source => source.Name == GustoTelemetry.ActivitySourceName, stopped.Add);

        await queue.EnqueueAsync(new EnqueueOptions { ParentContext = parent },
            (Handler handler) => handler.Run("value"));

        var envelope = JObject.Parse(record!.ArgumentsJson);
        Assert.Equal(1, envelope.Value<int>("version"));
        Assert.Equal("value", envelope["arguments"]!.ToObject<string[]>()![0]);
        var enqueue = Assert.Single(stopped, x => x.OperationName == "EnqueueJob");
        Assert.Equal(ActivityKind.Producer, enqueue.Kind);
        Assert.Equal(parent.TraceId, enqueue.TraceId);
        Assert.Equal(parent.SpanId, enqueue.ParentSpanId);
        Assert.Equal("vendor=value", envelope.Value<string>("tracestate"));
        var persisted = ActivityContext.Parse(envelope.Value<string>("traceparent")!, envelope.Value<string>("tracestate"));
        Assert.Equal(enqueue.Context, persisted);
    }

    [Fact]
    public async Task EnqueueUsesAmbientParentAndPreservesItWithoutListener()
    {
        Record? record = null;
        var storage = Substitute.For<IJobStorageProvider<Record>>();
        storage.When(x => x.StoreJobAsync(Arg.Any<Record>(), Arg.Any<CancellationToken>()))
            .Do(call => record = call.Arg<Record>());
        var queue = new JobQueue<Record>(storage);
        using var ambient = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();

        await queue.EnqueueAsync<Handler>(handler => handler.Run("value"));

        var persisted = ActivityContext.Parse(
            JObject.Parse(record!.ArgumentsJson).Value<string>("traceparent")!, null);
        Assert.Equal(ambient.Context, persisted);
    }

    [Fact]
    public void DirectConstructionPersistsAmbientContextWithoutCreatingProducerActivityOrStoring()
    {
        var storage = Substitute.For<IJobStorageProvider<Record>>();
        var queue = new JobQueue<Record>(storage);
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(source => source.Name == GustoTelemetry.ActivitySourceName, stopped.Add);
        using var ambient = new Activity("request")
            .SetParentId("00-11111111111111111111111111111111-2222222222222222-01")
            .Start();
        ambient.TraceStateString = "ambient=value";

        var record = queue.ConstructRecordFromExpression<Handler>(
            handler => handler.Run("ambient"), null);

        var envelope = JObject.Parse(record.ArgumentsJson);
        var persisted = ActivityContext.Parse(
            envelope.Value<string>("traceparent")!, envelope.Value<string>("tracestate"));
        Assert.Equal(ambient.Context, persisted);
        Assert.Equal("ambient=value", persisted.TraceState);
        Assert.DoesNotContain(stopped, activity => activity.OperationName == "EnqueueJob");
        storage.DidNotReceiveWithAnyArgs().StoreJobAsync(default!, default);
    }

    [Fact]
    public void DirectConstructionExplicitContextOverridesAmbientAndPreservesTraceState()
    {
        var storage = Substitute.For<IJobStorageProvider<Record>>();
        var queue = new JobQueue<Record>(storage);
        using var ambient = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var explicitContext = ActivityContext.Parse(
            "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01", "explicit=value");

        var record = queue.ConstructRecordFromExpression<Handler>(
            handler => handler.Run("explicit"), null, explicitContext);

        var envelope = JObject.Parse(record.ArgumentsJson);
        var persisted = ActivityContext.Parse(
            envelope.Value<string>("traceparent")!, envelope.Value<string>("tracestate"));
        Assert.Equal(explicitContext, persisted);
        Assert.NotEqual(ambient.Context, persisted);
        Assert.Equal("explicit=value", envelope.Value<string>("tracestate"));
    }

    [Fact]
    public void DirectConstructionWithoutAmbientContextPersistsNoContext()
    {
        Assert.Null(Activity.Current);
        var queue = new JobQueue<Record>(Substitute.For<IJobStorageProvider<Record>>());
        var handler = new Handler();

        var record = queue.ConstructRecordFromExpression(
            () => handler.Run("none"), null, (ActivityContext?)null);

        var envelope = JObject.Parse(record.ArgumentsJson);
        Assert.Null(envelope["traceparent"]);
        Assert.Null(envelope["tracestate"]);
    }

    [Fact]
    public void DirectConstructionInvalidExplicitContextDoesNotFallBackToAmbient()
    {
        var queue = new JobQueue<Record>(Substitute.For<IJobStorageProvider<Record>>());
        using var ambient = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        Expression<Func<Task>> methodCall = () => new Handler().Run("invalid");

        var record = queue.ConstructRecordFromExpression(
            methodCall.Body, null, default(ActivityContext));

        var envelope = JObject.Parse(record.ArgumentsJson);
        Assert.Null(envelope["traceparent"]);
        Assert.Null(envelope["tracestate"]);
    }

    [Fact]
    public async Task WorkerParentsConsumerToPersistedContextAndTraceState()
    {
        var parent = ActivityContext.Parse(
            "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01", "vendor=value");
        var job = CreateJob(Envelope("ok", Format(parent), parent.TraceState));
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(source => source.Name == GustoTelemetry.ActivitySourceName, stopped.Add);

        await RunWorkerAsync(new[] { job });

        var execute = Assert.Single(stopped, x => x.OperationName == "ExecuteJob");
        Assert.Equal(ActivityKind.Consumer, execute.Kind);
        Assert.Equal(parent.TraceId, execute.TraceId);
        Assert.Equal(parent.SpanId, execute.ParentSpanId);
        Assert.True(execute.HasRemoteParent);
        Assert.Equal("vendor=value", execute.TraceStateString);
        Assert.Empty(execute.Links);
        Assert.Equal(ActivityStatusCode.Ok, execute.Status);
    }

    [Fact]
    public async Task EnqueueAndExecuteCorrelateThroughPersistedParent()
    {
        Record? job = null;
        var enqueueStorage = Substitute.For<IJobStorageProvider<Record>>();
        enqueueStorage.When(x => x.StoreJobAsync(Arg.Any<Record>(), Arg.Any<CancellationToken>()))
            .Do(call => job = call.Arg<Record>());
        var activities = new ConcurrentBag<Activity>();
        using var listener = Listen(source => source.Name == GustoTelemetry.ActivitySourceName, activities.Add);
        using var request = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();

        await new JobQueue<Record>(enqueueStorage)
            .EnqueueAsync<Handler>(handler => handler.Run("chain"));
        await RunWorkerAsync(new[] { job! });

        var producer = Assert.Single(activities, x => x.OperationName == "EnqueueJob");
        var consumer = Assert.Single(activities, x => x.OperationName == "ExecuteJob");
        Assert.Equal(producer.TraceId, consumer.TraceId);
        Assert.Equal(producer.SpanId, consumer.ParentSpanId);
        Assert.True(consumer.HasRemoteParent);
        Assert.Empty(consumer.Links);
    }

    [Fact]
    public async Task RetryAttemptsShareTraceParentedToPersistedContext()
    {
        var enqueueContext = ActivityContext.Parse(
            "00-cccccccccccccccccccccccccccccccc-dddddddddddddddd-01", "retry=original");
        var job = CreateJob(Envelope("retry", Format(enqueueContext), enqueueContext.TraceState));
        var activities = new ConcurrentBag<Activity>();
        using var listener = Listen(source => source.Name == GustoTelemetry.ActivitySourceName, activities.Add);

        await RunWorkerAsync(new[] { job });
        await RunWorkerAsync(new[] { job });

        var attempts = activities.Where(x => x.OperationName == "ExecuteJob").ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.All(attempts, attempt =>
        {
            Assert.Equal(enqueueContext.TraceId, attempt.TraceId);
            Assert.Equal(enqueueContext.SpanId, attempt.ParentSpanId);
            Assert.Equal("retry=original", attempt.TraceStateString);
            Assert.True(attempt.HasRemoteParent);
            Assert.Empty(attempt.Links);
        });
    }

    [Fact]
    public async Task MissingAndInvalidContextsCreateIndependentRootsOutsideBatch()
    {
        var jobs = new[]
        {
            CreateJob(Envelope("one")),
            CreateJob(Envelope("two", "not-a-traceparent"))
        };
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(source => source.Name == GustoTelemetry.ActivitySourceName, stopped.Add);

        await RunWorkerAsync(jobs, concurrency: 2);

        var batch = Assert.Single(stopped, x => x.OperationName == "ProcessBatch");
        var executions = stopped.Where(x => x.OperationName == "ExecuteJob").ToArray();
        Assert.Equal(2, executions.Length);
        Assert.All(executions, x => Assert.Equal(default, x.ParentSpanId));
        Assert.All(executions, x => Assert.Empty(x.Links));
        Assert.All(executions, x => Assert.NotEqual(batch.TraceId, x.TraceId));
        Assert.NotEqual(executions[0].TraceId, executions[1].TraceId);
    }

    [Fact]
    public async Task HandlerChildDoesNotInheritUnlistenedProcessBatch()
    {
        Handler.Signal = new SemaphoreSlim(0);
        var children = new ConcurrentBag<Activity>();
        using var listener = Listen(source => source.Name == Handler.Source.Name, children.Add);

        await RunWorkerAsync(new[] { CreateJob(Envelope("ok")) });

        var child = Assert.Single(children);
        Assert.Equal(default, child.ParentSpanId);
    }

    [Fact]
    public async Task UnsupportedEnvelopeVersionUsesFailureHandling()
    {
        var job = CreateJob("{\"version\":99,\"arguments\":[]}");
        Exception? failure = null;
        var storage = Substitute.For<IJobStorageProvider<Record>>();
        storage.GetBatchAsync(Arg.Any<JobSearchParams<Record>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { job }, Array.Empty<Record>());
        storage.When(x => x.OnHandlerExecutionFailureAsync(job, Arg.Any<Exception>(), Arg.Any<CancellationToken>()))
            .Do(call => failure = call.ArgAt<Exception>(1));

        await RunWorkerAsync(new[] { job }, storage);

        var unsupported = Assert.IsType<NotSupportedException>(failure);
        Assert.Contains("Unsupported GUSTO job payload version '99'", unsupported.Message);
    }

    private static Record CreateJob(string argumentsJson) => new()
    {
        TrackingId = Guid.NewGuid(),
        JobType = typeof(Handler).AssemblyQualifiedName!,
        MethodName = nameof(Handler.Run),
        ArgumentsJson = argumentsJson,
        ExecuteAfter = DateTime.UtcNow
    };

    private static string Envelope(string value, string? traceParent = null, string? traceState = null)
    {
        var valueObject = new JObject { ["version"] = 1, ["arguments"] = new JArray(value) };
        if (traceParent != null) valueObject["traceparent"] = traceParent;
        if (traceState != null) valueObject["tracestate"] = traceState;
        return valueObject.ToString(Formatting.None);
    }

    private static string Format(ActivityContext context)
        => $"00-{context.TraceId}-{context.SpanId}-{(byte)context.TraceFlags:x2}";

    private static ActivityListener Listen(Func<ActivitySource, bool> predicate, Action<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = predicate,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static async Task RunWorkerAsync(
        IReadOnlyCollection<Record> jobs,
        IJobStorageProvider<Record>? suppliedStorage = null,
        int concurrency = 1)
    {
        var storage = suppliedStorage ?? Substitute.For<IJobStorageProvider<Record>>();
        if (suppliedStorage == null)
        {
            storage.GetBatchAsync(Arg.Any<JobSearchParams<Record>>(), Arg.Any<CancellationToken>())
                .Returns(jobs, Array.Empty<Record>());
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => storage);
        await using var provider = services.BuildServiceProvider();
        var worker = new JobQueueWorker<Record>(
            provider,
            Options.Create(new GustoConfig
            {
                BatchSize = jobs.Count,
                Concurrency = concurrency,
                PollInterval = TimeSpan.FromMilliseconds(10),
                JobExecutionTimeout = TimeSpan.FromSeconds(2)
            }),
            Substitute.For<ILogger<JobQueueWorker<Record>>>());
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        JobQueueWorker<Record>.BatchCompletedBarrier = completed;
        await worker.StartAsync(CancellationToken.None);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);
    }
}
