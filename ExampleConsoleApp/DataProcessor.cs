using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JobDispatcherNET;

namespace ExampleConsoleApp;

// DataProcessor.cs - Sample processor for real-world usage
public class DataProcessor : AsyncExecutable
{
    private readonly Dictionary<string, int> _processedItems = new();
    private readonly object _lock = new();

    public void ProcessItem(string itemId, int priority)
    {
        Console.WriteLine($"[Thread {Environment.CurrentManagedThreadId}] Processing item {itemId} with priority {priority}");

        // Simulate processing work
        Thread.Sleep(100 * (1 + Random.Shared.Next(5)));

        lock (_lock)
        {
            if (_processedItems.TryGetValue(itemId, out var count))
            {
                _processedItems[itemId] = count + 1;
            }
            else
            {
                _processedItems[itemId] = 1;
            }
        }

        // Schedule follow-up processing based on priority
        if (priority > 5)
        {
            DoAsync(() => HighPriorityFollowUp(itemId));
        }
        else if (priority > 2)
        {
            DoAsyncAfter(TimeSpan.FromMilliseconds(500), () => MediumPriorityFollowUp(itemId));
        }
    }

    private void HighPriorityFollowUp(string itemId)
    {
        Console.WriteLine($"[Thread {Environment.CurrentManagedThreadId}] High priority follow-up for {itemId}");
        // Processing logic here
    }

    private void MediumPriorityFollowUp(string itemId)
    {
        Console.WriteLine($"[Thread {Environment.CurrentManagedThreadId}] Medium priority follow-up for {itemId}");
        // Processing logic here
    }

    public Dictionary<string, int> GetProcessingStats()
    {
        lock (_lock)
        {
            return new Dictionary<string, int>(_processedItems);
        }
    }
}
