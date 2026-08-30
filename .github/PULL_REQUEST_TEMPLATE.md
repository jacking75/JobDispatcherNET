<!--
Thanks for the PR. Keep the summary short; the checklist is the part reviewers read first.
For anything touching AsyncExecutable, JobSystem, TimerService or Sequencer,
please fill in the "Concurrency" section — it is not optional for those files.
-->

## What this changes

<!-- One or two sentences. Link the issue: "Fixes #123". -->

## Why

<!-- The behaviour that was wrong, or the thing that was impossible before. -->

## Checklist

- [ ] `dotnet build All.sln` is clean — the core library builds with `TreatWarningsAsErrors`, so a warning is a build break.
- [ ] `dotnet test JobDispatcherNET.Tests/JobDispatcherNET.Tests.csproj` passes on both `net8.0` and `net10.0`.
- [ ] Tests added for every behaviour change. A bug fix without a test that fails before the fix will be sent back.
- [ ] `CHANGELOG.md` has an entry under `## [Unreleased]`, in the right group (Added / Changed / Fixed / Deprecated / Removed).
- [ ] Public API unchanged — **or** the change is called out below and noted in the changelog under Changed / Removed.
- [ ] New public members have XML documentation that says what the caller must guarantee, not just what the method is named.
- [ ] No new dependency in the core `JobDispatcherNET` package. Dependencies belong in an `Extensions.*` package.

## Concurrency

<!--
Required if you touched AsyncExecutable, JobSystem, TimerService, JobDispatcher, Sequencer
or anything else on the hot path. Delete this section only for docs and samples.
-->

- Regression test that fails without this fix:
- Which thread runs the new code (worker / timer thread / arbitrary producer):
- Interleaving you are claiming is now impossible, and the memory operation that rules it out:

## Public API changes

<!-- "None", or list added / changed / removed members. -->

## Benchmarks

<!--
Only for changes justified by performance. Paste the BenchmarkDotNet summary for
before and after. "Should be faster" is not a justification — the project rule is
that performance changes need numbers.
-->
