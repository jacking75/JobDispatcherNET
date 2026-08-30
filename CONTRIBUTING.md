# Contributing to JobDispatcherNET

Thanks for taking the time. This is a concurrency library, so the review bar is a little
unusual: the code style rules are short, and the rules about *proving* a change are long.

- Bugs and feature proposals: the [issue tracker](https://github.com/jacking75/JobDispatcherNET/issues).
- "How do I", "is this the right pattern", threading-model questions:
  [Discussions](https://github.com/jacking75/JobDispatcherNET/discussions).
- Security and thread-safety defects that can corrupt state: **do not open an issue** —
  see [SECURITY.md](SECURITY.md).

Everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Prerequisites

- .NET SDK **10.0.301** or newer. The library multi-targets `net8.0;net10.0`, so the
  .NET 8 runtime must also be installed to run the tests against both.
- Any editor. The repository ships an `.editorconfig`; keep it enabled.

## Build

```bash
dotnet build All.sln
```

`All.sln` contains the library, the tests, the benchmarks and every sample. Build the
whole solution before opening a PR — the samples use the public API and are the first
place an accidental breaking change shows up.

The core library sets `TreatWarningsAsErrors`, `Nullable`, `EnableNETAnalyzers` and
`GenerateDocumentationFile`. A warning in `JobDispatcherNET/` is a build break, including
a missing XML doc comment on a public member.

## Test

```bash
# everything except the stress suite (this is what you run while working)
dotnet test JobDispatcherNET.Tests/JobDispatcherNET.Tests.csproj --filter "Category!=Stress"

# the whole suite, including stress
dotnet test JobDispatcherNET.Tests/JobDispatcherNET.Tests.csproj

# only the stress suite
dotnet test JobDispatcherNET.Tests/JobDispatcherNET.Tests.csproj --filter "Category=Stress"

# one framework at a time
dotnet test JobDispatcherNET.Tests/JobDispatcherNET.Tests.csproj -f net8.0
```

Stress tests are marked `[Trait("Category", "Stress")]` (see
`JobDispatcherNET.Tests/MetricsAndStressTests.cs`). They run for tens of seconds, push
millions of jobs, and assert on throughput and drain latency, so they are unreliable on a
shared CI runner — CI runs them in a `continue-on-error` step. Run them locally on an
otherwise idle machine before any change to the admission path, the flush loop, the timer
thread or the ready queue.

Give a new stress test the trait too. Anything that takes more than a couple of seconds
or asserts on wall-clock timing belongs there, not in the default suite.

## Benchmarks

```bash
dotnet run -c Release --project JobDispatcherNET.Benchmarks -- --filter "*"

# one benchmark class
dotnet run -c Release --project JobDispatcherNET.Benchmarks -- --filter "*Throughput*"

# list what is available
dotnet run -c Release --project JobDispatcherNET.Benchmarks -- --list flat
```

Always `-c Release`; BenchmarkDotNet refuses to run a Debug build for good reason. Run on
a quiet machine with no debugger attached.

**Performance changes need numbers.** If a PR is justified by speed, paste the
BenchmarkDotNet summary for before and after into the PR. "This should be faster" is not
a justification — the leader-flush model has enough non-obvious cache and contention
behaviour that intuition is frequently wrong.

## Code style

The `.editorconfig` at the repository root is the authority; the points worth stating
explicitly are:

- **Nullable reference types are on**, everywhere. Do not add `#nullable disable`. If the
  compiler is complaining, the model is wrong, not the compiler.
- **Warnings are errors** in the core library. Do not suppress with `#pragma` unless you
  add a comment saying why the analyzer is wrong here.
- **XML documentation is required on every public member.** Document what the caller must
  guarantee and which thread the code runs on, not just what the method is named.
  "Returns the queue depth" is not useful; "Jobs admitted to an actor queue and not yet
  retired — a snapshot, already stale by the time you read it" is.
- **File-scoped namespaces** (`namespace JobDispatcherNET;`). One top-level type per file
  unless the extra types are small and only exist to serve the main one.
- 4-space indent, no tabs. `var` when the type is apparent from the right-hand side, an
  explicit type when it is not.
- Expression-bodied members are fine and used widely for one-line properties and
  forwarding methods.
- **`static` lambdas on hot paths.** Anything reached per job — `DoAsync`, the flush loop,
  the admission CAS, timer dispatch — must not capture. Write `static () => ...` or use
  the `DoAsync<TState>` overload and pass the state explicitly. A captured lambda is a
  per-job allocation, which is exactly what the `TState` overloads exist to avoid.
- Prefer `Volatile.Read` / `Volatile.Write` / `Interlocked` over `lock` on anything a
  worker touches per job, and say in a comment what ordering you are relying on.

## Pull request checklist

The template asks for all of this; here is the reasoning.

1. **Tests for every behaviour change.** A fix without a test that fails before it and
   passes after it will be sent back, however obviously correct it looks.
2. **A `CHANGELOG.md` entry** under `## [Unreleased]`, in the right group
   (Added / Changed / Fixed / Deprecated / Removed). Write it for someone upgrading, not
   for someone reading the diff.
3. **No public API change without a note.** Adding, changing or removing a public member
   must be called out in the PR description and in the changelog. Removals go through
   `[Obsolete]` first: the project deprecates in one major version and removes in the
   next (see `TimerRegistry` and `AsyncExecutable.AcceptingWork` for the pattern).
4. **No new dependency in the core package.** Zero external dependencies is a feature —
   it is why the library is usable in constrained hosts. Integrations belong in
   `JobDispatcherNET.Extensions.*`.
5. Keep the PR to one concern. A fix plus a refactor plus a rename is three reviews.

## Concurrency changes need a regression test that fails without the fix

This is the one rule with no exceptions.

Every race this library has shipped was "obviously" impossible until someone wrote the
test that produced it. A patch to a lock-free path that is only argued for in prose is
indistinguishable from a patch that moves the window somewhere harder to hit. So: if you
change `AsyncExecutable`'s admission or flush path, `JobSystem`'s ready queue,
`TimerService`, the worker supervisor or `Sequencer<T>`, the PR must include a test that
**fails on `main` and passes with your change**.

Use `JobDispatcherNET.Tests/RegressionTests.cs` as the model. Each test there targets one
defect, and each one has the same shape:

- An XML doc comment naming the defect and stating in one sentence what used to go wrong.
- A dedicated `JobSystem` / `TestSystem` so the test does not share global state and can
  run in parallel with the others.
- The interleaving forced open on purpose — `Thread.Yield()` at a chosen point,
  a deliberately tiny `MaxQueueSize`, a worker made to throw — rather than hoping the
  scheduler cooperates.
- Assertions on the *invariant*, not on a timing: `MaxConcurrent == 1`,
  `executed == accepted`, `RemainingTaskCount == 0`.
- A guard that the test proved something at all — `P0_1_BoundedRejectionNeverStrandsTheLeader`
  asserts that some jobs were accepted **and** some were rejected, so a future change that
  accidentally makes the queue unbounded fails the test instead of passing it vacuously.

Say in the PR description which interleaving you are claiming is now impossible and which
atomic operation rules it out. If you cannot make the test fail on `main`, say so
explicitly and describe how you tried; sometimes the answer is to widen the window with a
`SpinWait` or a `Thread.Sleep(0)` in the producer, and sometimes the answer is that the
bug is somewhere else.

## Commit messages

Short imperative subject line, a body if the "why" is not obvious. Reference the issue
(`Fixes #123`). No particular format is enforced.
