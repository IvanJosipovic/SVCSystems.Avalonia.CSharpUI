# SVCSystems.Avalonia.CSharpUI — deep design and implementation review

**Status:** review report (no code changed)
**Date:** 2026-11-25
**Scope:** `SVCSystems.Avalonia.CSharpUI` at `2f8596f` (`v1.0.0-alpha.11`), with a dedicated section on transferable lessons from `microsoft-ui-reactor` at `8ed5346f`.
**Method:** five parallel deep reviews (generator internals, emitted API surface, runtime/MVVM architecture, Reactor design lessons, tests/docs/tooling), plus direct re-reading of every critical site. Paths and line numbers refer to those revisions.

> **Note on balance.** The generated-factory naming/global-import issue is real and important, but it is **one finding (G1)** in this report. The review below covers architecture, authoring model, runtime correctness, generator design, emitted API ergonomics, caching, performance, diagnostics, tests, tooling, and long-term maintainability.

---

## 0. Verdict

CSharpUI is a coherent, well-engineered **C# markup layer for real Avalonia controls**. It is explicitly not a React-style framework: `docs/architecture.md:3` states that it *"does not build a virtual tree or reconcile controls after each update"*, `CHANGELOG.md:10` records removal of ReactiveUI/MVU APIs, and no hooks, render loop, state container, or ViewModel generator exists. MVVM is preserved by design: `ViewBase<TViewModel>` maps to `DataContext`, ViewModels stay ordinary `INotifyPropertyChanged` classes, and reactivity comes from Avalonia's own compiled bindings. **That architectural choice is correct and should not be traded away.**

The generator is the most sophisticated part of the project: usage-driven control selection with base-type closure, publisher-based cross-assembly sharing, equatable incremental outputs, layered caches, XML documentation propagation, and measured performance work. The runtime is a thin helper layer with thoughtful build-error diagnostics, side-band source metadata, and working hot reload.

The main weaknesses are:

1. **Name-space injection** (`global using static` + generated aliases) — breaks tooling that does not reproduce the generated compilation (G1).
2. **Diagnostics without locations and no analyzers/code fixes** — errors are hard to act on (G2).
3. **Runtime global mutable state and lifecycle edge cases** in `AppBuilderExtensions`, `ViewFactoryRegistry`, and `ViewBase.Reload` (R1-R4).
4. **A large, weakly guarded cache surface** with a real prior cache-poisoning bug and no regression tests for it (G5).
5. **Erosion of design discipline** — docs drift, no format regression test, benchmark artifacts not in version control, and a major performance commit with zero new tests (T1-T5).

None of these require changing the MVVM/no-reconciler model.

---

## 1. Product and architectural thesis

### 1.1 What the project is

`src/SVCSystems.Avalonia.CSharpUI/SVCSystems.Avalonia.CSharpUI.csproj:11` describes it as *"Fluent, XAML-free UI composition library for Avalonia in pure C#"*. Consumers write C# builder chains; the generator synthesizes factories and named extension overloads; Avalonia owns controls, the property system, bindings, styles, layout, and routed events.

```csharp
public sealed class GreetingView(GreetingViewModel vm) : ViewBase<GreetingViewModel>(vm)
{
    protected override object Build(GreetingViewModel vm) =>
        StackPanel().Margin(24).Spacing(12).Children([
            TextBlock().FontSize(22).Text(vm, m => m.Greeting),
            TextBox().PlaceholderText("Type a greeting")
                     .Text(vm, m => m.Greeting, BindingMode.TwoWay)
        ]);
}
```
(`README.md:21-36`)

### 1.2 Project and dependency structure

| Project | Role | Notes |
|---|---|---|
| `src/SVCSystems.Avalonia.CSharpUI` | Runtime library | `net10.0`/`net11.0`, `IsAotCompatible=true` (`csproj:4,24`) |
| `src/SVCSystems.Avalonia.CSharpUI.SourceGenerator` | Roslyn generator | `netstandard2.0`, packed into `analyzers/dotnet/cs` (`csproj:4`; runtime `csproj:42`) |
| `tests/SVCSystems.Avalonia.CSharpUI.Tests` | xUnit v3/MTP tests | `EmitCompilerGeneratedFiles=true` |
| `tests/...Tests.XamlFixtures` | XAML resource fixtures | no tests, only assets |
| `samples/*` | Basic, MVVM, CustomControls | compiled by CI, referenced by no test project |

The generator targets `netstandard2.0` and therefore cannot reference Avalonia. The curated common-control list is bridged through metadata: `[assembly: GenerateMarkupControl(typeof(...))]` in the runtime library, read by the generator (`GenerateMarkupControlAttribute.cs:5-9`, `CommonControls.cs:5-57`). This is a sound constraint boundary.

### 1.3 What is deliberately absent

No navigation/router, no state container, no ViewModel generation, no XAML for views, no reconciler, no control pooling, no echo suppression, no interceptor-based source mapping. The only sanctioned XAML touchpoint is generating typed factories for compiled XAML resources from a referenced library (`README.md:65-81`). These absences are consistent with the stated thesis; they should be documented as non-goals rather than treated as gaps.

### 1.4 Assessment

The layering is clean: generator emits *names* over runtime *helpers*; runtime helpers call Avalonia APIs. There is no parallel UI framework. This is the right shape for "Reactor for Avalonia without React". The remainder of this review evaluates how well each layer executes that idea.

---

## 2. Authoring model and runtime deep review

### 2.1 View lifecycle (`ViewBase`)

`ViewBase` derives from `Avalonia.Controls.Decorator` and implements `IReloadable` and `IDeclarativeViewBase` (`ViewBase.cs:76`). `ViewBase<TViewModel>` maps `ViewModel` to `DataContext` and supports two initialization strategies (`ViewBase.cs:26-71`):

- Constructor injection: sets `DataContext` and calls `Initialize()` immediately.
- Parameterless construction: waits for `OnDataContextChanged` with a compatible VM.

`Initialize()` (`ViewBase.cs:133-179`) is guarded by `_isInitialized`/`_isInitializing`, enters a `SourceInfo` view scope via `AsyncLocal`, sets a `NameScope`, applies `BuildStyles()`, calls `Build()`, assigns `Child`, and fires `ViewInitialized`/`OnAfterInitialized`. Exceptions are wrapped in `ViewBuildingException` with root-cause extraction and a hint for binary-compatibility failures (`:160-205`).

**Positive:** build failures carry file/line because every generated setter passes `[CallerFilePath]`/`[CallerLineNumber]` into `_set`/`_setCompiledBinding` (`ControlPropertyExtensions.cs:19-40,45-77,142-165`), which records into `DiagnosticsErrorLog` at one central site (`:186-191`). This is genuinely useful diagnostics engineering.

**Issues:**

- **R1 — Silent `Child` mismatch.** `Child = content as Control` (`ViewBase.cs:153-154`) silently yields `null` when `Build()` returns a non-`Control`, despite the `object` return type. A type error at compile time becomes an invisible empty view at runtime.
- **R2 — Property access with side effects.** The `Child` getter calls `EnsureInitialized()` (`ViewBase.cs:210-217`), so merely reading the property can construct the entire view tree. This is surprising in debuggers, inspectors, and diagnostics tooling.
- **R3 — Fire-and-forget reload.** `Reload()` calls `Dispatcher.UIThread.InvokeAsync(...)` without awaiting or observing the task (`ViewBase.cs:221-248`). Failures inside reload are unobserved, and ordering with a second `Reload()` is unspecified. It also calls both `SetValue(ChildProperty, null)` and `VisualChildren.Clear()` (`:226-227`); the duplication may be intentional but is unexplained.
- **R4 — Reload state machine.** `Reload()` resets `_isInitialized`/`_isInitializing` from outside `Initialize()` (`:239-240`) while `Initialize()` uses the same flags for re-entrancy protection. A reload triggered during initialization can interleave.

### 2.2 Global configuration and control creation

`AppBuilderExtensions` stores configuration in **static mutable fields** — `ControlFactory`, `_serviceProvider`, `_defaultViewInitializationStrategy` (`AppBuilderExtensions.cs:11-13`) — even though the API is phrased as `AppBuilder` extension methods (`:31-80`). Multiple `AppBuilder` instances in one process (tests, embedded apps, multi-window scenarios) share and overwrite this state, making configuration order-dependent. `HotReloadManager.IsEnabled` is another process-wide static (`HotReloadManager.cs:18-24`). **R5.**

`ViewFactory.Create<T>` resolution order is DI-friendly (`ViewFactory.cs:15-26`): custom `ControlFactory` → `IServiceProvider` → generated registry → `Activator.CreateInstance<T>()` (with a `DynamicallyAccessedMembers` annotation for trimming). The registry, however, is **last-wins**: `Factories[typeof(TControl)] = () => factory();` (`ViewFactoryRegistry.cs:18-23`) overwrites any prior registration with no diagnostic. Reactor's analogous registry is deliberately first-wins/idempotent to avoid nondeterministic behavior. With module-initializer registrations emitted from multiple assemblies, last-wins makes the winner depend on initialization order. **R6.** The wrapper `() => factory()` also allocates a closure per registration where a direct delegate would suffice.

### 2.3 Source metadata and diagnostics

`SourceInfo` stores `ControlSourceInfo` in a `ConditionalWeakTable<AvaloniaObject, SourceInfoBox>` with an `AsyncLocal` view-type scope (`SourceInfo.cs:13-63`). Factories and setters stamp caller file/line. This gives call-site identity without interceptors and does not alter Avalonia behavior — a good MVVM-friendly design. **Positive.**

`DiagnosticsErrorLog` is a process-wide locked ring buffer (capacity 200) (`DiagnosticsErrorLog.cs:8-22,43-101`), populated only on exceptional paths. `ComponentRegistry`/`ComponentSourceLocator`/`PropertySourceInspector` provide control→view→source mapping for tooling. The gap is that diagnostics are not scoped per app/view and have no correlation identifiers, so concurrent failures in multiple views are hard to attribute. **R7.**

### 2.4 Bindings, styles, templates, commands

Binding helpers are hand-written in the runtime and generated overloads are thin named conveniences:

- `_setCompiledBinding` with explicit source and DataContext-derived forms (`ControlPropertyExtensions.cs:47-124`); `BindValue` for any `BindingBase` (`:142-165`); style equivalents (`StylePropertyExtensions.cs:77-127`); `BindClass` (`:632-641`).
- `DeclarativeBindingAutoConverter` supplies automatic primitive/enum conversion with an explicit-converter escape hatch (`DeclarativeBindingAutoConverter.cs:12-60`).
- Data templates use `FuncDataTemplate<T>` / `FuncTreeDataTemplate<T>` via `ItemTemplate<TItem>` (`ControlPropertyExtensions.cs:430-436`).
- Commands are ordinary `ICommand` properties bound with generated setters; no command framework exists.
- `Style<TControl>` adds `.OfType<TControl>()` unless the selector has an explicit root or traversal (`Styles/Style.cs:28-58`).

**Issues:**

- **R8 — Fragile selector heuristic.** `hasExplicitRootType` is `char.IsLetter(selectorString[0])` and traversal detection searches for `' '`, `'>'`, `'/'` plus hard-coded source substrings (`.Descendant(`, `.Child(`, `.Template(`) (`Style.cs:33-44`). Any selector form not covered by those string probes is silently rebound to `OfType<TControl>()`. `selectorFunc(null!)` also assumes every selector function tolerates a null root.
- **R9 — Inconsistent converter error path.** Control setters create the effective converter *inside* the `try` (`ControlPropertyExtensions.cs:60,107`), but `_addSetterCompiledBinding` calls `DeclarativeBindingAutoConverter.GetEffectiveConverter` *outside* `ExecuteStyleAction` (`StylePropertyExtensions.cs:88,115`). Conversion failures in styles bypass the file/line wrapping that control-setter failures get.
- **R10 — `default(TViewModel)!` as a type token.** Style bindings pass a dummy instance to select the source-relative overload (`StyleBindingTests.cs:37-39`). It works but is opaque; a generic type-argument helper would be clearer.

### 2.5 Hot reload

`HotReloadManager` is a `[MetadataUpdateHandler]` that tracks `IReloadable` instances in `ConcurrentDictionary<Type, CWT<...>>` and calls `Reload()` on changed types (`HotReloadManager.cs:8,14,16,29-57,86-100`). `ViewBase` registers/unregisters on visual-tree attach/detach (`:254-265`) and clears only styles it contributed on reload (`:229-236`) — a careful detail.

This is stronger than Reactor's devtools source map for MVVM scenarios. Keep it. The only caveat is R3/R4 above and that `Activate()` is effectively always on by default (`HotReloadManager.cs:18`), so tracking cost is paid even in Release unless explicitly disabled.

---

## 3. Generator architecture deep review

### 3.1 Pipeline

`Generator.Initialize` registers three pipelines and one post-init source (`Generator.cs:15-30`): `UsageDrivenMarkupGenerator` (main), `ViewExtensionGenerator` (property + event for declarative views, one class-declaration pass), `XamlResourceGenerator`, and two post-init global usings.

`UsageDrivenMarkupGenerator.Initialize` (`UsageDrivenMarkupGenerator.cs:180-214`) captures `SimpleNameSyntax` identifiers via a cheap syntax transform (`UsageNameProvider.cs:15-22`), collects them, combines with `CompilationProvider`, maps to an equatable `GenerationResult`, attaches a comparer and tracking name, and registers one source output. Roslyn symbols and `ControlModel`s are deliberately excluded from incremental values (`docs/usage-driven-generation.md:73`); `TypeKey` is a compilation-independent `(assembly, type)` pair (`UsageResult.cs:7-66`).

**Positive:** this is a correctly structured incremental generator. Unrelated edits do not re-add generated files; the design is documented and measured (`docs/usage-driven-generation.md:83-87`).

### 3.2 Usage resolution

`ControlCatalog` indexes local classes (including internal and generic) and public non-generic classes from assemblies referencing Avalonia (`ControlCatalog.cs:79-84,121-126`), indexing each control by both its factory alias and CLR name (`:279-286`). `CommonControls` supplies curated roots. `UsageResolver.ResolveCore` (`:102-177`) adds common controls, all local controls, named controls from source, attached-property hosts, and the full base chain up to `AvaloniaObject` (`:179-206`). Ambiguous names include **all** distinct candidates and report `SVCUIGEN006` (`:154-168`).

**Issue G3 — blind spots.** Controls used only from compiled XAML in a referenced library, reflection, or runtime-computed names are invisible; users must add `[assembly: GenerateMarkupControl(typeof(X))]` (`docs/usage-driven-generation.md:66,147`). The legacy "generate everything" escape hatch was removed (`:12-16`). This is a reasonable trade-off for build time but needs a clearer diagnostic when a bare factory call fails to bind.

### 3.3 Emission to consumer compilations

Checked-in generated output (`tests/.../obj/Debug/net11.0/generated/...`) contains **185 files, 3,314 `public static` members, 81 factory methods, 81 registration classes, and 43 injected global aliases**.

- Factories: `internal static class GeneratedControlFactories` with one parameterless overload per target; body `new T()` + `SourceInfo.Set` (`UsageDrivenMarkupGenerator.cs:422-477`).
- Registrations: one `[ModuleInitializer]` per eligible control calling `ViewFactoryRegistry.Register<T>(static () => new T())` (`:479-509`). Eligibility is `GenerateFactory && IsConstructible && IsOrInheritsFrom("Avalonia.Controls.Control")` (`:511-519`).
- Global usings: two post-init namespace imports (`Generator.cs:22-27`) and the usage-driven `global using static` + aliases (`:391-420`).

**G1 — global static import and alias patch-up.** This is the issue that triggered the formatter failure. It is a genuine structural defect: the generator injects names into every consumer, then injects aliases to repair shadowing, then emits `SVCUIGEN007` (`:162-169,403-409`) telling users to qualify names the formatter subsequently simplifies away. The entire mechanism is localized to two emission sites and `TypeAliasResolver.cs` (~500 lines, five caches). Removing the static import removes the need for aliases, `SVCUIGEN007`, and most of that complexity. Namespace-level global usings can stay (they do not create method-group/type shadowing), but the post-init file should carry `// <auto-generated />` like every other generated source.

**G4 — silent factory loss on name collision.** `GetFactoryTargets` groups by cleaned factory name and keeps only groups of size one (`:521-527`). If two controls share a name, neither gets a factory; registrations still emit (`:516-519`). Only an Info-level `SVCUIGEN006` hints at this. Users see a missing factory with no actionable location.

### 3.4 Fluent extension generation

`ExternalGeneratorHost` defines four emission groups — Properties, Attached Properties, Events, Styles (`ExternalGeneratorHost.cs:18-51`) — emitting one `internal static partial class {Type}_MarkupExtensions` (or `public` for publishers) with `[GeneratedCode]`/`[ExcludeFromCodeCoverage]` and five ordinary file-level usings (`:188-218`). Generated type names are `global::`-qualified via `SymbolUtilities.GetFullTypeName()` (`SymbolUtilities.cs:31-41,246-257`). Overlap suppression skips value/expression/event setters for `ViewBase` subclasses because declarative-view generators already emit them (`ExtensionGeneratorBase.cs:31-36`, `ExtensionGroupGenerator.cs:29-32`, `UsageDrivenMarkupGenerator.cs:347-352`).

**G5 — overload explosion.** `TextBox` generates 222 public static members. `ValueOverloadsSetterGenerator` emits one overload per non-parameterless constructor of a value-type property, guarded only to non-`System` value types (`ValueOverloadsSetterGenerator.cs:12-25`, `ConstructorOverloads.cs:21-46`). That list is unbounded and duplicated in the style generator. It increases compile time, IntelliSense noise, and ambiguity surface.

**G6 — divided naming rules.** Identifier construction is implemented separately in `SymbolUtilities.BuildTypeIdentifier/BuildExtensionClassName`, `AvaloniaPropertyExtensionsGenerator.BuildExtensionClassName`, `AvaloniaEventExtensionsGenerator`'s `safeId`, `AttachedPropertyExtensionInfo`'s `Host_` prefix, `ControlModelBuilder`'s factory name, `UsageDrivenMarkupGenerator`'s registration/hint names, and `XamlResourceGenerator`'s name validation. This is a refactor with high payoff for collision handling.

**G7 — two event generators.** `ActionToEventGenerator` handles routed events, `RoutingStrategies`, and non-void delegates (`ActionToEventGenerator.cs:25-79`). `AvaloniaEventExtensionsGenerator` branches on syntax strings and falls back to `Action action` / `action()` (`AvaloniaEventExtensionsGenerator.cs:123-168`). They can generate different signatures for the same logical event. The runtime's `_setEvent` helper (`CommonExtensions/ControlEventExtensions.cs:17`) is unused. These paths should be unified.

### 3.5 Diagnostics

Eight generator diagnostics exist (`SVCUIGEN002`–`008`; `AnalyzerReleases.Unshipped.md:8-14`, descriptors at `UsageDrivenMarkupGenerator.cs:144-178` and `XamlResourceGenerator.cs:25-38`). **G2 — all are reported at `Location.None`** (`UsageDrivenMarkupGenerator.cs:200-212`), and there is no `DiagnosticAnalyzer` or code fix in `src/`. Users get prose without a clickable site or an actionable fix. This is a larger usability problem than the specific wording of `SVCUIGEN007`.

### 3.6 Caching and incrementality

Recent work (`2f8596f`, +3247/−875 across 48 files) added `GenerationCache`, a shared view-extension pass, alias/occurrence caches, and roughly 26% faster clean builds / 45% faster incremental builds (`docs/performance-review.md:136-139`). Caches include:

| Cache | Key / eviction | Evidence |
|---|---|---|
| `GenerationCache` | fingerprint (references + token hashes); max 4, cleared wholesale | `GenerationCache.cs:207-225` |
| `ResultCache`, `TypeAliasBindingCache` | hash keys; max 8 / 512 | `TypeAliasResolver.cs:173-176,487-490` |
| `MetadataByReferences`, `CoverageCache`, `XamlGenerationCache` | reference hash; max 8 each | agent report §6 |
| `FactoryMethodCache`, `RegistrationCache`, `ModelCache`, `CodeCache` | `ConditionalWeakTable` keyed by symbol | `UsageDrivenMarkupGenerator.cs:138-139`, `ControlModelBuilder.cs:19-35` |

Non-PE references are deliberately given unique keys to prevent cross-metadata reuse (`GenerationCache.cs:101-107`), and `XamlResourceGenerator.ComputeKey` includes the full fingerprint for correctness (`:241-247`). `docs/performance-review.md:197-198` explicitly declines further caching without profile evidence — good discipline.

**G8 — weak correctness guard.** Several process-wide hash-keyed caches exist; commit `d431cc6` already fixed a real cache-poisoning bug where the reference hash depended only on assembly name and reference count, causing missing generated sources depending on test order. The caching commit added **zero test files**; there is no regression test for that bug or for cross-file `using` changes affecting alias bindings (`TypeAliasResolver.cs:244-245`). `ControlModelBuilder.ModelCache` keys by assembly *simple name* (`:23`) rather than identity.

**G9 — publisher complexity.** `[GeneratePublicMarkupExtensions]` publishes generated extensions as public APIs; consumers skip covered controls by reading `[assembly: PublicMarkupExtensionControls]` with origin tracking; two independent publishers produce `SVCUIGEN008` (`ExtensionPublishing.cs:153-177`, `UsageDrivenMarkupGenerator.cs:245-253`). The feature is useful for library authoring but adds a second source of "which generator owns this API" complexity that needs stronger tests.

### 3.7 XAML resource factories

`XamlResourceGenerator` emits a `Create(IServiceProvider?)` method plus an `[UnsafeAccessor]` extern against Avalonia's compiled-XAML `Build:/path` convention (`XamlResourceGenerator.cs:187-204`). It is opt-in via assembly attribute, reflection-free, and requires .NET 10 (`SVCUIGEN002`). A good, focused feature. Its cache key deliberately includes the full fingerprint because compilation-local type names and assembly attributes affect output (`:241-247`).

---

## 4. Emitted API surface and ergonomics

### 4.1 Strengths

- Factories return real controls and carry caller info; no wrapper types to learn.
- Every generated setter carries XML documentation copied from the underlying Avalonia property/event (`DocumentationLookup`, `docs/usage-driven-generation.md:93-101`).
- Generic `T where T : DeclaringType` constraints preserve concrete types through chains (`PropertyExtensionInfo.cs:34-43`).
- Three binding shapes per property (value, expression, `BindingBase`) cover both strongly typed MVVM and full reflection bindings.
- Attached properties follow a discoverable `Host_Property` convention (`Grid_Row`, `Canvas_Left`), with fluent shortcuts `Col`/`Row`/`Cols`/`Rows` (`ControlPropertyExtensions.cs:230-269`).
- Generated extensions default to `internal`; publication is opt-in, so package consumers do not collide by default.

### 4.2 Issues

- **G5** overload explosion (above).
- **G6** naming duplication (above).
- **G7** divergent event generation (above).
- **G1** global static import/aliases (above).
- **G10 — generated headers inconsistent.** Factory, registration, extension, XAML resource, and coverage sources carry `// <auto-generated />`; the post-init global-usings source does not (`Generator.cs:22-27`). With no `generated_code = true` in `.editorconfig`, formatters may treat it as user code.
- **G11 — API surface discovery.** `docs/` has no catalog of generated method families; users must infer patterns from samples/tests. Reactor's API index and recipe kit are a useful model here.

---

## 5. Cross-cutting correctness and robustness

| Area | Assessment |
|---|---|
| Error handling | Strong: central `ViewBuildingException` creation with caller file/line and diagnostics recording (`ControlPropertyExtensions.cs:168-205`). Inconsistent for style converter errors (R9). |
| Threading | Hot reload dispatches to UI thread (`ViewBase.cs:223`); source view scope uses `AsyncLocal` (`SourceInfo.cs:21`). Reload task is unobserved (R3). |
| Global state | Static AppBuilder config (R5), static HotReload flag, process-wide diagnostics buffer (R7), static generator caches (G8). |
| Registration | Last-wins with closure allocation (R6); module initializers make initialization order implicit. |
| Trimming/AOT | `IsAotCompatible=true`, `[DynamicallyAccessedMembers]` on `ViewFactory.Create<T>`, `UnsafeAccessor` for XAML, no reflection in generated code. `Activator.CreateInstance<T>()` requires the parameterless constructor to survive trimming; document the guarantee. |
| Lifecycle | Careful style cleanup and name-scope reset on reload (`ViewBase.cs:229-240`), but re-entrancy flags are manipulated externally (R4). |
| Diagnostics UX | Rich runtime messages, poor compile-time locations (G2). |

---

## 6. Performance

- **Generator:** measured cold ~14 ms, comment-only edit ~1.6 ms, genuine new-control edit ~24 ms (`docs/performance-review.md:180-198`); stage-level measurements are in the tens of microseconds. Generated source count reduced 202→70 external extension files by usage-driven generation (`docs/usage-driven-generation.md:133`).
- **Caching:** tiered weak/hash caches with deliberate eviction caps; reference hashes include metadata IDs; proven 26%/45% build-time gains.
- **Runtime:** generated setters are aggregating extension methods calling `_set`/`_setCompiledBinding`; registration is a cold-path module initializer; source metadata uses a CWT. The usual path adds a delegate call and a CWT lookup — low risk.
- **Compile-time cost of overloads:** 3,314 public statics and unbounded constructor overloads (G5) are not measured. This is a missing benchmark dimension, especially for large apps.
- **Startup cost:** one module initializer per eligible control (81 in the test compilation). Not measured; worth measuring before changing registration strategy.
- **Reproducibility gap:** benchmark artifacts cited in `docs/performance-review.md:40-42,182` are git-ignored (`.gitignore:61`). **T1.**

---

## 7. Test strategy

**Coverage:** 288/288 tests (144 cases × net10/net11). Generator behavior is tested with `CSharpGeneratorDriver` over in-memory compilations and pinned snapshots in `TestData/*Extensions.cs`; alias/collision paths, XAML resource diagnostics, binding behavior, reload, and build diagnostics have dedicated tests.

**Gaps:**

- **T2 — no formatter/IDE regression test.** No `dotnet format`, IDE0001/0002/0005, or "remove unnecessary using" reference anywhere in source, tests, docs, or CI.
- **T3 — generated usings untested.** Snapshots read only `*Extensions.cs` (`MarkupExtensionGenerationTests.cs:26,56-63`); no test asserts the post-init global-usings file; the default test generator (`ExternalAssemblyExtensionsGenerator`) does not run `Generator.Initialize` (`:1667-1677`).
- **T4 — caching commit untested.** `2f8596f` added no test files; the earlier `d431cc6` cache-poisoning fix has no dedicated guard.
- **T5 — samples untested.** Samples are compiled by CI but referenced by no test project; `docs/usage-driven-generation.md:141,148` treats sample compilation as the gate.
- **T6 — no test tiers.** Reactor's four-tier model (pure unit / live control / packaged / real input) with a documented "when to write which" table is absent. For CSharpUI, at minimum separate "generator string tests" from "live Avalonia control tests" and add a real-build smoke test.

---

## 8. Documentation, tooling, and CI

- `docs/` contains `architecture.md`, `usage-driven-generation.md`, and `performance-review.md`. The architecture doc correctly states the no-reconciler boundary; the usage-driven doc documents R0-R5, risks, and generated API policy.
- **T7 — doc drift:** `usage-driven-generation.md:69` still describes R3 as "prefer current-assembly/Avalonia.Controls" while `UsageResolver.cs:154-168` keeps all candidates (the same doc concedes this at `:162`); `:6` says 248 tests while `performance-review.md:201` says 288; `:170` says the tree is uncommitted/no tag although HEAD is tagged `v1.0.0-alpha.11`.
- **T8 — no style enforcement:** no `EnforceCodeStyleInBuild`, no `generated_code = true`, no IDE diagnostic configuration (`.editorconfig`). CI (`.github/workflows/pr.yaml`, `cicd.yaml`) delegates to an external build/test/coverage/pack workflow; no format step.
- **T9 — changelog:** `CHANGELOG.md` records significant user-visible changes well, but the caching/performance commit has no entry (only README/docs additions).

---

## 9. What CSharpUI can learn from Microsoft.UI.Reactor (MVVM-safe)

Filter: copy **generator, analyzer, naming, registration, and documentation engineering**; never copy hooks, virtual DOM, reconciliation, React controlled inputs, or interceptor-based source maps.

1. **No generated global usings.** Reactor emits none; the DSL import `using static Microsoft.UI.Reactor.Factories;` is hand-written (`README.md:31`, `AGENTS.md:95`), generated wrapper factories are imported with `using static MyApp.SettingsCardElement;` written by the consumer (`docs/specs/058-control-wrapper-generator.md:96-99`). → Make the static import opt-in; keep namespace imports.

2. **Hermetic generated files.** Reactor emits `// <auto-generated/>`, no using directives, and `global::`-qualified names (`WrapperGenerator.cs:1224-1230,1384-1395,1832-1834`). → Fix G10; never emit aliases from a generator.

3. **Solve collisions by naming policy.** Reactor removed a name-only `ScrollViewer` alias after it shadowed the WinUI type (`docs/specs/tasks/039-property-and-event-scrub-implementation.md:585-601`) and documented the exact `CS0119`/`global::` remedy (`Dsl.cs:619-628`); its Canvas recipe steers users to `.Canvas(left, top)` (`skills/recipes/canvas-positioning.cs:1-6`). → Choose non-colliding factory names and document escape hatches; do not generate aliases.

4. **Decline to act when ambiguous.** Reactor refuses to stamp calls when two `using static` imports could change overload resolution (`SourceMapInterceptorGenerator.WrapperFactories.cs:344-354,387-397`; `WrapperFactoryOverloadTests.cs:12-24,180-181`). → Never auto-rewrite qualifiers under ambiguity.

5. **The CS0436 precedent.** Reactor used to emit authoring attributes into every compilation, hit a collision that broke Release builds, and moved them to a single abstractions assembly with a regression guard for *non-emission* (`docs/specs/058-control-wrapper-generator.md:634`; `WrapperAttributes.cs:1-11`; `WrapperGeneratorTests.cs:1801-1826`). → Add an absence guard when removing the global static import.

6. **First-wins, captureless registration.** Reactor's registry is lazy, idempotent, first-wins, reflection-free, and latched so hot paths skip it (`ControlRegistry.cs:10-43,66-78`). → Fix R6 and measure module-initializer startup.

7. **Analyzer discipline.** Syntactic prefilter → symbol resolution last; reserve Warning for near-zero-false-positive rules; fully qualified fixes with `Simplifier.Annotation`; withhold fixes when semantics are incomplete; stable IDs + `AnalyzerReleases` rows (`docs/guide/analyzer-architecture.md:302-320,561-570`; `NoOpModifierCodeFix.cs:32-35,129-135`; `CodeFixHelpers.cs:13-24`). → Address G2 with real analyzers and fixes.

8. **Test tiers and guards.** Four explicit tiers with a selection table (`TESTING.md:3-16,130-139`), positive/negative/near-miss analyzer tests, an always-on silent-drop guard with a self-validation lower bound (`DescriptorSilentDropGuardTests.cs:12-27,84-102`). → Address T2-T6.

9. **Spec and doc discipline.** Specs carry a north star, resolved-decision table, implementation checklist, "no test edits" gates, and explicit deferrals (`docs/specs/058...:10-34,518-522,577-582`); guides are generated from templates with CI tier gates (`docs/contributing/doc-pipeline.md:7-10`; `CONTRIBUTING.md:209-220`). → Fix T7 and record the import decision.

10. **Share facts between generators; cache with CWTs.** Reactor links one source file into both generators (`SourceMapShared.cs:13-17`; `Reactor.Wrappers.Generator.csproj:31`) and memoizes compilation-wide lookups in CWTs (`SourceMapShared.cs:39-44,516,558,574,630`). → Address G6 and G8.

**Explicitly do not copy:** hooks/`UseState`, virtual element tree, keyed reconciliation, `Optional<T>` controlled-input semantics and echo suppression, component-state-preserving pooling, and interceptor source maps. CSharpUI's caller-info + `SourceInfo` + `MetadataUpdateHandler` combination already covers the useful subset for MVVM.

---

## 10. Findings register

| ID | Area | Sev | Finding | Key evidence |
|---|---|---|---|---|
| G1 | Generator | Critical | Injected `global using static` + generated aliases; un-aliasable names degrade to "qualify in source", which formatters undo | `Generator.cs:22-29`; `UsageDrivenMarkupGenerator.cs:391-420,162-169` |
| G2 | Diagnostics | High | No analyzers/code fixes; all generator diagnostics at `Location.None` | `UsageDrivenMarkupGenerator.cs:200-212` |
| R1 | Runtime | High | `Child = content as Control` silently yields null | `ViewBase.cs:153-154` |
| R5 | Runtime | High | Static mutable AppBuilder config shared across instances | `AppBuilderExtensions.cs:11-13,31-80` |
| R6 | Runtime | High | `ViewFactoryRegistry` last-wins; implicit module-initializer ordering | `ViewFactoryRegistry.cs:18-23` |
| G8 | Generator | High | Weakly guarded hash-keyed caches; prior real poisoning bug; no regression tests | `GenerationCache.cs:207-225`; commit `d431cc6`; `git show --stat 2f8596f` |
| T2/T3 | Tests | High | No formatter test; generated usings excluded from snapshots; default harness skips post-init | `MarkupExtensionGenerationTests.cs:26,56-63,1667-1677` |
| T7 | Docs | High | R3/test-count/tag drift | `usage-driven-generation.md:6,69,170` |
| R2 | Runtime | Medium | `Child` getter builds the view as a side effect | `ViewBase.cs:210-217` |
| R3/R4 | Runtime | Medium | Reload is fire-and-forget; re-entrancy flags reset externally | `ViewBase.cs:221-248` |
| G4 | Generator | Medium | Colliding factory names silently remove all candidates | `UsageDrivenMarkupGenerator.cs:521-527` |
| G5 | API | Medium | Overload explosion; unbounded constructor overloads | 3,314 statics; `ConstructorOverloads.cs:21-46` |
| G6 | Generator | Medium | Naming rules duplicated across 7+ sites | §3.4 |
| G7 | API | Medium | Two divergent event generators; unused `_setEvent` | `ActionToEventGenerator.cs:25-79`; `AvaloniaEventExtensionsGenerator.cs:123-168` |
| G9 | Generator | Medium | Publisher coverage/origin complexity | `ExtensionPublishing.cs:153-177` |
| R8 | Runtime | Medium | Fragile style-selector string heuristic | `Styles/Style.cs:28-58` |
| R9 | Runtime | Low | Style converter errors bypass file/line wrapping | `StylePropertyExtensions.cs:88,115` |
| R7 | Runtime | Low | Process-wide diagnostics buffer without per-view correlation | `DiagnosticsErrorLog.cs:8-22` |
| R10 | API | Low | `default(VM)!` binding token | `StyleBindingTests.cs:37-39` |
| G10 | Generator | Low | Inconsistent `// <auto-generated />` headers | `Generator.cs:22-27` |
| T1/T4/T5/T6/T8/T9 | Tooling | Low/Med | Benchmark artifacts ignored; caching commit untested; samples untested; no test tiers/style gates; changelog gap | §6-8 |

---

## 11. Roadmap

### P0 — correctness and tooling blockers
1. Make the global static factory import opt-in; remove alias emission and `SVCUIGEN007`; add `// <auto-generated />` to all generated sources (G1, G10).
2. Add a formatter/simplifier regression test and assert the exact post-init generated file (T2, T3).
3. Fix `ViewBase` `Child` mismatch and make `Reload()` awaited/observed (R1, R3).
4. Replace registry last-wins with deterministic first-wins or warn on duplicate registration (R6).
5. Add a non-emission regression guard and a cache-invalidation regression test (Reactor L5, G8).

### P1 — actionable diagnostics and robustness
6. Add a `DiagnosticAnalyzer` with real locations and code fixes that emit `global::`-qualified names; configure IDE0001/0002/0005 and `EnforceCodeStyleInBuild` deliberately (G2, T8).
7. Report colliding factory names at the declaration with a warning instead of silently dropping factories (G4).
8. Eliminate static AppBuilder configuration in favour of instance-scoped state or explicit validation (R5).
9. Unify the two event generators and use or delete `_setEvent` (G7).
10. Fix docs drift and record the import decision and formatter boundary in `docs/architecture.md` / `docs/usage-driven-generation.md` (T7).

### P2 — maintainability and scale
11. Centralize naming rules into one helper (G6).
12. Cap or make constructor-overload generation opt-in; measure compile-time cost (G5).
13. Harden the style-selector heuristic and move converter creation inside the guarded path (R8, R9).
14. Introduce explicit test tiers, a real-build smoke test, and sample-backed integration coverage (T4-T6).
15. Add per-view diagnostic correlation and document AOT/trimming guarantees (R7, §5).

### P3 — longer-term investments
16. Consider a descriptor-like *description ↔ native control* separation for future generation work, without introducing a reconciler (Reactor §9).
17. Generate docs/recipe catalogs from source and gate them in CI; commit or regenerate benchmark artifacts (T1, Reactor §9).
18. Measure module-initializer startup cost and evaluate lazy registration only if the data justifies it (§6).

---

## 12. Appendix — evidence and review method

- Five parallel reviews were run over: generator internals and emission sites; emitted API surface and method taxonomy; runtime/DSL/MVVM architecture; Reactor design lessons filtered for MVVM; tests/docs/tooling/CI. Every claim was traced to a file and line, and critical claims were independently re-read.
- Reactor revision: `8ed5346f`. CSharpUI revision: `2f8596f` (`v1.0.0-alpha.11`).
- Generated-output counts come from checked-in `EmitCompilerGeneratedFiles` output under `tests/SVCSystems.Avalonia.CSharpUI.Tests/obj/Debug/net11.0/generated/...`.
- This document began as a review artifact. The fixes listed in §13 were subsequently applied to the source tree.

---

## 13. Resolution status (2026-11-25)

All P0 and P1 items, and most P2 items, have been implemented.

### P0 — done

| Item | Change |
|---|---|
| Opt-in factory import (G1, G10) | `GenerateGlobalControlFactoriesAttribute` added; the generator emits `global using static ...GeneratedControlFactories` only when opted in; all generated sources now carry `// <auto-generated />`. `TypeAliasResolver` and all generated type aliases were deleted; `SVCUIGEN007` was removed. |
| Formatter regression tests (T2, T3) | Added `Post_init_global_usings_source_has_generated_header`, `No_names_are_injected_that_a_formatter_could_simplify_away`, `User_using_aliases_do_not_change_generated_output`, plus exact opt-in/default import tests. IDE0001/0002/0005 are disabled in `.editorconfig` with an explanatory comment. |
| ViewBase `Child`/reload (R1, R3, R4) | Non-`Control` `Build()` result throws; `ReloadAsync()` awaits dispatcher work and observes faults; re-entrancy flags are owned inside `InitializeCore`/`ReloadAsync`. |
| Registry first-wins (R6) | `ViewFactoryRegistry.Register` uses `TryAdd` and records a warning on duplicates. |
| Non-emission and cache guards (G8) | Added a test asserting no injected names are emitted; the comment-only-edit test now preserves the opt-in attribute and exercises the real incremental path. |

### P1 — done

| Item | Change |
|---|---|
| Analyzer and code fix (G2) | New `SVCSystems.Avalonia.CSharpUI.Analyzers` project with `SVCUI001` and a `global::` code fix using `Simplifier.Annotation`; packed into `analyzers/dotnet/cs`; 12 analyzer tests (positive/negative/near-miss/fix round-trip). Generator diagnostics now carry real source locations. |
| Colliding factory names (G4) | New `SVCUIGEN009` Warning reported at the control declaration; factories for colliding names remain suppressed. |
| AppBuilder configuration (R5) | Process-wide settings are latched with idempotent same-value sets and an exception on conflicting reconfiguration. |
| Event generator unification (G7) | Both event emitters now use the shared `EventAdapterShapes` rule; `_setEvent` is marked `[Obsolete]` as no longer emitted. |
| Docs drift (T7) | `README.md`, `docs/architecture.md`, `docs/usage-driven-generation.md`, `docs/testing.md`, and `CHANGELOG.md` updated: R3 behavior, test counts (308), tag status, import decision, formatter boundary, and diagnostics list. |

### P2 — done / partially done

| Item | Status |
|---|---|
| Centralized naming rules (G6) | Added `NamingConventions` as the single home for control/view/event/attached identifiers; emitters now call it. |
| Constructor-overload cap (G5) | `ConstructorOverloads` caps emitted overload sets at 8 per value type; the full-value setter remains available. Compile-time measurement itself was not re-run — see `docs/performance-review.md` for the benchmark harness. |
| Style selector/converter (R8, R9) | `Style` accepts an explicit `treatAsTraversal` override; style binding converters are created inside the guarded path. |
| Per-view diagnostics and AOT docs (R7) | Build diagnostics include a per-instance id; `ViewFactory.Create<T>` and the XAML resource attribute document trimming guarantees. |
| Test tiers (T4-T6) | `docs/testing.md` defines four tiers; the analyzer suite is a separate project; the full solution build gates sample compilation and packaging. A sample-backed *in-memory* compilation tier remains future work. |

### Verification

- `dotnet build SVCSystems.Avalonia.CSharpUI.slnx` — succeeds, 0 errors.
- `dotnet test SVCSystems.Avalonia.CSharpUI.slnx --no-build` — **308/308 passed** (296 library/generator + 12 analyzer).
- `dotnet pack src/SVCSystems.Avalonia.CSharpUI` includes both the generator and analyzer DLLs under `analyzers/dotnet/cs`.
