namespace JobDispatcherNET;

/// <summary>
/// Interface for objects that can be run in a dedicated worker thread.
/// Runs on a real OS thread (not thread pool), so ThreadLocal state is preserved.
/// </summary>
public interface IRunnable : IDisposable
{
    /// <summary>
    /// Called repeatedly on the dedicated worker thread.
    /// Use Thread.Sleep for yielding CPU instead of Task.Delay.
    /// </summary>
    /// <returns>True to continue running, false to stop</returns>
    bool Run(CancellationToken cancellationToken);
}
