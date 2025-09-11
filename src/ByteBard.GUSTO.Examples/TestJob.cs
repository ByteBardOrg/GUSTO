public class TestJob
{
    private readonly TestService _service;

    public TestJob(TestService service)
    {
        _service = service;
    }
    public virtual Task DoSomethingAsync(string input)
    {
        return Task.CompletedTask;
    }
}


public class TestService
{
    public string GetSomething() => Random.Shared.Next().ToString();
}
