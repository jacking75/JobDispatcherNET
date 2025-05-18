using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobDispatcherNET;

/// <summary>
/// Interface for objects that can be run in a worker thread
/// </summary>
public interface IRunnable : IAsyncDisposable
{
    /// <summary>
    /// Runs the work loop
    /// </summary>
    /// <returns>True to continue running, false to stop</returns>
    ValueTask<bool> RunAsync(CancellationToken cancellationToken);
}
