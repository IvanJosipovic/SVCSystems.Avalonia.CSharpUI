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

Benchmarks cover cold generation, all four generators in isolation, the ideal cached path and
both incremental edit paths. A second benchmark class (`HotPathBenchmarks`) measures every
internal pipeline stage and the hot helper operations individually:

| Class | Operations measured |
|---|---|
| `GeneratorBenchmarks` | cold full generator; cold usage-driven; cold property extensions; cold event extensions; cold XAML resources; incremental identical; incremental comment-only edit; incremental code edit |
| `HotPathBenchmarks` | `ControlCatalog.Build`; `UsageResolver.Resolve`; `ExtensionPublishing.Read`; `ControlModelBuilder.Build`; `GenerationFingerprint.Create`; `ExternalGeneratorHost.GenerateExtensions`; `TypeAliasResolver.Resolve`; `IsOrInheritsFrom`; `GetFullTypeName`; `CleanIdentifier`; `RemoveIllegalFileNameCharacters`; `FormatXmlDoc`; `TypeKey.ToString` |

Run a subset with BDN's filter, e.g. `--filter '*HotPathBenchmarks*'` or
`--filter '*XamlResources*'`. The generator exposes internals to the benchmark assembly via
`InternalsVisibleTo` in `Properties/AssemblyInfo.cs`.

## Headline results

BenchmarkDotNet (net10.0). Full reports: `BenchmarkArtifacts/Baseline` (original code) and
`BenchmarkArtifacts/Exhausted` (final code, complete 29-benchmark suite rebuilt from current
sources, including 10-iteration stable measurements). Final stable numbers:

| Scenario | Original | Final | Change |
|---|---:|---:|---:|
| Cold full generator | 145.9 ms | **13.93 ms ± 0.13** | 10.5× |
| Incremental identical compilation | ~27 ms | 2.13 ms | — |
| **Incremental comment-only edit** | 81.0 ms / 32.5 MB | **1.58 ms / 1.11 MB** | **51× faster** |
| Incremental code edit (new control name) | 120.9 ms / 32.6 MB | 24.2 ms / 13.8 MB | 5× |
| Cold XAML resource generator | (not measured) | 32 µs | — |

Stage benchmarks (final, `BenchmarkArtifacts/Exhausted`):

| Operation | Original | Final cached path |
|---|---:|---:|
| `ControlCatalog.Build` | 3.343 ms | 85.2 µs (uncached 239 µs) |
| `UsageResolver.Resolve` | 487 µs | 2.04 µs |
| `ExtensionPublishing.Read` | 941 µs | 17.5 µs (includes the benchmark's own ref hash; lookup is O(1)) |
| `ControlModelBuilder.Build` | 236 µs | 19.2 ns (uncached 165 µs) |
| `GenerationFingerprint.Create` | 819 µs | 18.5 µs |
| `ExternalGeneratorHost.GenerateExtensions` | 31 µs | 16.4 ns |
| `TypeAliasResolver.Resolve` | 1.563 ms | 18.4 µs |
| All hot helpers | 34–81 ns | 7–82 ns |

**Measured stage costs vs end-to-end:** the sum of the isolated, cached generator stages in the
table above is roughly 0.12 ms, while a full generator run is 13.9 ms cold / 1.58 ms incremental.
These are separate measurements: the stage numbers time individual cached calls, while the
end-to-end numbers time the whole driver run. Isolated cached stages do not, by themselves, prove
where the remaining end-to-end time is spent. Attributing that remainder would require inclusive
callback profiling of the same end-to-end scenario; it is not claimed here. The residual stage cost
that is measurable comes from `GetReferenceHash` over ~200 metadata references (~18 µs), which is
needed to invalidate caches when references change.
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

## Remaining opportunities

### Applied in later passes

- `ControlCatalog.Build` takes a cancellation token, memoizes the "references Avalonia" answer per
  assembly, precomputes the common-type list, uses value-based `CatalogEntry` equality, and caches
  the entire metadata-derived index per referenced assembly symbol (`ConditionalWeakTable`), so an
  unchanged reference graph costs a dictionary merge instead of a full type walk.
- `XamlResourceGenerator` is now attribute-driven: a syntax provider selects assembly attributes,
  the generation step emits an equatable result (strings + diagnostic data), and results are cached
  by request/reference fingerprint. No compilation is retained by the incremental cache.
- `AvaloniaPropertyExtensionsGenerator` and `AvaloniaEventExtensionsGenerator` share one
  class-declaration provider (`ViewExtensionGenerator`) so each class is symbol-bound once; the two
  legacy entry points still work for isolated tests.
- All member-extension generators now append directly into the group `StringBuilder` instead of
  building intermediate strings and copying them.
- Factory methods and view-factory registrations are cached per control symbol; control models are
  cached per (symbol, generated assembly identity) so unchanged controls are not rebuilt.
- `GenerationFingerprint` caches each syntax tree's token hash and each reference's metadata ID;
  only the file the user edited is rehashed.
- `TypeAliasResolver` caches per-tree candidate-name occurrences (weakly by tree instance) in
  addition to bindings, so unchanged trees need no syntax walk at all.
- Attribute scans prefilter by simple name before formatting (`ExtensionPublishing`,
  `CommonControls`, `XamlResourceGenerator`); publishing metadata dropped from ~941 µs to ~35 µs.
- `UsageResolver` checks `IsOrInheritsFrom` once per root; `ControlModelBuilder` performs one
  `GetMembers()` scan per type and stops ownership checks at the first match; `ControlModel`
  computes its flags eagerly; `TypeKey`, `IsObsolete`, `IsAvaloniaMarkupView` and documentation are
  cached per symbol; constructor overload fragments are shared between setter generators.
- Cancellation is threaded through catalog, publishing and usage resolution; view, usage and XAML
  providers have tracking names.

### Final hotspot assessment (verified)

A 10-iteration stable run from final sources (`BenchmarkArtifacts/FinalVerified`) confirmed no
regression and quantified every remaining generator stage:

- `ControlCatalog.Build` 215 µs — the metadata index is cached per referenced assembly symbol;
  the identity map and common-control attributes are now read only on a cache miss.
- `UsageResolver.Resolve` 2.04 µs — cached by reference set, name set and local-control set.
- `TypeAliasResolver.Resolve` 49 µs — cached per-tree binding, occurrence and resolution data.
- `GenerationFingerprint.Create` 19 µs — cached token hashes and metadata IDs.
- Extension emission 33 µs per control, writing straight into a shared `StringBuilder`.
- All other helpers 8–82 ns; control models 17.7 ns on the real cached path.

The end-to-end figures (~14 ms cold, ~1.6 ms comment edit, ~24 ms for a genuine new-control edit)
measure the complete driver run, not just generator callbacks. The individually measured stages are
cached and in the tens of microseconds or less. Whether the remainder is driver work or further
generator work would need inclusive callback profiling of the same scenario; no allocation of that
time is made here. Further caching changes add shared mutable state and correctness risk, so they
are not pursued without profile evidence.
## Verification

- `dotnet test` (net10.0 + net11.0): **288/288 passing**.
- Generated source count for the benchmark workload remains 156–158 depending on the control set,
  i.e. no output regression from caching.
