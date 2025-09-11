// See https://aka.ms/new-console-template for more information

using ByteBard.GUSTO;
using ByteBard.GUSTO.Examples;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddGusto<JobRecord, InMemoryJobStorageProvider>(context.Configuration);
        services.AddScoped(typeof(TestService));
    })
    .Build();

var jobQueue = (JobQueue<JobRecord>)host.Services.GetService(typeof(JobQueue<JobRecord>));
var testJob = new TestJob(new TestService());
var id = jobQueue.EnqueueAsync<TestJob>(t => t.DoSomethingAsync("test")).GetAwaiter().GetResult();
jobQueue.ContinueWithAsync(id, () => testJob.DoSomethingAsync("test")).GetAwaiter().GetResult();

await host.RunAsync();
