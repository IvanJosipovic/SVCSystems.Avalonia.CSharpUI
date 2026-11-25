# Performance methodology

**Status:** methodology and "don'ts" (no measured claims in this document)
**Companion:** `docs/performance-review.md` contains the generator numbers; this document defines how numbers are produced and interpreted.

## Principle

Fix the instrument before optimizing. Reactor's perf work found that `BeginUpdate/EndUpdate` measured only `SetState` (0.04 ms) while the real render pass cost 50–185 ms, so several optimizations were ranked incorrectly. A measurement that does not include the work you are trying to change is worse than no measurement.

## What to measure

Three layers, kept separate and never summed into one headline number:

| Layer | Example | instrument |
|---|---|---|
| Generator pipeline | parse, usage resolution, model build, emit, compilation | `benchmarks/...SourceGenerator.Benchmarks` with `--profile` (Roslyn tracked steps) |
| View construction and binding | `Build()` time, setter count, binding setup, first layout | per-phase counters in a headless Avalonia fixture |
| Runtime churn | property-change storms, list edits, reload | `GC.GetAllocatedBytesForCurrentThread`, `Stopwatch`, external process counters |

Report allocations per operation alongside time. Memory sampled from inside the process can miss engine and native caches, so a managed-only figure must be labelled as such.

## How to compare

- **Same runner, interleaved.** Run `main, PR, main, PR` (at least 12 reps, 2 warmups) rather than back-to-back groups; a back-to-back pair leaves a constant thermal/scheduling bias that can show an identical binary as a ~15% delta.
- **Paired confidence interval.** Treat a change as a win or regression only when the paired 95% CI excludes zero. Do not use a fixed percentage floor: it both hides small real wins and rubber-stamps noise.
- **Deterministic flags first.** Allocation counts (`B/op`) are deterministic and should drive a row's flag; `ns/op` stays informational until calibrated.
- **One macro workload.** In addition to micro-benchmarks, keep one workload that resembles real usage: many views with control references, a comment-only edit (cache path), and a genuine new-control edit (regeneration path).
- **Pin numbers to a commit.** Investigation artifacts live in `docs/perf-investigations/` and record the commit, SDK, runner and exact command. Predicted-vs-measured tables are kept with a "cut / deprioritized" section so invalidated hypotheses remain visible.

## Don'ts

- Do not report in-app frame rate as a performance number without saying how it was counted; in-app counters can overcount by ~2x under load. Report the counter name and its definition instead.
- Do not compare runs taken under different power states. Record power state with every sample.
- Do not add caching without profile evidence. The generator already declines further caching for this reason; shared mutable caches add correctness risk that must be justified by a measured stage cost.
- Do not attribute a residual end-to-end cost to a stage you measured in isolation. Say explicitly that the remainder is unattributed.
- Do not build a privileged OS-level profiler into the library. Use external tools (`dotnet-trace`, BenchmarkDotNet, EventSource) and keep an additive, stable-name structured diagnostic layer in the product.
- Do not ship an experiment without a sunset criterion. Superseded features get a `Superseded` or `REMOVED` banner explaining where the capability moved.

## Current status

The generator has a measured baseline in `docs/performance-review.md` (cold ~14 ms, comment-only edit ~1.6 ms, genuine edit ~24 ms) with explicit "remaining opportunities" and a refusal to add more caching without evidence. Per-phase view-construction counters and the interleaved CI comparison described above are **not yet implemented**; this document is the contract for adding them.
