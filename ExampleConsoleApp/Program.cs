// See https://aka.ms/new-console-template for more information
using System;
using System.Threading.Tasks;
using ExampleConsoleApp;
using JobDispatcherNET;

class Program
{
    private const int TestWorkerThreadCount = 4;

    static async Task Main(string[] args)
    {
        Console.WriteLine("Hello, World!");

        Console.WriteLine("JobDispatcher Demo");
        Console.WriteLine("=================");

        // Basic example - simple async execution
        await BasicExampleAsync();

        // Worker thread example - running tasks in parallel
        await WorkerThreadExampleAsync();

        // Advanced example - real-world data processing
        await AdvancedExampleAsync();

        Console.WriteLine("\nAll examples completed");
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }




    static async Task AdvancedExampleAsync()
    {
        Console.WriteLine("Advanced Example - Data Processing:");

        await using var processingService = new ProcessingService(4);

        Console.WriteLine("Starting data processing with 4 workers...");
        processingService.Start();

        // Run for 5 seconds
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Get processing statistics
        var stats = processingService.Processor.GetProcessingStats();

        Console.WriteLine("\nProcessing Statistics:");
        Console.WriteLine($"Total unique items: {stats.Count}");
        Console.WriteLine($"Total processing operations: {stats.Values.Sum()}");

        foreach (var item in stats.OrderByDescending(x => x.Value))
        {
            Console.WriteLine($"{item.Key}: Processed {item.Value} times");
        }
    }



    static async Task WorkerThreadExampleAsync()
    {
        Console.WriteLine("Worker Thread Example:");

        await using var dispatcher = new JobDispatcher<TestWorkerThread>(4); // 4 worker threads

        // Run worker threads
        var dispatcherTask = Task.Run(async () => await dispatcher.RunWorkerThreadsAsync());

        // Let it run for a while
        Console.WriteLine("Running worker threads for 5 seconds...");
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Stop worker threads
        Console.WriteLine("Stopping worker threads...");
        await dispatcher.DisposeAsync();

        Console.WriteLine("All workers have completed");
    }


    static async Task BasicExampleAsync()
    {
        Console.WriteLine("Basic Example:");

        await using var testObject = new TestObject();

        // Execute methods asynchronously
        testObject.DoAsync(() => testObject.TestFunc0());
        testObject.DoAsync(() => testObject.TestFunc1(5));
        testObject.DoAsync(() => testObject.TestFunc2(25, 10));

        // Scheduled for execution after 500ms
        testObject.DoAsyncAfter(TimeSpan.FromMilliseconds(500), () => testObject.TestFunc1(15));

        // Wait for jobs to complete
        await Task.Delay(1000);

        Console.WriteLine($"Test count: {testObject.GetTestCount()}");
    }
}