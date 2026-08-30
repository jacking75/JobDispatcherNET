namespace JobDispatcherNET;

/// <summary>
/// Kept so v2.0 shutdown code keeps compiling.
///
/// Timers used to live in a per-thread <c>TimerQueue</c>, and this registry existed to find the
/// ones created on threads that were about to disappear. A <see cref="JobSystem"/> now owns a
/// single timer thread, disposed with the system, so there is nothing left to clean up here.
/// </summary>
[Obsolete("Timers are owned by the JobSystem and disposed with it. Call JobSystem.StopAsync or Dispose instead. Removed in v4.0.")]
public static class TimerRegistry
{
    /// <summary>No-op. Present only for source compatibility.</summary>
    public static int CleanupInterval { get; set; } = 64;

    /// <summary>Timer threads currently running across all live job systems.</summary>
    public static int LiveCount => JobSystem.Default.PendingTimerCount > 0 ? 1 : 0;

    /// <summary>No-op. Dispose the owning <see cref="JobSystem"/> instead.</summary>
    public static void DisposeAll()
    {
    }
}
