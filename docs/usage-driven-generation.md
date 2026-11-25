# Usage-driven generation design

Status: **implemented**
Applies to: `SVCSystems.Avalonia.CSharpUI.SourceGenerator`

Implementation notes: the curated common-control list is a `typeof(...)` assembly attribute (`GenerateMarkupControlAttribute`) in the runtime library, because the `netstandard2.0` generator cannot reference Avalonia 12 (net8.0/net10.0 only). The same attribute can be applied in the consuming assembly as a narrow per-type opt-in. The legacy `GenerateMarkupExtensionsFor*` attributes are removed; public-extension publishing is opt-in via `[GeneratePublicMarkupExtensions]`. **308 tests pass** (154 per target framework) on net10.0 and net11.0.

## Decisions (confirmed)

| Topic | Decision |
| --- | --- |
| Usage signal | **Any mention of a control in application source**: factory calls (`Button()`), `new Button()`, typed declarations/casts, `typeof(Button)`, generic arguments (`ViewFactory.Create<Button>()`), base lists, `nameof`, etc. |
| Legacy "generate everything" mode | **Removed.** No `[assembly: GenerateMarkupExtensionsForAvalonia]` / `[assembly: GenerateMarkupExtensionsForAssembly]` full-scan escape hatch. |
| Scope of trimming | **Everything**: fluent extensions, `GeneratedControlFactories`, and `ViewFactoryRegistry` registrations are generated only for the used set (plus the required base-type closure). |
| Common controls | A curated allowlist of frequently used Avalonia controls is **always** generated, declared with `typeof(...)` in the runtime library and read by the generator from metadata. This is not the removed full-scan mode. |
| Factory syntax | Bare `Button()` requires a consumer-authored import: a per-file `using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;`, a project-level `<Using Include="SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories" Static="true" />` item, or an opt-in `[assembly: GenerateGlobalControlFactories]` that asks the generator to emit a global static import. The generator never emits type aliases. |

Non-goals: changing extension method bodies, the Avalonia property system integration, compiled bindings, source metadata, XAML resource factories, or the declarative-view generators. Those are driven by declarative views and view classes, not by the whole control universe.

---

## 1. What the old generator did

| Generator | Discovery | Cost |
| --- | --- | --- |
| `ExternalAssemblyExtensionsGenerator.GetGenerationTargets` | `SymbolUtilities.GetTargetAssemblies()` unconditionally added `Avalonia.Base`, `Avalonia.Controls`, `Avalonia.Markup`, `Avalonia.Themes.Fluent`; `GetPublicClasses` walked every namespace. | Full `ControlModel` (base chains, docs) plus all `ExtensionInfo` records and a large signature string for **every public Avalonia class**. |
| `LocalControlExtensionsGenerator` | Syntax provider over every `ClassDeclarationSyntax`, keeping anything deriving from `AvaloniaObject`. | A model for every local control, used or not. |
| `ControlFactoryGenerator` | Enumerated all public classes in the local and target assemblies. | A factory for every eligible constructible control. |
| `ViewFactoryRegistrationGenerator` | Syntax provider over every class; built a `ControlModel` before eligibility filtering. | Model building even for view models and unrelated classes. |

`[assembly: GenerateMarkupExtensionsForAvalonia]` was misleading: it did not enable generation, only flipped generated classes from `internal` to `public`.

---

## 2. Implemented architecture

```
Source syntax trees (user code only)
  │   predicate: node is SimpleNameSyntax → capture identifier string
  ▼
UsageNameProvider                       → collected names
  │   .Collect() + Combine(CompilationProvider)
  ▼
ControlCatalog                          → name → candidate control types
  │   (local classes + public classes of Avalonia-referencing assemblies)
  │   + GenerateMarkupControl attribute read from the app and referenced assemblies
  ▼
UsageResolver                           → UsageResult
  │   1. add curated common controls as roots
  │   2. add local control declarations as roots
  │   3. map source names to control symbols
  │   4. close over base types             ← mandatory (see 2.2)
  │   5. convert to stable string keys, sort, dedupe
  ▼
UsageDrivenMarkupGenerator               → one ControlModel per used type
  │   ├─ ExternalGeneratorHost            → per-control fluent extensions
  │   ├─ factory emitter                  → GeneratedControlFactories
  │   ├─ registration emitter             → ViewFactoryRegistry.Register<T>()
  │   └─ factory-using emitter            → global static import + type aliases
```

`AvaloniaPropertyExtensionsGenerator`, `AvaloniaEventExtensionsGenerator`, and `XamlResourceGenerator` are unchanged.

### 2.1 Defining "used"

0. **R0 — curated common controls and per-type opt-in.** Types in `[assembly: GenerateMarkupControl(typeof(...))]` are roots. The runtime library declares the built-in list with `typeof` (compile-time checked); the generator reads it from metadata because it cannot reference Avalonia directly. An application can apply the same assembly attribute to force generation for controls that usage detection cannot see, e.g. a control used only from compiled XAML in a referenced library.
1. **R1 — any simple name.** Every `SimpleNameSyntax` is captured as a string: factory calls, `new X()`, typed declarations/casts, `typeof`, `nameof`, generic arguments, base lists, and fully qualified names (the rightmost segment). Unrelated identifiers are ignored unless they match a catalog factory name.
2. **R2 — local control declarations.** All `AvaloniaObject`-derived classes declared in the current compilation are roots, so controls created by reflection or from XAML inside the app still get APIs.
3. **R3 — disambiguation.** Keep every candidate whose generated extension-class name is distinct, so source compiles regardless of which control was meant, and report informational `SVCUIGEN006`. When two controls share a generated *factory* name, both factories are suppressed and warning `SVCUIGEN009` is reported at the declaration; registrations are still emitted.
4. **R4 — base-type closure.** Add each root's full base chain up to `Avalonia.AvaloniaObject`.
5. **R5 — filtering.** Drop `Ignore = true` types and types inaccessible from generated code. Abstract types stay in the extension set but are excluded from factories/registrations by `IsConstructible`.

The result is a sorted `UsageResult` of stable `TypeKey`s (`assembly identity + fully qualified name`). Roslyn symbols and `ControlModel`s are never stored in incremental values; keys are re-resolved against the current compilation in the source-output stage.

### 2.2 Why base-type closure is mandatory

Setters are emitted on the type that *declares* the Avalonia property (`ExternalGeneratorHost.GetPropertyInfos` uses `DeclaredProperties`). Extension resolution walks base types, so `Button().Margin(...)` requires `Layoutable` in the set. Closing over the chain (`Button → ContentControl → TemplatedControl → Control → InputElement → Interactive → Layoutable → Visual → StyledElement → Animatable → AvaloniaObject`) preserves this. `StyledElement`'s style-field special case and attached setters (hosted by e.g. `LevelBadge`) also depend on the host being present. `ControlCatalog` indexes the generated attached-property extension names (`Host_Property`, including metadata aliases), so calling one in source — e.g. `Grid_Row(...)` or `ThirdPartyGrid_Dock(...)` — roots its host automatically, even when the host type is never named. This works for hosts that are controls and for hosts that are plain non-control classes; only the latter don't get factories or base-chain entries.

### 2.3 Control catalog

`ControlCatalog` walks local classes (including `internal`) and public classes of referenced assemblies that reference Avalonia (via `IModuleSymbol.ReferencedAssemblies`). It reads one metadata attribute per type; **no `ControlModel` is built here**. This is the main win: models are built only for types surviving usage filtering.

### 2.4 Incremental behavior

`UsageNameProvider` captures strings with a cheap syntax transform; unchanged trees are cached by Roslyn. `Collect()` is combined with `CompilationProvider`, and `RegisterSourceOutput` resolves keys, builds each `ControlModel` once in a shared cache, and emits all files from one generator instance. No signature strings are built just for comparison. The optional global static factory import is emitted from the same stage as the other sources; the two always-on namespace imports are post-init. Both are unrelated to usage analysis because no aliases are generated from the pre-generation compilation.

The test `Comment_only_edit_keeps_generated_sources_stable_on_one_driver` reuses one `GeneratorDriver` across a real syntax-tree replacement and asserts the generated sources are unchanged.

### 2.5 Transparent factory calls — consumer-controlled

The generator **does not inject a global static import by default**. Bare factory calls are made available by a consumer-authored import:

```csharp
using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;
```

or, project-wide, `<Using Include="SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories" Static="true" />` in the project file. A project that deliberately wants the import generated can apply `[assembly: GenerateGlobalControlFactories]`; the generator then emits one file containing `global using static ...GeneratedControlFactories;`.

No generated type aliases exist. A static import shadows same-named types in member-access positions (`Path.Combine`, `Canvas.Left`), so callers who opt in must qualify such types (`global::System.IO.Path.Combine`) or use the per-file import instead. The `SVCUI001` analyzer reports shadowed identifier usages and offers a code fix that emits a globally qualified name with a `Simplifier` annotation. This design keeps every name visible in consumer source so formatters and IDEs resolve the same compilation the compiler does.

### 2.6 XML documentation on generated APIs

Extension classes, setters, bindings, events, style setters, and factory methods carry `///` comments. `DocumentationLookup` handles the compiler-host gap where metadata symbols have no documentation provider:

- source symbols → `GetDocumentationCommentXml()`;
- metadata symbols → the `.xml` file beside the reference, parsed into `<member name="...">` entries and served by `DocumentationCommentId`; fields also try the backing CLR property's `P:` ID;
- factories use the control type's docs, with a synthetic `<summary>` fallback.

Efficiency: reference→path is cached once per assembly identity, each XML file is parsed once, and each formatted entry is cached once — none of it repeats per member or per keystroke.

---

## 3. API and compatibility changes

**Removed:** both `GenerateMarkupExtensionsFor*` attributes, `AutoFrameworkAssemblies`, `GetTargetAssemblies`, `AddAvaloniaTargets`, `AddAssemblyAttributeTarget`, `AddTargetAssembly`, `HasGeneratePublicExtensions`, `TargetAssembly`, `GetPublicClasses`, `SVCUIGEN001`, `SVCUIGEN007`, duplicate-provider checks, `LocalControlExtensionsGenerator`, `ViewFactoryRegistrationGenerator`, and `TypeAliasResolver` with all generated type aliases.

**Added:** `GenerateMarkupControlAttribute` + the `typeof` list in `CommonControls.cs`; `GenerateGlobalControlFactoriesAttribute` (opt-in generated global static import); `Usage/` (`UsageNameProvider`, `ControlCatalog`, `UsageResolver`, `UsageResult`, `CommonControls`, `TypeKey`); `DocumentationLookup`; `UsageDrivenMarkupGenerator`; the `SVCUI001` analyzer and code fix in `SVCSystems.Avalonia.CSharpUI.Analyzers`. `ExternalAssemblyExtensionsGenerator` and `ControlFactoryGenerator` remain as thin wrappers for direct test hosts.

**Kept:** `SVCSystemsCSharpUIGeneratorMetadataAttribute` (`Ignore`, `Alias`, `GenerateFactory`) — now the user's direct control over usage resolution and suppression. The inert `Content` option was removed. Public-extension publishing remains opt-in via `[GeneratePublicMarkupExtensions]`.

**Diagnostics:** `SVCUIGEN003` (generation failure, Error), `SVCUIGEN006` (ambiguous control name, Info), `SVCUIGEN008` (overlapping publishers, Warning), `SVCUIGEN009` (colliding factory name, Warning, reported at the control declaration), and `SVCUI001` (factory name shadows a type, Warning, with a `global::` code fix). Registered in the corresponding `AnalyzerReleases.Unshipped.md` files.

The library is in alpha, so these breaking changes (public attributes removed, generated extensions now internal) need no compatibility shims or migration path beyond the `CHANGELOG.md` entry.

---

## 4. Implementation status

| Phase | Status |
| --- | --- |
| 0 — test-first groundwork | Done |
| 1 — usage detection and resolution | Done |
| 2 — route fluent extensions through used set | Done; consolidated into `UsageDrivenMarkupGenerator` |
| 3 — factories, registrations, opt-in global import | Done; registrations cover used controls from any assembly; no generated aliases |
| 4 — remove legacy surface, update samples/docs | Done |
| 5 — verification and measurement | Done; numbers below |

## 5. Verification

- `dotnet build` and `dotnet test`: green on net10.0/net11.0, **308/308 tests** (296 library/generator + 12 analyzer tests).
- Generated sources in the test project: **202 → 70** external extension files (258 → 170 total generated files).
- Measured full-solution builds (two runs each, HEAD worktree vs current tree):

| Metric | Before | After | Change |
| --- | --- | --- | --- |
| Clean rebuild | 8.74s / 8.39s | 6.43s / 6.18s | ~26% faster |
| Incremental (one view file edited) | 2.99s / 3.18s | 1.79s / 1.60s | ~45% faster |

The three samples compile and run with a single opt-in attribute each (`[assembly: GenerateGlobalControlFactories]`) for bare factories; inherited setters, attached setters, events, style setters, factories, `SourceInfo`, and `ViewFactory.Create<T>()` all work.

## 6. Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Control used only in compiled XAML, reflection, or a runtime-computed name | Local declarations are roots; referenced-library controls are found by name; `[assembly: GenerateMarkupControl(typeof(X))]` in the app is a narrow per-type opt-in. |
| Inherited setters break without closure | R4 base-type closure; sample compilation is the gate. |
| Name collisions | R3 include-all + `SVCUIGEN006`; colliding factory names are reported with `SVCUIGEN009` and suppressed. |
| Global static import shadows types | The static import is opt-in and no aliases are generated; prefer a per-file `using static` or project-level `Using Static`. The `SVCUI001` analyzer reports shadowed identifiers and offers a `global::` fix. |
| Metadata docs unavailable in compiler host | `DocumentationLookup` reads the XML beside each reference, cached at three levels. |
| Breaking change for consumers | Alpha library — breaking changes are acceptable; recorded in `CHANGELOG.md`. |
| Stale generated files when usage is removed | Source output emits the full set each run; Roslyn drops files not re-added. |

## 6b. Review hardening

Follow-up from PR review:

- The catalog records base-type symbols, so constructed generic bases (`Derived : GenericBase<int>`) resolve to real symbols and get their setters generated; open definitions are suppressed when a constructed counterpart exists.
- Referenced assemblies are discovered through transitive Avalonia references, nested control types are enumerated, and referenced controls are indexed by both factory alias and CLR name.
- Attached-property hosts are rooted from generated extension names (`Host_Property`), including non-control hosts.
- Name collisions now keep all distinct candidates instead of preferring one.
- Generated type aliases and `TypeAliasResolver` were removed: an injected static import plus generated aliases is not reproducible by tooling that formats or simplifies consumer source.
- `ViewFactory` registrations are emitted for every eligible control even when factory names collide; `SVCUIGEN009` reports the collision instead of dropping factories silently.
- Generation result is equatable and registered as the source-output input, so Roslyn caches unchanged files across edits.
- Generator tests resolve the Avalonia reference path from MSBuild instead of a hard-coded NuGet cache.

## 7. Remaining work

- Evaluate a descriptor-like description ↔ native-control separation for future generator work, without introducing a reconciler.
- Provide source-generated option sets for frequently used attached properties where constructor-overload expansion becomes measurable.
- See `docs/review-findings.md` for the full review and the status of its recommendations.
