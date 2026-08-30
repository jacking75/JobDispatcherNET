using BenchmarkDotNet.Running;

namespace JobDispatcherNET.Benchmarks;

/// <summary>
/// Entry point. Run every benchmark with:
/// <code>dotnet run -c Release --project JobDispatcherNET.Benchmarks -- --filter *</code>
/// or one family with <c>--filter *PingPong*</c>. Add <c>--job Dry</c> for a correctness smoke run.
/// </summary>
public static class Program
{
    /// <summary>Hands the command line to BenchmarkDotNet's switcher.</summary>
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
