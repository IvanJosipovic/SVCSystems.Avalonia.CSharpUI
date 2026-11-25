# What CSharpUI can learn from Microsoft.UI.Reactor — deep review

**Status:** review report (no code changed)
**Date:** 2026-11-25
**Sources:** `microsoft-ui-reactor` @ `8ed5346f`; `SVCSystems.Avalonia.CSharpUI` @ `2f8596f` (`v1.0.0-alpha.11`)
**Method:** six parallel deep reviews (core/reconciler; DSL + generators; analyzers + `mur check`; MVVM-compatible framework features; perf/diagnostics/testing; layout/navigation/animation/features), cross-checked against `docs/review-findings.md`, `docs/architecture.md`, `docs/performance-review.md` and direct source reads.

**Filter applied throughout:** copy generator/analyzer/API/diagnostic engineering, headless state models, measurement and test discipline, and MVVM-agnostic framework services. **Never copy** hooks, the virtual element tree, the reconciler, keyed element diffing as a rendering model, descriptor interpreters, echo-suppression hooks, or interceptor-based source maps. CSharpUI's `ViewBase<TViewModel>` + generated factories + typed expression bindings + Avalonia's own property/binding/style system already provide the useful subset of Reactor's value proposition.

---

## 0. Verdict

Reactor's durable contribution is **not its MVU runtime**. It is the authoring and engineering discipline around it:

> attribute-per-decision → metadata-name-bound attributes in a separate assembly → incremental generator with a model and string emit → per-element hint names → an analyzer for every attribute → a silent-drop reflection guard → factory-cctor as the trim-safe chokepoint → generated code compiled against a small frozen public seam → specs that record which earlier decision was wrong and why.

CSharpUI already has the factory chokepoint, the incremental generator, the usage-driven model, cross-assembly publishing, caller-info source metadata and a working `MetadataUpdateHandler`. The largest gaps relative to Reactor are **not in the generator core** — they are (1) the near-absence of an analyzer suite, (2) no CLI lint/did-you-mean layer, (3) no semantic command/theme/a11y/async/data service vocabulary, and (4) a thin process/docs/measurement discipline around specs, reviews and benchmarks.

`docs/reactor-vs-xaml.md:48-57` is the decisive architectural caution: in Reactor, attaching a `{Binding}` to a control the reconciler owns creates a parallel reactive edge the next reconcile pass overwrites. CSharpUI's choice to let Avalonia bindings own state flow is the opposite side of that trade-off, and it is the correct one for an MVVM library.

---

## 1. Priority-ordered master list of everything found

Priorities: **P0** = do now (correctness / high leverage / low risk); **P1** = next (high-value features and tooling); **P2** = later (scale / advanced); **P3** = process and docs (adopt the practice, not necessarily the code). Numbering is the global priority order.

### P0 — do now (1–34)

**1. Expand the analyzer suite from one rule to a real catalog.** Reactor ships ~40 analyzer files / ~70 IDs (`src/Reactor.Analyzers`, `AnalyzerReleases.Unshipped.md`, `docs/guide/analyzer-architecture.md`). CSharpUI ships only `SVCUI001`. Highest-value ports: property-invoked-as-method, fuzzy factory name, factory argument shape, invalid constant string, icon-only-button / image-alt / form-label / keyboard-reachability, event subscribed in a repeatedly-run build, mutate-then-raise on the same reference, framework-owned collection/selection writes, imperative write where a generated declarative setter exists, XML docs (internal-only), blocking `Task.Result/.Wait()`, non-static registration lambda. **CSharpUI:** expand `src/SVCSystems.Avalonia.CSharpUI.Analyzers`.

**2. Enforce analyzer release tracking.** Every diagnostic = descriptor + `AnalyzerReleases.Unshipped.md` row + user catalog row; Roslyn `RS2008` fails the build on a missing row; rows migrate to `Shipped.md` on GA (`analyzer-architecture.md:561-569`).

**3. Use the Reactor analyzer pipeline shape.** Cheap syntactic gate → name fast-path → symbol resolution → report; `ConfigureGeneratedCodeAnalysis(None)` + `EnableConcurrentExecution`; `RegisterCompilationStartAction` to resolve symbols once; `RegisterSymbolAction` for per-symbol questions. `analyzer-architecture.md:616-657` notes `GetSymbolInfo` on every invocation can double build time.

**4. Treat fix restraint as a feature.** Carry fix data in `Diagnostic.Properties` (never re-parse message text); use fully-qualified `global::` syntax with `Simplifier.Annotation`; withhold fixes that would emit uncompilable code; offer a template instead of a silent rewrite for non-mechanical changes (`spec 061:301-318`, `analyzer-architecture.md:669-678`). The existing `global::` fix is a good start.

**5. Add a silent-drop guard for generated APIs.** Reactor's `DescriptorSilentDropGuardTests` reflects every annotated wrapper, mirrors the generator's type-support rules, fails if a property is silently dropped, and self-validates it inspected ≥20 controls so it cannot pass vacuously (`spec 058:318-327`). CSharpUI's generator skips properties of unsupported types — add the equivalent guard over generated metadata.

**6. Put shared generator attributes in one tiny referenced assembly, bound by metadata name; never synthesize them.** Reactor used `RegisterPostInitializationOutput` and hit `CS0436` through `InternalsVisibleTo` in friend assemblies that also ran the generator; the fix was `Reactor.Wrappers.Abstractions` (`WrapperAttributes.cs`, `spec 058:634`). Verify CSharpUI's attribute packaging for cross-assembly extension publishing and add an absence regression guard.

**7. Add attribute-site analyzers one-to-one with generator attributes.** Reactor has REACTORGEN002–016 with a matching `Wrap*Analyzer` for every `Wrap*` attribute (`spec 058:445-447`). Every `[SVCSystemsCSharpUIGeneratorMetadata...]` / `[GenerateXamlResource...]` option should have a compile-time validator.

**8. Keep the factory as the single chokepoint and use trim-clean registration.** Reactor roots handlers through the factory, uses `static` captureless lambdas, `ConcurrentDictionary.TryAdd` (first wins, no throw), and explicitly rejects module initializers, reflection scanning and eager central registrars (`spec 048:123-153,157-180,326-361`; `spec 062:166-173`). CSharpUI's registry is now first-wins but uses `[ModuleInitializer]` per control — measure module-initializer startup and add a publish-trim test proving unused controls are absent.

**9. Adopt source-mapping mechanics, not the interceptor.** Reactor's two gates (MSBuild `ReactorSourceMap` welded to `InterceptorsNamespaces`; runtime `Volatile` flag), `OriginalDefinition` for signatures, manual `PathMap` + separator normalization, mapped line spans, first-stamp-wins, and linked (not copied) shared position source (`spec 010`). CSharpUI's caller-info + `SourceInfo` already covers the useful subset — keep it and adopt the two-gate/pathmap/first-stamp rules if richer metadata is added; do not adopt interceptors (the `params` blind spot does not apply to CSharpUI's factory shape).

**10. Make "never publish a stale in-flight result" one coherent rule.** Generation stamps for theme resolution (`Theme.cs:49-138`), async validation (`BeginAsyncValidation`/`ApplyAsyncValidation`), query-cache dependency snapshots, and unmount-linked cancellation tokens (`UseMutation.cs`). Apply to any cache, async command or incremental rebuild.

**11. Add a command descriptor with an `ICommand` adapter.** Reactor's `Command` record bundles label/icon/description/accelerator/access-key/`CanExecute`/`IsExecuting`/`DebounceMs` with `with { }` per-site overrides, 16 standard commands, and metadata propagation to tooltip/HelpText/accelerator plus explicit cleanup (`Core/Command.cs`, `Core/StandardCommand.cs`, `Core/CommandBindings.cs`, `spec 012`); it ships `CommandInterop.FromCommand(ICommand, …)`. Fills the explicit `docs/review-findings.md:110` gap.

**12. Clear stale command metadata and diff accelerators.** Reactor's `UpdateCommandHost` rebuilds every `KeyboardAccelerator` each render — a known O(n) COM-churn weakness (`docs/research/critical-review.md:805-823`). Register key bindings once on the owning window/scope, remove on unmount, diff by `KeyGesture` signature, and clear tooltip/HelpText/access-key when a command goes null.

**13. Add generated accessibility setters, a live-tree scanner with JSON export, four analyzers, and a test helper.** `A11yDiagnostic` carries `WcagCriterion`, a fix suggestion with code snippet, and `A11yContext` (parent name, nearest heading/landmark, sibling texts, child content, placeholder) deliberately so an agent can generate intelligent names rather than placeholder text (`Core/AccessibilityScanner.cs`, `spec 006:703-750`); `IScanExtension` lets subsystems contribute checks. Scan Avalonia's live tree and emit `obj/Debug/csharpui-a11y.json`.

**14. Add `AsyncValue<T>` + `MutationCommand` + `QueryCache` as MVVM services.** Closed record hierarchy `{Loading, Data, Reloading(Previous), Error}` forces exhaustive handling; `QueryCache` is a scoped `Context`, not a static singleton (fresh per test, wipeable on sign-out); `UseMutation` applies optimistic updates synchronously before starting, fires rollback on failure and never invalidates on failure (`Core/AsyncValue.cs`, `Core/QueryCache.cs`, `Hooks/UseMutation.cs`, `spec 020`). Expose `MutationCommand<TIn,TOut> : ICommand` with `IsPending`/`Error`/`InvalidateKeys` and deactivation-linked cancellation.

**15. Build a keyed list bridge for virtualization.** Reactor keeps an internally-owned `ObservableCollection<ReactorRow>` as a **reference type** so native consumers distinguish "moved" from "removed+inserted", applies a prefix/map keyed diff with append/prepend/remove fast paths and a 25% churn bailout, uses a fixed-height fast path, and documents ItemsRepeater stale-index (`#12659`) and focus-pinning hazards (`spec 042`, `Core/Internal/KeyedListDiff.cs`). Port the algorithm and `IKeyedItem` convention into a helper for Avalonia virtualizing panels; keep Avalonia's virtualization.

**16. Coalesce change notifications into one UI-thread batch, with priority demotion.** `ReactorHost.RequestRender`/`RenderLoop` sets `_needsRerender` first, uses `Interlocked.CompareExchange` for a single enqueue, re-enqueues at Low priority after a >16 ms pass, and does the first render synchronously (`Hosting/ReactorHost.cs:881-1002`, `RenderPriorityPolicy.cs:5-40`). Apply to VM `PropertyChanged` storms, demoting to `DispatcherPriority.Background` after a long batch — but never re-execute a "Render()".

**17. One shared skip predicate with hard-wired decline gates.** `Element.CanSkipUpdate` combines `ShallowEquals` + a *shared* `ChildDiffHints.IsThemeSensitive` predicate + callback-presence equality; every incremental path uses that one gate (`Core/Element.cs:480-483,521-839`). Fail-safe direction is "re-resolve, never skip" (`count != 0`, and keyed-middle deliberately has no skip arm). Compute "which bound properties actually changed" once per notification and let one predicate drive all generated setters.

**18. Dirty-ancestor propagation.** `PopulateDirtyAncestorPath` walks from each self-triggered node to the root; `ResolveDirtyChildIndices` lets child reconciliation skip only safe children; the `HashSet` is allocated lazily on the first dirty node (`Reconciler.cs:1405-1474,3789-3819`). Needed if any parent-level "skip unchanged" optimization is added, otherwise a nested VM change gets swallowed.

**19. ArrayPool-backed allocation-free diff internals.** Rent `int`/`bool`/`string`/reference buffers, `clearArray: true` only for reference arrays, return in `finally`; re-entrant calls must own distinct buffers, so use `ArrayPool`, not a single `ThreadStatic` scratch (`ChildReconciler.cs:795-939,1152-1213`, `KeyedListDiff.cs:146-185`).

**20. Bounded LRU/keyed memo caches with effectiveness metrics.** `KeyedMemoCache` (capacity 128, value-equality keys so boxed ints dedupe, `FactoryInvocations` metric, invalidation on closure change) and `LruCache<TKey,TValue>` (evict-before-insert, `Trim` for memory pressure). Cache converted values (Brush/Geometry/templates) and generated results, not virtual nodes.

**21. Bucketed metadata so diagnostic features are free in production.** `ElementExtras` holds rare fields in one nullable bucket; `IsBehaviorallyEmpty` prevents diagnostic-only metadata from affecting behavior; `EqualityIgnored<T>` and a per-call-site cached bucket avoid per-element cost (`Element.cs:1903-2140`). `SourceInfo`'s CWT is already the right shape; beware that an equality-ignored field is *not* free once it materializes a container (`spec 010:675-706`).

**22. `Optional<T>` authority semantics — the semantics, not the descriptor machinery.** `Unset` means "the control/user owns this value"; `Of(x)` force-asserts; `OneWayClearValuePropEntry` calls `ClearValue` so a local value stops outranking styles (`Core/Optional.cs`, `PropEntry.cs:113-161,268-383`, `spec 050`). In generated bindings, `Unset` simply means "emit no binding for this property" — structurally preventing the "VM default clobbers user typing on refresh" bug.

**23. Echo suppression via native-object-attached state, when code writes bound properties.** Reactor uses an attached DP rather than a `ConditionalWeakTable` because WinRT can hand out two RCWs for one native object (`ChangeEchoSuppressor.cs:40-48`), exposed to authors only through `ReactorBinding.WriteSuppressed`. Avalonia bindings usually remove the need; keep the pattern for imperative writes into TwoWay-bound controls.

**24. Generate overload ladders, not god-methods; escape-hatch-first layering.** Uniform argument order (`.Margin(h, v)` then `.Margin(l,t,r,b)`), named per-side defaults, a value-type overload for every scalar shorthand (`ElementExtensions.cs:42-89`), `@`-escaped parameter names (`WrapperGenerator.cs:1881-1888`), long-form aliases for first-instinct names (`.HorizontalAlignment` → `.HAlign`), and a uniformly generated `.Set(Action<TControl>)` escape hatch (`spec 039 §0.2`). Note the C# "property-as-delegate" trap: never generate `.OnClick(handler)` when `OnClick` is a property; use `.Click(handler)` (`spec 039:22-38`).

**25. CLI lint tool modeled on `mur check`.** Four tiers (static hint table; Jaro–Winkler `SymbolSuggester` over CS1061/CS0103/CS0117/CS1503/CS7036; symbol-bound `IRulePattern` rewrites; future re-ranker), a pre-emit ranker with `iteration/strict/final/quiet` modes and a conservative unknown-code default, a diagnostic-count gate (start N=3), compilation load skipped when no diagnostic is suggestable, `--disable-rule` kill-switches, rules that self-disable when their target symbol is renamed, JSONL trace and opt-in byte-bounded local telemetry (`spec 038`, `src/Reactor.Cli/Check`). CSharpUI has no CLI today — this is a larger gap than any single analyzer rule.

**26. "Wrong is worse than silent."** Validation gate: ≥3 positive fixtures, ≥2 negatives, corpus frequency ≥0.05 and count ≥10 (or a documented prior-framework citation), reproducibility across ≥2 agents, reviewer signoff, and a kill-switch per rule (`spec 038 §6`). In-build fuzzy matching is deliberately stricter than CLI (0.88 vs 0.75) with PascalCase/length-delta/unbound-name gates calibrated to 0 FP at 40/40 recall (`FuzzyFactoryNameAnalyzer.cs`).

**27. Ground did-you-mean rules in real agent failures (eval-trace mining).** `spec 037` drives an agent in a hermetic sandbox with only the public package and CLI, captures JSONL build cycles, extracts `(broken_source, diagnostic, fixed_source)` triples plus per-diagnostic labels (`addressed_by_next_fix`, `agent_ignored`), clusters into `patterns.json`, and requires **human review before any runtime rule**. Use this instead of guessing which suggestions CSharpUI users need.

**28. Run analyzers against `samples/` before merge.** A rule that finds nothing in the samples is over-fit or gated too narrowly (`analyzer-architecture.md:674-678`, `spec 060 §7`). CSharpUI samples already compile in CI tier 4; make them an analyzer gate.

**29. Adopt the test *machinery*, not just a tier table.** Reactor's selftest architecture: one fixture protocol (TAP) shared by headless and real-window harnesses; round-robin sharding with pinned controls and `Shards_PartitionTheCorpus` / `Run_CoversExactlyTheDiscoveredFixtures` gates; a three-state verdict where zero checks + skips = Skipped, never Passed, with an always-skipped positive control; explicit wait-primitive rules (`Render` only for event-queue pumps, `WaitFor` re-queries, never `WaitFor` for negative/exact-count/survival assertions because it short-circuits at t=0); fail attribution by causal vs positional evidence (`TESTING.md:293-325,391-463,465-519,579-635`). CSharpUI's `docs/testing.md` already sketches four tiers — add these mechanisms.

**30. Mutation-check every oracle; use declared diff ledgers, not full snapshots.** Reactor's transform matrix records 432 cases and requires every diff against base to be declared with a row/issue in a ledger that a unit test checks against the PR's base commit (`TESTING.md:766-889`). Mutation checks must verify the source diff landed, both processes exited successfully, the assembly rebuilt, and `git status` is restored (`TESTING.md:141-157`). CSharpUI's pinned generated-source snapshots are weaker: a regenerated snapshot blesses a breaking change.

**31. Put "checks that actually prove something" in the contributor guide.** Reactor's `AGENTS.md:286-312`: an assertion must fail when its target is broken; an oracle proves nothing where healthy and broken branches coincide; a no-match is not a measurement until a positive control shows the probe can match; self-consistency is not currency; "green runs corroborate a fix; a mechanism establishes it" (at p=0.25, three clean passes happen 42% of the time). Adapt nearly verbatim for generator tests.

**32. Fix instrumentation before optimizing; measure with A/B + paired CI.** Reactor's `/perf` CI interleaves `main, PR, main, PR` with 12 reps / 2 warmups and treats a result as win/regression only when the paired 95% CI excludes zero; micro-suites are rep-interleaved because a back-to-back pair showed an identical binary as a spurious −14.8% delta (`tests/stress_perf/ci/README.md:49-94,150,218-230,306-332`). `spec 007:860-867` found `BeginUpdate/EndUpdate` measured only `SetState` (0.04 ms) while real cost was 50–185 ms. CSharpUI: add per-phase counters (parse/model/emit/compilation) and one macro workload reflecting view-build/bind churn, then interleave against `main`.

**33. Write a perf methodology with explicit "Don'ts" and commit-pinned artifacts.** Reactor's `tests/stress_perf/METHODOLOGY.md`: in-app FPS overcounts ~2×, so report `Total Renders` or authoritative ETW `DxgKrnl::Present`; `Effective Refresh = min(in-app renders/s, ETW Present/s)`; every CSV carries `GlobalVsyncPerSec` and `PowerState` (never diff battery against AC); memory is sampled externally via `WorkingSet64`; an `--percent 0` leg isolates the child skip-walk floor; a dedicated keyed-list leg exercises the LIS arm (`METHODOLOGY.md:41-64,99-132,165-221,223-312`). CSharpUI: `docs/perf-experiments.md` with predicted-vs-measured tables, a "cut/deprioritized" section and a close-out pinned to a commit; keep `docs/performance-review.md`'s refusal to add caching without profile evidence.

**34. Add a real-build smoke test and sample-backed in-memory compilation tier.** These are CSharpUI's explicit future work (`docs/testing.md:24`) and the gap that let the generator/analyzer packaging change go untested in a real compiler. Pair with the formatter/simplifier regression test already added for G1/T2.

### P1 — next (35–57)

**35. Theme tokens + per-control resource overrides + deterministic style cache.** `ThemeRef` records, three-tier precedence (unset → token → local), a generation-stamped resolution cache that caches only successful non-null resolutions, a style cache keyed by `targetType|sorted bindings`, and a `BasedOn` wrapper for author styles with remembered incompatible bases (`Core/Theme.cs`, `Reconciler.cs:592-680`, `spec 001`, `spec 015:464-643`). Resolve CSharpUI tokens to Avalonia `DynamicResource` where possible so Avalonia's theme propagation self-heals; add `.Resources(Action<ResourceBuilder>)` over `Control.Resources`.

**36. `FieldDescriptor` as the shared metadata spine.** One record drives FormField, PropertyGrid and DataGrid: name/display/type/get/set (set returns the possibly-new owner so immutable records work), editor factory, validators, width/flex/pin/sort/filter/cell renderer (`Core/Data/FieldDescriptor.cs`, `spec 017:653-777`). CSharpUI editors become `Func<object, object?, Control>` or metadata for generated views.

**37. Headless `DataGridState<T>` as a ViewModel.** Pure logic (sort/filter/search, selection with ctrl+shift/anchor-focus/select-range, virtualization range loading, column resize/reorder/pin/hide, edit state, optimistic `BeginAsyncCommit`/`CompleteAsyncCommit`/`FailAsyncCommit`, validation messages, keyboard focus moves) testable with no visual tree (`src/Reactor.Advanced/Controls/DataGrid/DataGridState.cs`, `spec 017:1097-1107`). This is the single most MVVM-friendly Reactor artifact — bind an Avalonia DataGrid to it.

**38. `IDataSource<T>` / `RowKey` / `DataPage<T>` / `DataRequest` service model.** Keys not indices, cursor pagination, serializable descriptor objects instead of lambdas, capability flags for server-side push-down, pages not streams; `IQueryable`/`IAsyncEnumerable` explicitly rejected (`spec 017:105-133,351-381`). Clean fit for VM repositories.

**39. Validation with generation guards + `INotifyDataErrorInfo`.** Producer-stamped `ValidationContext`, `IValidator`/`IAsyncValidator`, `BeginAsyncValidation` → `ApplyAsyncValidation` discards a result superseded by a newer value, rule sets with per-producer generations (`src/Reactor/Controls/Validation`, `spec 017`). Expose one source of truth through `INotifyDataErrorInfo`.

**40. Localization stack (large but modular).** ICU `MessageFormat`, typed key source generator over `.resw` `AdditionalFiles` with a configurable missing-key diagnostic, CLI `extract/translate/validate/status/prune`, pseudolocalization, BiDi sanitization, rich messages that escape args *before* formatting so a translator cannot mint a `<link>` tag, allocation-light tuple overload instead of reflection args (`spec 005`, `src/Reactor.Localization.Generator`, `src/Reactor.Cli/Loc`). Plugs into CSharpUI's existing generator project; add logical-layout setters (`MarginInlineStart`, `PaddingInlineEnd`) for RTL.

**41. Navigation model without the hook.** Typed routes as records (structural equality, `with`, JSON-serializable, exhaustive `switch`), developer-owned back stack as pure data with cancellable `NavigatingFrom`, JSON `GetState`/`SetState` for deep links, LRU cache with `Disabled/Enabled/Required` and `EvictLocked` skipping `Required`, independent stacks per tab (`spec 011`, `Core/Navigation/NavigationStack.cs`, `NavigationCache.cs`). MVVM service + DI + `RoutedViewHost`/view locator; skip `UseNavigation`/`NavigationHostElement`.

**42. Animation vocabulary, not the reconciler scope.** Immutable `Curve`/`Easing` records; composable `+` (parallel) / `|` (asymmetric) transitions; trigger-keyed keyframes (replay on any equatable value change) instead of edge booleans; stagger via parent-computed `DelayTime`; scroll-linked expression templates; interaction-state split between zero-cost compositor properties and brush swaps (`src/Reactor/Animation`, `spec 014`, `spec 066`). Generated Avalonia `Transitions` setters / attached behaviors / `IAnimationService` from commands.

**43. Gesture/config records + attach-once event trampoline.** `PanGesture`/`PinchGesture`/`RotateGesture`/`LongPressGesture` as readonly record structs with phase + cumulative/delta/velocity, immutable config records, automatic enabling of native tap/hold recognition (otherwise `OnDoubleTapped` on a `Rectangle` silently does nothing), one stable delegate attached once with a mutable "current handler" field instead of `+=`/`-=` per render (`spec 027`, `src/Reactor/Input`). Pure event-plumbing win for generated event setters.

**44. Window spec + docking layout algebra.** Immutable `WindowSpec` record (DIP-only, diffed on update) with `ResizeMode`/`AspectRatio`/`WindowLevel`/`WithPersistence`; docking layout as an immutable POCO algebra (`DockSplit`/`DockTabGroup`/`DockableContent`) independent of UI, with keyed panes, role split (Document/ToolWindow), per-pane state blobs, schema version + migration registry, ~12 cancellable `*ing` events, previous-container memory and a reserved DocumentArea role (`spec 054`, `spec 036`, `spec 045`, `spec 046`, `src/Reactor.Advanced/Docking`). Keep it bindable (`DocumentsSource`-style VM collections) — Reactor explicitly rejected that API, and an MVVM library must not.

**45. Partial-fill / "attach" generation for hand-written wrappers.** The generator emits only missing members into an author's partial record so individual entries can be overridden in place, into the **author's** namespace; descriptor-only mode changes no public API and requires "no test edits" (`spec 058:20-34,528-559`). Ideal for CSharpUI's public extension-publishing story: a library ships a curated API and the generator fills the mechanical 95%.

**46. Name-mapping attributes.** `[WrapAlias]`, `[WrapOneWay]`, `[WrapConvert]` (element type inferred from a struct's single-arg constructor), `[WrapContent]`, `[WrapEvent(Args=)]` (`spec 058`). Avalonia equivalents: `Content`→`Text` aliases, `double`→`Thickness`/`CornerRadius`/`GridLength`, event-arg projection from `RoutedEventArgs`-derived args.

**47. Binary compatibility discipline for the generated-code seam.** Freeze a small additive-only surface; enforce with `PublicApiAnalyzers` + `ApiCompat` and a **binary** interop harness that loads a pre-compiled DLL (recompiling hides breaks); bounded dependency ranges `[1.2.0, 2.0.0)` so mixing majors fails at restore (`spec 062 §6`). A snapshot test "blesses" breaking changes when regenerated.

**48. Root generated XAML resource factories at first use.** Reactor's `RegisterControlAssembly` must run in the element's cctor before `Generic.xaml` loads and is statically preserved under trim; a missing provider was a first-realization crash `0xC000027B` (`spec 058 §9a`). CSharpUI's `UnsafeAccessor` XAML resource factories have the analogous rooting requirement.

**49. Structured diagnostics with pasteable fixes.** Every rejected name is an error with a concrete pasteable fix; the no-handler runtime throw names the cause and remediation (`spec 062 §10:604-619`). Extend the existing `ViewBuildingException`/diagnostics work in this direction.

**50. Thin MCP bridge over Avalonia DevTools primitives — do not build a second inspector.** Reactor built an in-process MCP server because WinUI had no agent-consumable surface; Avalonia already ships live visual tree, property inspector, overlays and IDE hot reload. Expose `tree`/`properties`/`screenshot`/`click`/`type`/`waitFor` over Avalonia's `Visual`/LogicalTree, `AutomationProperties` and INPC VMs, plus CSharpUI-specific tools: `generatedFactories`, `bindings` (typed expression paths → shape-only values), `sourceInfo` (`SourceInfo.cs`), `diagnostics` (existing `Diagnostics/*`) and `reload` (`src/Reactor.Devtools`, `spec 024`).

**51. Ship devtools as an optional package behind a build-time switch, with positive and negative CI controls.** `ReactorFeatures.cs` uses `[FeatureSwitchDefinition("Reactor.DevtoolsSupport")]` + `[FeatureGuard]` + a hot-path static; two-layer activation (build-time capability AND `--devtools` session opt-in); CI asserts devtypes **absent** with the switch off and **present** in a positive control (`spec 051`, `.github/workflows/ci.yml:1010-1092`). A 1-line AOT hello-world previously carried ~1.5 MB dead devtools IL, mostly reflection-mode JSON and HttpClient. CSharpUI should verify trim with an analogous mstat gate.

**52. Reuse the MCP security envelope verbatim.** Loopback only, a per-launch 256-bit bearer token in a lockfile with constant-time compare, Host/Origin allowlists, non-simple Content-Type requirement, 1 MiB body cap, 16-concurrent-dispatch cap returning 503, 10 s IO timeouts (`DevtoolsMcpServer.cs:43-70,258-348,352-537`); document "dev-loop only, no auth". All UI-touching tools marshal through the dispatcher via `OnDispatcher<T>` with `ManualResetEventSlim` and `ExceptionDispatchInfo` (`:543-582`).

**53. Stable node IDs, self-describing schemas and shape-bounded state.** Window-scoped ids `r:<window>/<local>` backed by `WeakReference<UIElement>` + reverse CWT with tombstones, never reused; selector precedence id → AutomationId → AutomationName → type+index → source, ambiguity returns matches rather than silently picking first; `GET /mcp` emits the full grammar; state inspection returns primitives or `{ $type, $shape }` and never full `DataContext` dumps (`NodeRegistry.cs:25-107`, `DevtoolsStateTool.cs`, `spec 024:238-320,372-382`). Add `call <tool>` passthrough if a CLI is added, with an explicit sunset clause for one surface (`spec 025:290-307`).

**54. Structured log capture with monotonic sequencing.** `reactor.logs` drains a ring buffer of Console/Debug/Trace + EventSource events with `seq`/`nextSeq` paging, regex filter and bounded long-poll; installed early so late-attaching agents get startup logs and preserved across reload (`DevtoolsLogsTool.cs:5-103`). Fits CSharpUI's existing `DiagnosticsErrorLog`/`BindingErrorSink`/`RuntimeErrorSink`.

**55. Frame-aligned sampling for high-frequency VM properties.** `Interlocked.Exchange(ref _pending, value)` posts only when the prior was null → most-recent-wins coalescing on the producer thread, a `framesPer:1` fast path, `IFrameClock`/`VirtualFrameClock` for tests, `_alive` flag on unmount; wall-clock Hz and element-side throttling were explicitly rejected (`spec 031`). Add an MVVM throttled-binding behavior rather than an element hook.

**56. Hot-reload phase discipline with synthetic tests.** At-most-once recovery per pass (`HookOrderException` → `ResetForHotReload`), field-by-field state migration with cycle guards and a block-list for native handles, subtree migration reusing the existing element, and an AOT gate making all reflection phases statically dead (`spec 049:67-87,129-199,203-294,298-409`). CSharpUI already has `MetadataUpdateHandler`; test with manually supplied type arrays rather than relying on IDE behavior, and tag migrated state in devtools if shape migration is added.

**57. Keep agent-driven and IDE-driven reload protocols distinct.** Devtools uses an explicit `reload` tool, sentinel exit 42 and a supervisor that pins the MCP port; the VS embedded preview uses `dotnet watch`, detects rude edits via a new `CAPTURE_TOKEN=` on stdout (not `Process.Exited`), and deliberately has no `/reload` endpoint because watch treats nonzero as a crash (`spec 024:138-140,384-406`; `spec 056:1033-1114`). Choose the supervisor first and derive the reload protocol from it.

### P2 — later (58–74)

**58. Minimal-move keyed reorder utility.** O(n log n) patience-sorted LIS into pooled buffers, right-to-left walk with a live anchor, `oldRelToPanel` kept exact across every Insert/Move, `IKeyedMiddleSink` generic struct for devirtualization (`ChildReconciler.cs:795-939,997-1062,1152-1213`). Useful only if real controls are reordered in a panel (dynamic toolbar, drag-reorder); use the cheaper prefix/map diff for virtualized lists (item 15).

**59. Pooling with a rigorous reset contract — if you pool at all.** Per-type stacks with a cap, `ForceDetach` round-trip, `CleanElement` using `ClearValue` (not assignment) so a local value cannot outrank the next renter's style, two lanes for "visual taken" controls, `PoolPolicy<T>` opt-out, analyzer-enforced mirror of the poolable-type set (`ElementPool.cs`, `PoolPolicy.cs`). `docs/review-findings.md:63` records pooling as deliberately absent — keep it absent unless measured; if added, adopt the whole contract.

**60. Attached-property state instead of a CWT where wrappers can be duplicated.** Reactor's `ReactorAttached.StateProperty` holds element pointer and event state because WinRT RCW identity is unreliable; CSharpUI's CWT is fine for Avalonia objects. Remember the distinction for cross-thread or multi-wrapper scenarios.

**61. Incremental diff hints keyed by array identity.** `ChildDiffHint.PreviousChildren` is a `WeakReference<Element[]>` specifically to avoid pinning every past render's array; engagement requires the old array to *be* the hint's previous array with a safe full-walk fallback (`ChildDiffHints.cs:20-82`). The pattern is the point (weak side channel + identity proof + fallback), not the hook that produces it.

**62. One authoritative transform channel + measured invariants.** Spec 066 unified three transform channels, made identity writes no-ops, guarded facade-vs-Visual conflicts with `UnauthorizedAccessException`, reclaimed taken visuals through the pool and recorded 10 WinUI bugs. Define one channel for any transform/animation helper and verify pooling behavior rather than accumulating aliases.

**63. Optional heavy subsystems in an opt-in package.** `Reactor.Advanced` holds Win2D, Markdown and charting so the core DLL takes no heavy dependency and an app that never names the factory pays nothing (`spec 053`, `spec 062 §7`). Keep SkiaSharp/immediate-mode canvas/charting out of CSharpUI's core package.

**64. Immediate-mode canvas escape hatch with a stable frame-state object.** `UseDrawState` returns a reference stable across renders with an explicit Volatile/Interlocked/lock cross-thread contract (`src/Reactor.Advanced/Win2D`). Expose as an MVVM service/behavior, not a hook.

**65. Keep charts/markdown in the normal visual and a11y tree.** Reactor's critical review records that imperative `canvas.Children.Clear()` hosting hurt diffing, pooling, animation, theming and accessibility; the D3 port now participates like any other subtree (`spec 016`, `spec 026`). Bind chart series to VM observables and keep automation peers.

**66. Do not port Yoga; adopt its micro-patterns.** The pure C# Yoga port is ~220 KB with 543 generated fixtures; Avalonia already has flexible layout. A thin fluent setter over existing panels buys the same ergonomics. Do adopt per-instance DP-read caching via `ConditionalWeakTable`, lazy DPI sync in `MeasureOverride`, and generated conformance-fixture discipline (`FlexPanel.cs:26-54,78-85`).

**67. Figma→code as a skill/IR pipeline, not binary features.** Layout IR + mapping tables shipped as skill files, theme-token resolution, live preview, graceful TODO markers, tiered support matrix (`spec 033`). Fits CSharpUI's generator + agent-skill direction.

**68. Source-map interceptor mechanics — watch, don't adopt now.** 189/189 factory coverage including 39 `params` factories that `[CallerFilePath]` structurally cannot reach; measured +16–22% build time and +312 B per stamped site; spec records CallerInfo's EnC staleness (10-line insert → 10-line drift) (`spec 010:139-181`). Revisit only if call-site fidelity becomes a measured problem.

**69. Don't build a privileged/ETW-dependent in-app profiler; record failed diagnostics candidly.** The ETW layout-cost overlay shipped and was then **removed** (PR #507): session creation needed admin, WinUI native task IDs drifted across runtime updates, and per-Component attribution rarely gave useful signal (`spec 032:3-21,716-855`). The general EventSource/trace bridge was retained. Build a cheap structured diagnostic layer with stable names and an additive JSON schema; write "we tried X, it failed because Y" into specs so it is not re-proposed.

**70. AOT/trim support matrix as a live CI artifact.** `docs/aot-support.md` records works / doesn't work / planned with issue links, trim-root cost measurements and canary CI jobs; AOT selftests publish the Host with `PublishAotInternal=true` and periodically re-probe stale skips because a probe that roots reflection metadata can *mask* the defect (`docs/aot-support.md:126-199`). CSharpUI's AOT story is currently one sentence in `docs/architecture.md`.

**71. Swallowed-error audit as a durable contract.** ~80 `catch (Exception){Debug.WriteLine}` sites each get a sidecar row with HRESULT-level failure modes, "what we explicitly do NOT swallow" and a verdict `Keep|Narrow|Propagate|TryXxx|PromoteEvent`; a CI gate re-derives the distribution from the rows and fails on drift (`spec 044:380-517`). Strong defense against silent failure growth in a generator codebase.

**72. Stop-shipping-both discipline.** Every experiment carries a sunset/exit criterion; superseded features get a `Superseded` or `REMOVED` banner explaining where the capability moved (`spec 025:290-307`, `spec 032:3-21`, `spec 043:3-18`). CSharpUI already did this well by deleting generated type aliases when it removed the global static import — document the decision as a precedent.

**73. Feature flags are temporary scaffolding.** `ReactorFeatureFlags` flags are deleted once the default flips and soaks — explicit anti-entropy policy. Avoid permanent compatibility aliases.

**74. Allocation reduction checklist.** Single object initializers instead of `with` chains in hot loops (9 → 4 allocations/cell, +8% renders, −60% bytes/tick), cached modifiers (Gen0 −33%, peak memory −26 MB), typed-arity overloads instead of `params object[]` (no boxing), two-pass filter with no intermediate `List`, singleton comparers, self-merge avoidance (`spec 007`, `spec 034:254-330`, `Reconciler.Update.cs:94-107`). Applies to generated factories in list loops and to setter overload design.

### P3 — process and docs (75–89)

**75. Specs as append-only design records with tasks companions.** Numbered 001–066 with `Status`, `Owner`, `Related`, goals/non-goals, resolved open questions with Q-numbers, implementation phases with exit criteria, and companion `docs/specs/tasks/*` implementation files. Several record what was wrong earlier (`007` revised priority after measurement; `032` REMOVED; `043` Superseded; `034` distinguishes estimates from measured ledgers). CSharpUI's docs are closer to a changelog than a design record — adopt this shape.

**76. Parallel specialist code-review orchestrator.** `tools/reviewer` runs five/six specialists over 91 manifest batches (safety, lifecycle, interop, security, test-quality, then general), applies a "Team Lead Test" and signal-to-noise gate, cites pattern IDs from nine domain skill catalogs (281 patterns), dedupes and severity-sorts into `fix-list.md`, then a manager approves/declines each finding and a separate pass implements approved fixes (`tools/reviewer/README.md`). Directly reproducible with this harness for the next major generator change.

**77. Docs generated from templates with source snippets, plus a doc tier lint.** `docs/_pipeline/templates/*.md.dt` imports `// <snippet:name>` markers from real source so docs cannot drift, and CI verifies compiled docs are committed (`AGENTS.md:186-188`, `.github/workflows/ci.yml:1188-1375`); three page tiers (Stub/Solid/Comprehensive) have concrete definitions of done and `mur docs compile --validate-only` gates them (`spec 041:167-220,980-1000`). Two complementary audiences: user catalog with before/after pairs vs contributor architecture with full ID table, pipeline diagram, authoring checklist and "Common Mistakes". This fixes CSharpUI's known `T7` doc drift.

**78. Agent-facing repo instructions.** `AGENTS.md` documents architecture, conventions, build/test/lint commands with MTP nuances, CI constraints and "adding a new control" steps; `SKILL.md` + `skills/*` + `plugins/reactor/skills/*` supply recipe files and workflows. CSharpUI has no equivalent — add one capturing the no-reconciler boundary and generator invariants.

**79. Multi-model review rounds for analyzer proposals, with an inline correction changelog.** `spec 060` was reviewed by five models in three rounds; the changelog records which model drove each correction, including overturned premises and fictional helpers, and §9/§12 document rejected ideas with reasons. Cheap way to avoid codifying bad rules.

**80. Empirical grounding before codifying a rule.** Corpus calibration found CS1061 fuzzy suggestions were 0/9 matches, so structural rules were prioritized instead of loosening the threshold; an alignment-shortcut rule was deleted when upstream added real aliases (`spec 038`, `spec 061:150-158`). Delete rules whose mistake the API surface has already fixed.

**81. Parity tests for duplicated logic.** Analyzer and CLI each keep their own `StringSimilarity` because `netstandard2.0` cannot use the CLI's `FrozenDictionary`; bit-identical parity tests lock the copy (`StringSimilarityParityTests`, `DidYouMeanAnalyzerParityTests`, `ColorStringParityTests`). CSharpUI's analyzer and generator are both `netstandard2.0` — document and test any duplicated helper.

**82. Benchmark artifacts and honest residual attribution.** CSharpUI's `docs/performance-review.md` already declines further caching without profile evidence — keep that discipline; Reactor's `spec 007` revised priorities after measurement and `spec 034` has a verified close-out; investigation artifacts are committed (`docs/perf-investigations/`, `docs/reports/`), not chat transcripts. Pin numbers to a commit and never claim stage numbers explain end-to-end time without inclusive profiling.

**83. Release channels + human-approved publish + pre-publish package verification.** Prerelease labels *are* channels (`0.*`, `0.*-preview*`, `0.*-nightly*`); auto-build, human-approved publish because a tag is cheap and nuget.org versions are permanent; a pre-publish `NuGet.VerifyMicrosoftPackage` step exists because a non-atomic push once half-published a preview release (`spec 057:87-105,217-258,352-394`). CSharpUI uses `.releaserc.json`; adopt the approval gate and compliance check.

**84. Bootstrap that self-verifies, plus `doctor`/`upgrade` verbs.** `bootstrap.ps1` is idempotent and verifies the installed `mur` version against HEAD via `mur --version` + git SHA; `DoctorCommand` prints PASS/WARN/FAIL with copy-pasteable remediation and distinguishes "installed but too old" from "not installed"; `upgrade` cannot replace its own running binary and says so (`bootstrap.ps1:579-619`, `src/Reactor.Cli/Doctor`, `Upgrade`). Bundle these with a future `cui` CLI.

**85. Agent kit and generated indexes as CI-gated artifacts.** `plugins/reactor/` holds 14 focused skills shipped under `agentkit/`; `skills/reactor.api.txt` is a generated 70K-token API index byte-mirrored and regenerated by `mur --regen-api` (`AGENTS.md:314-341`); search indexes and gallery snippets are checked by unit-tier lints (`TESTING.md:236-266`). Contributor skills are instructions that drive committed scripts, not executables (`.github/skills/README.md:123-135`).

**86. Prefer contributing to an existing cross-framework tool over owning a second corpus.** `mur find/get/list` were superseded by `winapp find-ui --source reactor` over a generated index (`spec 043:3-18`); generated indexes must be CI-gated against source (`AGENTS.md:326-341`). CSharpUI should plug into existing Avalonia/IDE tooling where possible rather than duplicating it.

**87. Logging channel taxonomy.** "Audience, not severity, decides the channel": `Debug.WriteLine` only for framework-builder signal; anything an app developer might need in Release goes to an EventSource; PII policy table and in-process `EventListener` subscription instead of a new file-capture API (`spec 044:15-25,230-248,554-606`). Apply before `Diagnostics` grows more sinks.

**88. Document spec-vs-shipped gaps and keep a candid critical review.** Specs openly list deferred items (`ThemeScope`, focused command routing, host-level a11y opt-in) and `docs/research/critical-review.md` calls out unfixed issues (hard-coded English `StandardCommand` labels, accelerator rebuild churn). Keep `docs/review-findings.md` current as work lands, including resolution status.

**89. Push component-layer conventions upstream where the substrate already exists.** `spec 023` concludes "Microsoft has the substrate; we lack the conventions on top" and recommends contributing most of the perf-insight vision to WinUI/BCL, with only five component-layer bullets owned by Reactor (`:204-220,222-246,358-370`); `METRIC: time to first insight` is the worthwhile measure. CSharpUI should likewise prefer Avalonia/ROSlyn conventions over framework-specific parallel systems.

---

## 2. Explicitly do NOT copy (MVU-bound)

| Reactor mechanism | Why it exists | CSharpUI stance |
|---|---|---|
| `Component.Render()` → immutable `Element` tree → reconciler mount/update | Answers "what changed since the last render?" | Avalonia bindings answer this at property granularity. `docs/architecture.md:3` already states there is no virtual tree. |
| Hooks (`UseState/UseEffect/UseMemo/UseRef/UseNavigation/UseDrawState`), call-order slots, hook-rule analyzers | State co-located with re-executed render | `TViewModel` + INPC/ReactiveUI is the state store. |
| `Context<T>` propagation / `UseContext` | Tree-local replacement for DI | DI container / attached properties / view locator. |
| `ControlDescriptor` + `DescriptorHandler` + `IElementHandler` V1 protocol | Interpreter of declarative diff-and-write rules | Generated setters write directly to real controls at call time. |
| `Optional<T>` descriptor interpreter + echo suppressor + trampolines + `ReactorState` | Programmatic writes from `Update` look like user input | Keep only *authority semantics* (item 22); bindings own two-way flow. |
| Children strategies, keyed element diff, `ElementRef` reactive graph, `CallSiteChangedOnSkip` | Stable identity across re-renders | Not needed without re-renders; keep the algorithmic lessons (items 15/58/61). |
| Ambient `Animate(...)` `AsyncLocal` transaction, delayed unmount for exit transitions, reconciler-owned visual handoff | Reconciler is the single choke point | Avalonia `Transitions` + attached behaviors + `IAnimationService`. |
| Interceptor-based source mapping | CallerInfo cannot reach `params` factories and goes stale under EnC | Keep caller-info + `SourceInfo` + `MetadataUpdateHandler`. |
| Yoga port, Win2D/Markdown/D3 element trees, docking `Content : Element` | Composition replaces data binding by design | Use existing Avalonia facilities; keep the immediate-mode *pattern* in an opt-in package. |
| Reactor's own DevTools UI / second visual-tree inspector | WinUI had no agent-consumable in-process surface | Thin MCP bridge over Avalonia DevTools primitives (item 50). |
| Module-initializer registration, eager control registrars, `[ModuleInitializer]` trim roots | Reactor rejected these itself for trim reasons | CSharpUI currently uses module initializers — measure, then consider the factory-chokepoint shape. |

---

## 3. Gap → lesson quick map (CSharpUI findings register)

| CSharpUI finding (`docs/review-findings.md`) | Reactor lesson above |
|---|---|
| G2 — no analyzers/code fixes; generator diagnostics at `Location.None` | 1–4, 7, 26–28, 79–81 |
| G5 — overload explosion / unbounded constructor overloads | 24, 74 |
| G6 — naming rules duplicated across 7+ sites | 75–77 (single naming policy + snippet-driven docs), 24 |
| G8 — weakly guarded hash-keyed caches; prior poisoning bug | 5, 10, 28, 30–31, 82 |
| G9 — publisher coverage/origin complexity | 6, 45, 47 |
| R5 — static AppBuilder config; R6 registration | 8, 22–23 |
| R7 — process-wide diagnostics buffer | 21, 54, 87 |
| R8 — fragile style-selector heuristic | 35 (token-driven styles), 47 |
| §1.3 — no navigation/state/command/validation framework | 11–14, 35–44 |
| §4.2 — no API-surface catalog | 77–78, 85 |
| §6 — module-initializer startup unmeasured | 8 |
| §7 — no test tiers / sample-backed tier / formatter test | 29–34, 76 |
| no CLI / no release or bootstrap tooling | 25, 27, 83–84 |

---

## 4. Suggested sequencing

1. **P0 sweep** (items 1–34): analyzer suite + release tracking + validation gate + silent-drop guard + command/a11y/async primitives + coalescing/skip/predicate patterns + measurement and test machinery. No change to the MVVM/no-reconciler thesis.
2. **P1 sweep** (items 35–57): theming, metadata/validation/data, localization, navigation, animation, gestures, windowing/docking, and a thin agent/MCP surface — each as a headless service, generated setter or optional package, never a hook.
3. **P2/P3** (items 58–89): only where measurement or a real scenario justifies it; keep the process/docs practices continuous from day one.

Every P0/P1 item is implementable while keeping `ViewBase<TViewModel>`, real Avalonia controls and typed expression bindings. Nothing in this document requires a virtual DOM, a reconciler, or hooks.
