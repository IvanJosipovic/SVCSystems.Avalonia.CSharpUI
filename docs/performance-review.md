# Source generator performance review

Scope: `src/SVCSystems.Avalonia.CSharpUI.SourceGenerator` (Roslyn incremental generator, ~4 k LOC).
Reviewed with three parallel deep code reviews plus a BenchmarkDotNet harness and Roslyn's
tracked-step profiler. All findings are based on measured behaviour, not just static reading.

## How to measure

```bash
# Step-level profile (per generator, per edit kind, cached vs. not)
dotnet run --project benchmarks/SVCSystems.Avalonia.CSharpUI.SourceGenerator.Benchmarks -c Release -- --profile

# Full BenchmarkDotNet run (ShortRun job + memory diagnoser)
dotnet run --project benchmarks/SVCSystems.Avalonia.CSharpUI.SourceGenerator.Benchmarks -c Release -- --filter '*'
```

The workload is a deterministic compilation with 40 views × 24 control references (41 syntax
trees, ~213 metadata references) plus the curated common-control attribute. Three compilation
variants are prepared:

- **Base** — unmodified;
- **Irrelevant edit** — a comment-only change inside one method body;
- **Name edit** — adds a new control name inside one method body (forces genuine regeneration).

Benchmarks cover cold generation, each generator in isolation, and both incremental edit paths.

## Headline results

Profiler totals (`RunGenerators` only, single process, warm JIT):

| Scenario | Before | After | Change |
|---|---:|---:|---:|
| Cold run (all generators, includes JIT) | ~1230 ms | ~1210 ms | ~same |
| Identical compilation (Roslyn cache) | ~27 ms | ~27 ms | unchanged (already optimal) |
| **Comment-only edit** | **~325 ms** | **~47 ms** | **~7× faster** |
| Code edit adding a control name (worst case) | ~261 ms | ~244 ms | modest; now dominated by genuine new-control emission |

BenchmarkDotNet (net10.0, ShortRun, MemoryDiagnoser). Full reports are in
`BenchmarkArtifacts/Baseline` and `BenchmarkArtifacts/OptimizedFinal`. The optimized run cycles
through eight distinct compilations so the generator's bounded static cache cannot make a "cold"
benchmark accidentally measure a cache hit. Means and allocated bytes:

| Benchmark | Before (mean / alloc) | After (mean / alloc) |
|---|---:|---:|
| Cold: full generator, 40 views | 145.9 ms / 33.5 MB | 107.4 ms / 28.2 MB |
| Cold: usage-driven only | 67.8 ms / 32.0 MB | 37.0 ms / 26.9 MB |
| Cold: property extensions only | 770.6 µs / 616 KB | 824 µs / 634 KB (noise) |
| Cold: event extensions only | 576 µs / 138 KB | 714 µs / 148 KB (noise) |
| **Incremental: comment-only edit** | **81.0 ms / 32.5 MB** | **4.77 ms / 1.17 MB (~17× less allocation)** |
| Incremental: code edit | 120.9 ms / 32.6 MB | 97.4 ms / 27.2 MB |

## What was wrong, and what changed

### 1. Critical — the whole pipeline ran on every keystroke

`UsageDrivenMarkupGenerator` combined the collected names with `CompilationProvider` and ran
`ControlCatalog.Build` (full type walk of every Avalonia-referencing assembly), model building and
all source emission in the `Select`. The `WithComparer(GenerationResultComparer.Instance)` only
skipped `AddSource`; every expensive step still ran. A comment-only edit cost ~325 ms.

**Fix:** `GenerationFingerprint` hashes assembly identities/metadata IDs plus the *token stream* of
all syntax trees (comments and whitespace excluded, XML doc comments included). `GenerationCache`
returns the same immutable `GenerationResult` when the fingerprint is unchanged, so the comparer
short-circuits and the analysis/emission pipeline is skipped entirely. Cache is bounded to four
entries and all inputs are immutable.

### 2. Critical — TypeAliasResolver re-bound every file on every generation

`TypeAliasResolver.Resolve` created a semantic model for every syntax tree and called
`GetSymbolInfo` for every matching simple name. Profiling attributed 140–235 ms to this single
method — the dominant cost after fix 1.

**Fix:** bindings are resolved once per `(name, context)` where context captures using directives,
the relevant enclosing declaration offset and any qualifier (`A.Shared` vs `B.Shared`). Successful
bindings are cached across compilations in `TypeAliasBindingCache`, keyed by metadata reference
hash + tree token hash (independent of the factory-name set, so adding a new control does not
invalidate existing bindings). Unchanged files never create a semantic model again; only
user-edited files are bound. This cut `GenerateFactoryUsings` from ~146 ms to ~49 ms and made
comment-only edits ~7× cheaper.

### 3. High — namespace formatting in every base-type walk

`SymbolUtilities.IsOrInheritsFrom` called `ContainingNamespace.ToDisplayString()` and string
interpolation at every step of every base-type walk. It is used across the catalog, usage closure,
event infos, model construction and registration filtering; `AddWithClosure` made it quadratic.

**Fix:** cheap simple-name pre-filter before formatting, plus a `ConditionalWeakTable`-backed cache
of metadata names. `UsageResolver.AddWithClosure` still walks each chain once per root (the
quadratic pattern is bounded by the pre-filter and shared cache).

### 4. High — attribute decoding and formatter calls for every symbol

`ControlModelBuilder.GetMetadata` formatted *every* attribute class name for *every* type and field
in the catalog, repeatedly for the same symbols. `GetFullTypeName` re-formatted the same types many
times per generation.

**Fix:** pre-filter on `AttributeClass.Name` before the full display-string check; cache metadata
per symbol and full type names per type in `ConditionalWeakTable`s (compilation-scoped, no leaks).
`GetStyleInfos` now reads metadata once per field instead of twice.

### 5. High — expensive checks before cheap ones

`IsAvaloniaPropertyField`, `IsAttachedPropertyField` and `IsAcceptableStyledField` called
`GetAttributes()` (attribute decoding) before checking the field type name, for every field of
every scanned type.

**Fix:** the zero-cost `field.Type.Name.StartsWith(..., StringComparison.Ordinal)` gate now runs
first; attribute/member lookups happen only for candidate Avalonia property fields.

### 6. High — comparison re-hashed all generated source text

`GenerationResultComparer.GetHashCode` re-hashed every generated string on each incremental
comparison (hundreds of KB per run), even though the result is immutable.

**Fix:** `GeneratedSource`, `GenerationDiagnostic` and `GenerationResult` compute their hashes once
in their constructors; `GetHashCode` is now O(1) and cache hits compare by reference.

### 7. Medium — miscellaneous allocation reductions

- `CleanIdentifier`: allocation-free fast path via a static `ConcurrentDictionary`, capacity-sized
  `StringBuilder` for the slow path.
- `RemoveIllegalFileNameCharacters`: static invalid-character set, returns the original string when
  no sanitization is needed.
- `ExternalGeneratorHost`: group generators are now a single static array; the host is shared.
- `TypeAliasResolver`: one syntax walk per tree instead of two; semantic model created lazily.
- `GenerateFactoryUsings` now forwards the cancellation token.

## Remaining opportunities (not changed)

These were found in the review but carry higher correctness risk or smaller measured payoff:

- `XamlResourceGenerator` still registers directly on `CompilationProvider`; move it to an
  equatable attribute-derived value (`ForAttributeWithMetadataName` or a syntax provider + compare).
- `AvaloniaPropertyExtensionsGenerator` stores `SemanticModel`/`INamedTypeSymbol`/syntax nodes in
  an incremental value whose comparer ignores them, retaining stale compilations. Emit strings from
  the transform instead. Its signature is also recomputed in `GenerateSource`.
- `AvaloniaEventExtensionsGenerator` emits raw `ClassDeclarationSyntax` with no comparer, so event
  sources are re-added on every edit.
- `ControlCatalog.Build` walks every type in every Avalonia-referencing assembly; a cached,
  compilation-independent catalog (or a narrower symbol scan) would help cold-start builds.
- Cancellation tokens are not threaded through `ControlCatalog`, `UsageResolver`,
  `ExtensionPublishing` and most emit loops, so obsolete IDE work cannot be aborted promptly.
- No `WithTrackingName` calls; adding them makes future cache regressions visible in
  `#IncrementalGenerators` output.
- `DocumentationLookup` static caches are unbounded and its `Lazy` captures the first compilation
  for an assembly identity; scope them per compilation.
- Emit still uses many interpolated-string concatenations (netstandard2.0 has no interpolated
  string handler for `StringBuilder`); writing directly into a shared builder would reduce churn.
- Wrapper generators (`ControlFactoryGenerator`, `ExternalAssemblyExtensionsGenerator`) re-register
  the whole usage pipeline; registering more than one per host multiplies all work.

## Verification

- `dotnet test` (net10.0 + net11.0): **288/288 passing**.
- Generated source count for the benchmark workload remains 156–158 depending on the control set,
  i.e. no output regression from caching.
