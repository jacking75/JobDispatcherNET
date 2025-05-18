using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JobDispatcherNET;

namespace ExampleConsoleApp;

// ProcessingService.cs - Service that uses the dispatcher
public class ProcessingService : IAsyncDisposable
{
    private readonly DataProcessor _processor = new();
    private readonly List<string> _items = new();
    private readonly int _maxItems;
    private readonly JobDispatcher<ProcessingWorker> _dispatcher;
    private Task _processingTask = Task.CompletedTask;

    public ProcessingService(int workerCount)
    {
        _dispatcher = new JobDispatcher<ProcessingWorker>(workerCount);

        // Generate test items
        _maxItems = Random.Shared.Next(15, 30);
        for (int i = 0; i < _maxItems; i++)
        {
            _items.Add($"Item-{Random.Shared.Next(1000, 9999)}");
        }
    }

    public DataProcessor Processor => _processor;

    public void Start()
    {
        // Submit items for processing
        _processingTask = Task.Run(async () =>
        {
            for (int i = 0; i < _maxItems; i++)
            {
                string item = _items[Random.Shared.Next(_items.Count)];
                int priority = Random.Shared.Next(1, 10);

                _processor.DoAsync(() => _processor.ProcessItem(item, priority));

                await Task.Delay(Random.Shared.Next(10, 50));
            }
        });

        // Start worker threads
        _ = _dispatcher.RunWorkerThreadsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _dispatcher.DisposeAsync();
        await _processingTask;
        await _processor.DisposeAsync();
    }
}