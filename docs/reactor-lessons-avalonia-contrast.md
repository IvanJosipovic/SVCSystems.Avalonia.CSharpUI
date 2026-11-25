# Reactor lessons, contrasted against Avalonia

**Status:** companion to `docs/reactor-lessons-deep-review.md` (no code changed)
**Date:** 2026-11-25

The first review listed what Reactor does that CSharpUI could learn from. This document asks the more important question: **what does Avalonia already do, and what is actually missing?** The goal is not to reimplement Reactor on Avalonia. It is to find the small set of things where Reactor's engineering buys CSharpUI something Avalonia does not already provide.

## The filter

| Verdict | Meaning |
|---|---|
| **Adopt** | Genuinely net-new for an Avalonia/MVVM library; no native equivalent. |
| **Adapt** | Useful idea, but implement it *Avalonia-natively* and narrower than Reactor did. |
| **Skip** | Avalonia (or the surrounding ecosystem) already solves this, or it only exists because of re-render/diff. |

The two biggest reasons to **skip** are:

1. **Avalonia's bindings are already the fine-grained updater.** Reactor's skip predicates, dirty-ancestor paths, diff hints, echo suppression and render coalescing exist to recover the precision that bindings give you for free. Adding them would be solving a problem CSharpUI does not have.
2. **Avalonia's platform surface is broader than WinUI's.** Gesture recognizers, `ControlTheme`/`DynamicResource`, animations, virtualization, DevTools and hot reload are built in. Reactor built equivalents because WinUI lacked an agent-consumable or code-first surface. CSharpUI should build on Avalonia's primitives, not replace them.

Conversely, the things that really are missing cluster in four places: **authoring-time tooling** (analyzers, generated API ergonomics, diagnostics), **headless semantic models** (commands, field/validation metadata, async state), **agent-facing surfaces** (structured diagnostics, a thin MCP bridge), and **engineering process** (specs, measurement, mutation-checked tests).

---

## 1. P0 items (1–34), contrasted

| # | Reactor idea | Avalonia already gives you | Real gap / net-new value | Verdict |
|---|---|---|---|---|
| 1 | ~70 analyzer IDs for the DSL | Roslyn analyzers in general; no DSL-specific suite for code-first Avalonia | Generated factories/setters have near-zero compile-time guidance; only `SVCUI001` exists | **Adopt** |
| 2 | `AnalyzerReleases.*` tracked by RS2008 | Same Roslyn facility, unused | Stable ID/category/severity policy for a growing suite | **Adopt** |
| 3 | Syntactic gate → symbol check pipeline | Same Roslyn APIs | Avoid adding seconds of build time per file | **Adopt** |
| 4 | Fix restraint + `Diagnostic.Properties` | Same Roslyn facilities | Avoids the exact formatter-undo problem CSharpUI hit with generated aliases | **Adopt** |
| 5 | Silent-drop guard for generated APIs | Nothing | Generated setters skip unsupported property types with no signal | **Adopt** |
| 6 | Shared attributes in one referenced assembly | CSharpUI already binds attributes by metadata name | Verify no `CS0436` in friend-assembly publishing; add absence guard | **Adapt** |
| 7 | One analyzer per generator attribute | No equivalent | Every generator option currently fails late or silently | **Adopt** |
| 8 | Factory-cctor registration, no module initializers | CSharpUI uses `[ModuleInitializer]` + first-wins registry; Avalonia controls are ordinary objects | Only relevant to AOT/trim; measure before changing registration strategy | **Adapt** |
| 9 | Interceptor source mapping | `CallerFilePath`/`CallerLineNumber` + CSharpUI `SourceInfo`; Avalonia hot reload | `params` factories and EnC staleness are Reactor-specific problems | **Skip** (keep caller-info) |
| 10 | Generation stamps for caches/async | Normal async/await + INPC | A single rule preventing stale in-flight results in any cache/command | **Adapt** |
| 11 | `Command` record + `ICommand` adapter | `ICommand`, `KeyBindings`, `MenuItem.InputGesture`, `ToolTip.Tip`, `AutomationProperties.HelpText`, CommunityToolkit `[RelayCommand]` | No bundled metadata; same label/icon/shortcut repeated across menu, toolbar, context menu | **Adapt** — thin descriptor + generated setter, not a new command system |
| 12 | Clear stale command metadata; diff accelerators | Manual property sets | Stale tooltip/HelpText/accelerator after command changes is a real bug | **Adopt** (small) |
| 13 | A11y scanner JSON + 4 analyzers | `AutomationProperties.*`, automation peers, DevTools a11y tree, `AccessibilitySettings` | No compile-time checks and no agent-consumable structured report with context | **Adapt** — analyzers + JSON export; **skip** a second tree inspector |
| 14 | `AsyncValue<T>` + `MutationCommand` + `QueryCache` | `AsyncRelayCommand.IsRunning`, ReactiveUI `ReactiveCommand.IsExecuting`, data binding | No stale-while-revalidate state ADT or generation guards; much of the rest is ecosystem-covered | **Adapt** — ADT + guards; do not build a query-cache framework unless needed |
| 15 | Keyed-list virtualization bridge | `VirtualizingStackPanel`, `ItemsControl`, `DataGrid`, `RecyclingElementFactory`, container recycling | Immutable VM lists still break incremental notifications; stale-index/focus-pinning lessons | **Adapt** — bridge helper only if measured; keep Avalonia virtualization |
| 16 | Coalesce renders, low-priority demotion | Avalonia `Dispatcher.UIThread` + binding engine already coalesce most work | High-frequency VM property storms | **Adapt** — one dispatcher batch + `DispatcherPriority.Background` |
| 17 | One skip predicate + decline gates | Bindings already update only changed properties | No re-render pass exists to skip | **Skip** |
| 18 | Dirty-ancestor propagation | Nothing to protect — no skip optimization | Only needed if item 17 is added | **Skip** |
| 19 | ArrayPool diff buffers | Framework collections | Only needed for item 15's keyed diff | **Skip** (adopt with 15) |
| 20 | Bounded LRU/keyed memo caches | Generator caches already exist in CSharpUI | Cache converted values (Brush/Geometry) with bounded windows | **Adapt** |
| 21 | Bucketed metadata / `EqualityIgnored` | `SourceInfo` already uses a `ConditionalWeakTable` | Debug metadata must stay off the hot path | **Adapt** |
| 22 | `Optional<T>` authority + `ClearValue` | `BindingMode`, `FallbackValue`, `TargetNullValue`, Avalonia's precedence chain | The clobbering bug is a re-render problem; bindings own two-way flow | **Skip** the machinery; keep the *meaning* in binding generation |
| 23 | Echo suppression via attached state | Avalonia bindings handle TwoWay writes; no reconciler writes into owned controls | No echo source exists | **Skip** |
| 24 | Overload ladders, aliases, `@` escaping, `.Set(Action<T>)` | C# itself; existing generated setters | Compile-time ergonomics of the generated DSL | **Adopt** |
| 25 | `mur check` CLI | Analyzers + `dotnet build` | A CLI is a product decision for a library; value is real but larger scope | **Adapt** — start with analyzers; add CLI only if shipping tooling |
| 26 | "Wrong is worse than silent" validation gate | Nothing | Prevents bad did-you-mean rules before they ship | **Adopt** (when 25 exists) |
| 27 | Eval-trace mining with real agent failures | Nothing | Needs a CLI + agent corpus first | **Skip** for now |
| 28 | Run analyzers against samples | Samples compile in CI | Cheap gate against over-fit rules | **Adopt** |
| 29 | TAP fixtures, sharding, three-state verdict | Avalonia.Headless + xUnit/MTP | Cross-tier fixture reuse and anti-vacuity rules are genuinely missing | **Adapt** — take the verdict/wait rules, not necessarily TAP |
| 30 | Mutation-checked oracles, diff ledgers | Snapshot tests in CSharpUI | Regenerated snapshots bless breaking changes | **Adopt** |
| 31 | "Checks that actually prove something" guide | Nothing | Directly applicable to generator tests | **Adopt** |
| 32 | Interleaved A/B + paired CI | BenchmarkDotNet harness already present | No macro view-build/bind workload; no paired CI | **Adapt** |
| 33 | Written perf methodology with "Don'ts" | `docs/performance-review.md` covers generator only | No methodology for runtime/view-build measurement | **Adapt** — ETW-free counters for Avalonia |
| 34 | Real-build smoke + sample-backed compilation | CI compiles samples; no test asserts it | Packaging/generator-host regressions | **Adopt** |

---

## 2. P1 items (35–57), contrasted

| # | Reactor idea | Avalonia already gives you | Real gap / net-new value | Verdict |
|---|---|---|---|---|
| 35 | `ThemeRef` + resolution/style caches | `ResourceDictionary`, `DynamicResource`/`StaticResource`, `ControlTheme`, `ThemeVariant`, `RequestedThemeVariant` | Typed C# token names in generated setters | **Adapt** — map tokens to `DynamicResource`; **skip** Reactor's manual resolution cache (Avalonia self-heals) |
| 36 | `FieldDescriptor` metadata spine | `DataGrid` columns, DataAnnotations | No shared metadata across form / property grid / grid | **Adopt** |
| 37 | Headless `DataGridState<T>` | DataGrid has sorting/selection; VMs bind directly | A headless, unit-testable state machine for selection/sort/edit/optimistic commits | **Adapt** — don't duplicate built-in sorting; add what's missing |
| 38 | `IDataSource`/`RowKey`/`DataPage` | Repository/service pattern is standard MVVM | Serializable descriptors + capability flags for server push-down | **Adapt** — useful as a service contract, not a framework |
| 39 | Validation with generation guards | `INotifyDataErrorInfo`, DataAnnotations, ReactiveUI validation | Stale async validation results published after newer values | **Adapt** — generation guards only |
| 40 | ICU localization + typed keys + CLI | `Language`, `FlowDirection`, community localization packages | Typed message keys and ICU pluralization | **Adapt narrowly** — generator for typed keys; **skip** building a translation CLI |
| 41 | Typed routes + guards + state serialization | ReactiveUI `RoutedViewHost`/`ViewLocator`, Avalonia navigation controls | Route records as data, cancellable guards, deep-link serialization | **Adapt** — MVVM service; skip hook/navigation-host machinery |
| 42 | Curve/Easing records, trigger keyframes | `Animation`, `KeyFrame`, `Transitions`, `RenderTransform` | Immutable animation vocabulary and trigger-keyed replay | **Adapt** — generated setters/behaviors; skip compositor-specific APIs |
| 43 | Gesture records + attach-once trampoline | `GestureRecognizers` (tap/pinch/pan), pointer events, drag-drop | Almost nothing | **Skip** |
| 44 | WindowSpec + docking algebra | `Window`/`TopLevel`, `WindowingPlatform`; docking is third-party | Layout-as-model is a good idea but out of CSharpUI's scope | **Skip** for core; revisit only as a separate package |
| 45 | Partial-fill / attach generation | C# partial types; existing generator publishes extensions | Library authors can't override individual generated members | **Adopt** |
| 46 | Name-mapping attributes | CSharpUI has `Alias`/`Ignore`/`GenerateFactory` | Add `Convert`, `Content`, event-args projection | **Adopt** |
| 47 | Binary compatibility gates | `PublicApiAnalyzers`/`ApiCompat` available, unused | Generated-code surface must stay additive within a major | **Adopt** |
| 48 | Root generated XAML factories | CSharpUI's `UnsafeAccessor` factories already exist | Rooting guarantee at first use | **Adapt** — add trim tests/documentation |
| 49 | Structured diagnostics with pasteable fixes | Runtime exceptions + diagnostics log | Actionable, location-bearing messages | **Adopt** |
| 50 | In-process MCP server | Avalonia DevTools (tree, properties, overlays, hot reload) | Agent-consumable, self-describing surface over CSharpUI-specific data | **Adapt** — thin bridge only: `generatedFactories`, `bindings`, `sourceInfo`, `diagnostics` |
| 51 | Devtools behind build-time switch | Feature switches available | Keeping devtools IL out of AOT/retail output | **Adopt** if 50 is built, with negative + positive trim controls |
| 52 | MCP security envelope | None (new surface) | Loopback + token + origin allowlist if 50 is built | **Adopt** with 50 |
| 53 | Stable node IDs, self-describing schema | Avalonia DevTools has its own IDs | Only needed if 50 is built | **Adapt** with 50 |
| 54 | Structured log capture with seq paging | Existing `DiagnosticsErrorLog`/sinks | Agent-readable paging | **Adapt** |
| 55 | Frame-aligned producer-thread sampling | Avalonia render loop + dispatcher | High-frequency telemetry/property sources | **Adapt narrowly** |
| 56 | Hot-reload phase discipline | Avalonia hot reload + CSharpUI `MetadataUpdateHandler` | Reactor's hook-state migration has no MVVM analogue | **Skip** |
| 57 | Agent vs IDE reload protocols | IDE tooling already owns reload | Reactor-specific supervisor problem | **Skip** |

---

## 3. P2 items (58–74), contrasted

| # | Reactor idea | Avalonia already gives you | Real gap / net-new value | Verdict |
|---|---|---|---|---|
| 58 | LIS minimal-move reorder | Panel children can be reordered imperatively | Only for a dynamic toolbar/reorder helper | **Skip** unless a concrete scenario appears |
| 59 | Element pooling with reset contract | Avalonia virtualization recycles containers; CSharpUI deliberately has no pooling | None currently | **Skip** |
| 60 | Attached state instead of CWT | Avalonia objects have stable identity | None | **Skip** |
| 61 | Diff hints keyed by array identity | No virtual tree | None | **Skip** |
| 62 | One authoritative transform channel | `RenderTransform`/`LayoutTransform` | Avoiding conflicting transform APIs in generated setters | **Adapt** as an API rule |
| 63 | Heavy features in opt-in package | NuGet packaging; SkiaSharp available | Keep GPU/charting deps out of core | **Adopt** |
| 64 | Immediate-mode canvas escape hatch | `DrawingContext`, `WriteableBitmap`, SkiaSharp | No need for Win2D hooks | **Adapt** — a behavior/service, not a hook |
| 65 | Charts/markdown in normal visual + a11y tree | Avalonia controls and automation peers | Keep custom controls participating in automation | **Adapt** — principle only |
| 66 | Yoga flex port | `StackPanel`, `DockPanel`, `Grid`, `WrapPanel` | Ergonomics, not layout capability | **Skip** the engine; **adapt** fluent setters if wanted |
| 67 | Figma→code skill/IR | Not an Avalonia concern | Optional future tooling | **Skip** for now |
| 68 | Interceptor source maps | Caller-info already works for CSharpUI | None | **Skip** |
| 69 | Don't build a privileged ETW profiler | `dotnet-trace`, BenchmarkDotNet, existing generator harness | Negative lesson worth recording | **Adopt** as a written rule |
| 70 | AOT/trim support matrix | Avalonia NativeAOT is a real deployment concern | CSharpUI's story is one sentence in `architecture.md` | **Adopt** |
| 71 | Swallowed-error audit | Fewer catch sites in CSharpUI | Lower priority but cheap discipline | **Adapt** |
| 72 | Stop-shipping-both / sunset banners | Nothing | Prevents permanent compatibility aliases | **Adopt** |
| 73 | Feature flags are temporary | Nothing | Anti-entropy policy | **Adopt** |
| 74 | Allocation reduction checklist | Generator benchmarks already present | Applies to generated factories in loops | **Adapt** |

---

## 4. P3 items (75–89), contrasted

All of these are **repo engineering practices**, not Avalonia features. Avalonia neither provides nor prevents them; they are cheap and independent of the UI stack. Verdict: **Adopt** for 75–82, 84–85, 87–89; **Adapt** for 83 (CSharpUI uses `.releaserc.json`, so add the approval gate and package-compliance check to the existing pipeline rather than importing Reactor's release runbook wholesale).

The one item to reconsider is **86** ("prefer contributing to an existing cross-framework tool"): for CSharpUI the equivalent is to plug into **Avalonia/IDE tooling** rather than build parallel search or preview surfaces. **Adopt** in that form.

---

## 5. What survives the Avalonia filter

After removing everything Avalonia already solves, the net-new work is much smaller than 89 items:

**Tier 1 — genuinely missing and cheap (do first)**
- Analyzer suite + release tracking + pipeline discipline + silent-drop guard + attribute-site validators (1–7, 28, 31).
- Generated API ergonomics: overload ladders, aliases, `@` escaping, `.Set(...)` escape hatch, name-mapping attributes (24, 45–46).
- Diagnostics: pasteable fixes, `Location`-bearing generator diagnostics, structured messages (4, 49).
- Test/measurement machinery: mutation-checked oracles, declared diff ledgers, real-build smoke, per-phase counters (30, 32–34).

**Tier 2 — real semantic gaps, keep narrow**
- Thin command descriptor + `ICommand` bridge + stale-metadata cleanup (11–12).
- Accessibility analyzers + JSON report; no second inspector (13).
- `AsyncValue<T>` ADT + generation guards; let ReactiveUI/CommunityToolkit own command execution (10, 14).
- `FieldDescriptor` + validation generation guards (36, 39).
- Headless grid state, but only for what Avalonia's DataGrid doesn't already do (37).
- Partial-fill generation, binary-compat gates, AOT matrix (45, 47, 70).

**Tier 3 — only if a concrete scenario demands it**
- Keyed collection bridge for immutable VM lists (15).
- Thin MCP bridge over Avalonia DevTools, with full security envelope (50–54).
- Localization typed-key generator (40), navigation typed routes (41), animation vocabulary (42).
- Everything in P2 that is not process (58–68).

**Explicitly not building:** virtual DOM, reconciler, hooks, skip predicates, dirty paths, diff hints, echo suppression, interceptor source maps, Yoga, gesture framework, docking framework, element pooling, a second DevTools inspector, a translation CLI, or a reload supervisor.

That is the practical difference between "learn from Reactor" and "copy Reactor": almost all of Reactor's runtime complexity disappears, and what remains is tooling, headless models, and engineering discipline.

---

## 6. P0 implementation status (2026-11-25)

| # | Status | What landed |
|---|---|---|
| 1 | Done | Analyzer suite expanded from `SVCUI001` to three rules: `SVCUI002` mistyped factory name (fuzzy, strictly gated, with code fix) and `SVCUI003` event subscription inside `Build`. 12 new analyzer test cases. |
| 2 | Done | `AnalyzerReleases.Unshipped.md` tracks all three IDs. |
| 3 | Done | New analyzers use syntactic gate → symbol check → report; the fuzzy rule resolves the factory set once per compilation. |
| 4 | Done | Fix data flows through `Diagnostic.Properties`; the code fix emits `global::`-qualified syntax with `Simplifier.Annotation`; argument-shape-style findings remain message-only by design. |
| 5 | Partial | Silent-drop guard checks four hard-coded binding-valued properties and self-validates the lower bound; deriving coverage from the generator's unsupported-type skip rule remains future work. |
| 6 | Done | Attribute packaging verified by the existing absence guards; no `CS0436` in friend assemblies. |
| 7 | Partial | Three analyzers exist, but a dedicated validator per generator attribute is still future work. |
| 8 | Deferred | Requires measuring module-initializer startup and a publish-trim test before changing registration. |
| 9 | Skip | Interceptors deliberately not adopted; caller-info + `SourceInfo` retained. |
| 10 | Partial | The "never publish a stale in-flight result" rule is documented in `docs/perf-experiments.md`; generation stamps await a real caching scenario. |
| 11–12 | Done | `CommandDescriptor` + `DescriptorCommand` bridge to `ICommand`; generated-style `.Command(...)` setters for `Button`/`MenuItem` clear stale tooltip/help/gesture metadata. |
| 13 | Partial | `A11yDiagnostic`, `A11yReport.ToJson` (Utf8JsonWriter, AOT-safe) and `A11yScanner` over the live visual tree exist; no second inspector was built. The four accessibility analyzers are not implemented. |
| 14 | Done | `AsyncValue<T>` with four states and `Match`; generation guards documented for future async services. No query-cache framework was added. |
| 15 | Done | `KeyedCollectionBridge<T>` applies immutable lists to an `ObservableCollection<T>` by key, preserving instance identity and emitting `Move`. |
| 16 | Done | `ChangeCoalescer` collapses notification storms into one `Dispatcher.UIThread` pass at background priority. |
| 17–19, 22–23 | Skip | No virtual tree, no re-render pass, no echo source — Avalonia's bindings own this. |
| 20–21 | Already present | Generator caches and `SourceInfo`'s `ConditionalWeakTable` already cover these. |
| 24 | Done | Constructor-overload emission now escapes C# keyword parameter names; the existing 8-overload cap and `Thickness` ladder remain. |
| 25–27 | Deferred | A CLI is conditional on shipping tooling. The contrast doc says "start with analyzers; add CLI only if shipping tooling" — the analyzers are the prerequisite and are now in place. |
| 28 | Done | PR workflow adds an `analyzer-gate` job building the solution with `-warnaserror:SVCUI001,SVCUI002,SVCUI003`. |
| 29–31 | Partial/done | `docs/testing.md` now carries the anti-vacuity rules, mutation-check guidance and diff-ledger policy; TAP fixtures/sharding are not needed for the current suite. |
| 32–33 | Doc done | `docs/perf-experiments.md` defines interleaved paired-CI comparison, per-phase layers and an explicit "Don'ts" list; per-phase counters remain future work. |
| 34 | Partial | The analyzer-gate job builds the full solution and the three samples, but does not pack or inspect package contents. |

**Verification:** `dotnet build SVCSystems.Avalonia.CSharpUI.slnx` clean; `dotnet test` 350/350 passing (net10.0 + net11.0); build with `-warnaserror:SVCUI001,SVCUI002,SVCUI003` clean.
