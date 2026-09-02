using JobDispatcherNET;

namespace ExampleConsoleApp;


public class TestObject : AsyncExecutable
{
    private int _testCount;

    public void TestFunc0()
    {
        Interlocked.Increment(ref _testCount);
    }

    public void TestFunc1(int b)
    {
        Interlocked.Add(ref _testCount, b);
    }

    public void TestFunc2(double a, int b)
    {
        Interlocked.Add(ref _testCount, b);

        if (a < 50.0)
        {
            DoAsync(() => TestFunc1(b));
        }
    }

    public void TestFuncForTimer(int b)
    {
        if (Random.Shared.Next(2) == 0)
        {
            DoAsyncAfter(TimeSpan.FromSeconds(1), () => TestFuncForTimer(-b));
        }
    }

    public int GetTestCount() => _testCount;
}
