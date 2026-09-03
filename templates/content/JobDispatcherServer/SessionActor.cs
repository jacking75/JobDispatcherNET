using System.Net.Sockets;
using System.Text;
using JobDispatcherNET;

namespace JobDispatcherServer;

/// <summary>
/// One connected client.
///
/// The receive thread never calls game code directly: it pushes lines into a
/// <see cref="Sequencer{T}"/>, which hands the drain to the worker pool. That keeps one client's
/// messages in arrival order while guaranteeing the handlers run on a worker.
/// </summary>
internal sealed class SessionActor : AsyncExecutable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly WorldActor _world;
    private readonly Sequencer<string> _inbound;
    private Thread? _recvThread;
    private int _closed;

    public long Id { get; }

    public SessionActor(JobSystem system, WorldActor world, TcpClient tcp)
        : base(new JobOptions
        {
            Name = "Session",
            System = system,
            MaxQueueSize = 256,
            Mode = ExecutionMode.Scheduled,
            OnDropped = static (actor, reason) => JobLog.Warn($"[{actor.Name}] refused a job: {reason}"),
        })
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _world = world;
        Id = world.AllocateId();

        // maxPending bounds what one client can queue up: an unbounded per-session queue is how
        // a single slow-to-handle client takes the whole process down.
        _inbound = new Sequencer<string>(system, HandleLine,
            ex => JobLog.Error($"[session {Id}] handler failed", ex),
            maxPending: 256, maxItemsPerDrain: 64);
    }

    public void Start()
    {
        _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = $"Recv-{Id}" };
        _recvThread.Start();
    }

    public void Send(string message) =>
        DoAsync(static t => t.Self.Write(t.Message), (Self: this, Message: message));

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        // Stop() refuses new items but still drains what was already accepted, so the last
        // messages of a session are never silently thrown away.
        _inbound.Stop();
        try { _stream.Dispose(); } catch (ObjectDisposedException) { }
        try { _tcp.Close(); } catch (SocketException) { }
    }

    private void ReceiveLoop()
    {
        var buffer = new byte[4096];
        var pending = new StringBuilder();

        try
        {
            while (true)
            {
                int read;
                try { read = _stream.Read(buffer, 0, buffer.Length); }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }
                if (read == 0) break;

                pending.Append(Encoding.UTF8.GetString(buffer, 0, read));

                int newline;
                while ((newline = IndexOfNewline(pending)) >= 0)
                {
                    var line = pending.ToString(0, newline).TrimEnd('\r');
                    pending.Remove(0, newline + 1);
                    if (line.Length == 0)
                        continue;

                    if (!_inbound.Enqueue(line))
                    {
                        JobLog.Warn($"[session {Id}] inbound queue full; dropping the connection");
                        return;
                    }
                }
            }
        }
        finally
        {
            Close();
            _world.RemoveSession(Id);
        }
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (var i = 0; i < sb.Length; i++)
        {
            if (sb[i] == '\n')
                return i;
        }
        return -1;
    }

    /// <summary>Runs on a worker thread, one line at a time, in arrival order.</summary>
    private void HandleLine(string line)
    {
        // Replace this with your protocol.
        _world.Broadcast($"[{Id}] {line}");
    }

    private void Write(string message)
    {
        if (Volatile.Read(ref _closed) != 0)
            return;

        var bytes = Encoding.UTF8.GetBytes(message + "\n");
        try
        {
            _stream.Write(bytes, 0, bytes.Length);
        }
        catch (IOException)
        {
            Close();
        }
        catch (ObjectDisposedException)
        {
            Close();
        }
    }
}
