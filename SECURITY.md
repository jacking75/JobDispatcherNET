# Security Policy

## Supported versions

| Version | Supported          | Notes                                                   |
| ------- | ------------------ | ------------------------------------------------------- |
| 2.x     | :white_check_mark: | Current release line. Fixes land here.                  |
| 1.x     | :x:                | Pre-packaging, source-only. Upgrade to 2.x.             |
| < 1.0   | :x:                | Not supported.                                          |

Fixes are shipped on the latest 2.x patch release. There is no long-term-support branch;
if you are pinned to an older 2.x patch, expect to move forward to pick up a fix.

## Reporting a vulnerability

**Please do not open a public issue.**

Two private channels, either is fine:

1. **GitHub private vulnerability reporting** — preferred.
   [Open a draft advisory](https://github.com/jacking75/JobDispatcherNET/security/advisories/new).
   This keeps the report, the discussion and the eventual advisory in one place, and lets
   us add you as a collaborator on the fix before it is public.
2. **Email** — <heungbae@com2us.com>. Put "JobDispatcherNET security" in the subject.

Please include, as far as you can:

- The affected version, target framework, OS and worker count.
- A minimal reproduction, and how reliably it reproduces.
- What an attacker gains: crash, hang, state corruption, information disclosure across
  actors, unbounded memory growth.
- Whether you have already published anything about it anywhere.

### What to expect

| Stage                                        | Target       |
| -------------------------------------------- | ------------ |
| Acknowledgement that we received the report   | 3 days       |
| Initial assessment (valid / not / need more)  | 7 days       |
| Fix, or a concrete plan and date for one      | 30 days      |
| Public disclosure                             | **90 days**  |

We aim to publish an advisory and a fixed release within **90 days** of the report. If a
fix is going to take longer we will tell you why and agree a new date with you rather than
letting the clock run out silently. If a vulnerability is being exploited in the wild we
will disclose sooner. Reporters are credited in the advisory unless you ask otherwise.

We ask that you give us the 90 days before publishing details. In return we will not
involve lawyers over good-faith research: testing against your own deployment, reporting
privately, and not accessing or destroying other people's data.

## Thread-safety defects are treated as security issues

This is a concurrency library. Its whole contract is that work on the same object is
serialized and work on different objects is not, and every user of it relies on that
contract to hold invariants without locks. So a defect in that contract is not merely a
correctness bug — it is a way to corrupt application state from the outside, and we treat
it as security-relevant.

Report the following privately, through the channels above, rather than in a public issue:

- **Lost serialization** — any interleaving where two threads run jobs for the same actor
  at the same time. Downstream this means torn writes to a player's inventory, a session
  state machine in an impossible state, a collection mutated during enumeration.
- **Lost or duplicated work** where the API promises delivery: an item accepted by
  `Sequencer<T>.Enqueue` that is never handled, a `DoAsync` that returned `true` but never
  ran, a job executed twice.
- **Permanent hangs and livelocks** — a stranded flush leader, a worker that never leaves
  a spin, a shutdown that never completes. On a server these are a denial of service.
- **Unbounded growth reachable from untrusted input** — a leak in the timer, job-pool or
  queue accounting that a remote peer can drive, in particular anything that defeats
  `MaxQueueSize` back-pressure.
- **Cross-actor state leakage** — a pooled `Job` handed out while still referenced, state
  captured by one actor visible to another.

Ordinary bugs that are annoying but not exploitable — a wrong counter in a metrics
snapshot, a misleading log line, a timer that fires 3 ms late — are normal public issues.
If you are unsure which side of the line something falls on, report it privately; we would
much rather triage a report that turns out to be routine than read about a race in public.
