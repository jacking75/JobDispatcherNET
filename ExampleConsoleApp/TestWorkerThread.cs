using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JobDispatcherNET;

namespace ExampleConsoleApp;

// TestWorkerThread.cs - Sample worker implementation
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

    public async ValueTask<bool> RunAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        // Test
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

        // Exit condition
        if (_testObjects[Random.Shared.Next(TestObjectCount)].GetTestCount() > 5000)
        {
            Console.WriteLine($"Thread {Environment.CurrentManagedThreadId} end by force");
            return false;
        }

        await Task.Delay(1, cancellationToken);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var testObject in _testObjects)
        {
            await testObject.DisposeAsync();
        }
    }
}