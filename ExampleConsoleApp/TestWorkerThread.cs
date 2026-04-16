using JobDispatcherNET;

namespace ExampleConsoleApp;

/// <summary>
/// Sample worker implementation — runs on a dedicated OS thread
/// </summary>
public class TestWorkerThread : IRunnable
{
    private readonly List<TestObject> _testObjects = new();
    private const int TestObjectCount = 10;

    public TestWorkerThread()
    {
        for (int i = 0; i < TestObjectCount; i++)
        {
            _testObjects.Add(new TestObject());
        }
    }

    public bool Run(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        int after = Random.Shared.Next(2000);

        if (after > 1000)
        {
            int index1 = Random.Shared.Next(TestObjectCount);
            int index2 = Random.Shared.Next(TestObjectCount);
            int index3 = Random.Shared.Next(TestObjectCount);
            int index4 = Random.Shared.Next(TestObjectCount);

            _testObjects[index1].DoAsync(() => _testObjects[index1].TestFunc0());
            _testObjects[index2].DoAsync(() => _testObjects[index2].TestFunc2(Random.Shared.Next(100), 2));
            _testObjects[index3].DoAsync(() => _testObjects[index3].TestFunc1(1));

            _testObjects[index4].DoAsyncAfter(TimeSpan.FromMilliseconds(after),
                () => _testObjects[index4].TestFuncForTimer(after));
        }

        if (_testObjects[Random.Shared.Next(TestObjectCount)].GetTestCount() > 5000)
        {
            Console.WriteLine($"Thread {Environment.CurrentManagedThreadId} end by force");
            return false;
        }

        Thread.Sleep(1);
        return true;
    }

    public void Dispose()
    {
        foreach (var testObject in _testObjects)
        {
            testObject.DisposeAsync().AsTask().Wait();
        }
    }
}
