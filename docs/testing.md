# Testing tiers

The test suite is split into explicit tiers so a change can be verified at the cheapest level that gives real evidence. All suites run on Microsoft.Testing.Platform and target both `net10.0` and `net11.0`.

| Tier | Project | What it proves | Command |
|---|---|---|---|
| 1. Generator and analyzer unit tests | `tests/SVCSystems.Avalonia.CSharpUI.Tests` | Emitted source shape, usage resolution, diagnostics, incremental caching, aliases/imports, XAML resource generation | `dotnet test tests/SVCSystems.Avalonia.CSharpUI.Tests` |
| 2. Analyzer tests | `tests/SVCSystems.Avalonia.CSharpUI.Analyzers.Tests` | `SVCUI001` positive/negative/near-miss cases and the `global::` code-fix round trip | `dotnet test tests/SVCSystems.Avalonia.CSharpUI.Analyzers.Tests` |
| 3. Live Avalonia control tests | same as tier 1 (`AvaloniaFact`) | Real property setters, compiled/reflection bindings, templates, styles, commands, reload, build diagnostics | `dotnet test tests/SVCSystems.Avalonia.CSharpUI.Tests` |
| 4. Build integration | `SVCSystems.Avalonia.CSharpUI.slnx` | The generator and analyzer load as analyzers in a real compiler and the three samples compile | `dotnet build SVCSystems.Avalonia.CSharpUI.slnx` |

## When to write which test

- A new naming, usage-resolution, caching or emission rule → **tier 1**, with pinned source fragments and a regression guard for any output that must *not* be emitted.
- A new analyzer or code fix → **tier 2**, with a positive case, a negative case, a near-miss that almost trips the syntactic fast path, and a fix round trip.
- A change to a setter, binding helper, view lifecycle or template → **tier 3**, against a real control.
- A packaging, target-framework or generator-host change → **tier 4**; the full solution build is the gate.

## Current coverage notes

- 308 tests pass (296 library/generator + 12 analyzer, i.e. 154 cases per target framework).
- Generated usings/import behavior is covered by `Global_factories_import_is_not_emitted_by_default`, `Opt_in_import_does_not_generate_type_aliases`, `Opt_in_factory_import_is_generated_and_alias_free`, `Post_init_global_usings_source_has_generated_header`, `No_names_are_injected_that_a_formatter_could_simplify_away` and `User_using_aliases_do_not_change_generated_output`.
- Cache correctness is covered by `Comment_only_edit_keeps_generated_sources_stable_on_one_driver` and `Generated_files_disappear_when_usage_is_removed`; the opt-in attribute is preserved across the edit so the test exercises the real pipeline.
- Samples are compiled by tier 4. They currently require `[assembly: GenerateGlobalControlFactories]` for bare factories; a sample-backed in-memory compilation tier is future work.
- Benchmarks are in `benchmarks/SVCSystems.Avalonia.CSharpUI.SourceGenerator.Benchmarks`; see `docs/performance-review.md`. Benchmark artifacts are intentionally not checked in, so regenerate them before comparing numbers.
- The analyzer suite is run against the samples on every PR with `dotnet build ... -warnaserror:SVCUI001,SVCUI002,SVCUI003`. Rule detection is validated by positive analyzer unit tests (tier 2); the sample build gate proves the rules produce no false positives on real code. Clean samples can correctly yield zero findings.

## Checks that actually prove something

Adapted from Reactor's contributor guidance. These rules apply to generator and analyzer tests, where a green run is easy to manufacture by accident.

1. **An assertion must fail when its target is broken.** Before trusting a new assertion, break the target deliberately and confirm the assertion fails.
2. **An oracle proves nothing where healthy and broken behaviour coincide.** If both paths produce the same generated text, the test is not measuring the behaviour it claims to.
3. **A no-match is not a measurement until a positive control shows the probe can match.** Silent-drop guards must assert a lower bound on what they inspected (see `Binding_valued_Avalonia_properties_generate_value_and_compiled_binding_setters`, which requires at least four properties).
4. **Self-consistency is not currency.** A snapshot compared against a file regenerated in the same command proves only that the generator is deterministic.
5. **Green runs corroborate; mechanisms establish.** At p = 0.25, three consecutive clean passes happen roughly 42% of the time. Prefer a differential oracle or a mutation check over repeated clean runs.

## Mutation checks and declared diff ledgers

- **Mutation-check every new oracle.** Verify the source edit actually landed at the intended site, that both the before and after builds exited successfully, that the product assembly was rebuilt after the edit, and that `git status --porcelain` matches the pre-mutation state after restore.
- **Treat generated-source changes as declared diffs, not opaque snapshots.** A test that accepts whatever was regenerated will bless a breaking API change. Record each intentional diff in a ledger with the reason and a commit or issue reference, then diff the ledger against the PR base. The current pinned fragments (`TestData/*Extensions.cs`) are the seed for this practice; they should be accompanied by an explicit statement of *why* each expected shape exists.
- **Do not claim stage numbers explain end-to-end time.** The generator benchmark reports isolated cached stages and full driver runs separately. Attributing the remainder requires inclusive callback profiling of the same scenario; see `docs/performance-review.md`.
