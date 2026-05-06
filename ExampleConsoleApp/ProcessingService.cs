namespace ExampleConsoleApp;

/// <summary>
/// AsyncExecutable의 심화 활용 예제.
/// 다수 아이템을 단일 DataProcessor에 비동기로 디스패치하고,
/// DataProcessor 내부에서 우선순위에 따라 자기 자신에게 후속 작업을 다시 디스패치한다.
/// 모든 작업은 AsyncExecutable의 객체별 직렬 실행 보장 위에서 동작한다.
/// </summary>
public class ProcessingService : IAsyncDisposable
{
    private readonly DataProcessor _processor = new();
    private readonly List<string> _items = new();
    private readonly int _itemCount;
    private readonly CancellationTokenSource _cts = new();
    private Task _processingTask = Task.CompletedTask;

    public ProcessingService()
    {
        _itemCount = Random.Shared.Next(15, 30);
        for (int i = 0; i < _itemCount; i++)
        {
            _items.Add($"Item-{Random.Shared.Next(1000, 9999)}");
        }
    }

    public DataProcessor Processor => _processor;

    public void Start()
    {
        var token = _cts.Token;
        _processingTask = Task.Run(async () =>
        {
            for (int i = 0; i < _itemCount && !token.IsCancellationRequested; i++)
            {
                string item = _items[Random.Shared.Next(_items.Count)];
                int priority = Random.Shared.Next(1, 10);

                _processor.DoAsync(() => _processor.ProcessItem(item, priority));

                try
                {
                    await Task.Delay(Random.Shared.Next(10, 50), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _processingTask; } catch (OperationCanceledException) { }
        await _processor.DisposeAsync();
        _cts.Dispose();
    }
}
