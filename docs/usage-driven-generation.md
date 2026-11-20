# Usage-driven generation design

Status: **implemented**
Applies to: `SVCSystems.Avalonia.CSharpUI.SourceGenerator`

Implementation notes: the curated common-control list is a `typeof(...)` assembly attribute (`GenerateMarkupControlAttribute`) in the runtime library, because the `netstandard2.0` generator cannot reference Avalonia 12 (net8.0/net10.0 only). The same attribute can be applied in the consuming assembly as a narrow per-type opt-in. The legacy `GenerateMarkupExtensionsFor*` attributes and public-extension publishing are removed. **248 tests pass** (124 per target framework) on net10.0 and net11.0.

## Decisions (confirmed)

| Topic | Decision |
| --- | --- |
| Usage signal | **Any mention of a control in application source**: factory calls (`Button()`), `new Button()`, typed declarations/casts, `typeof(Button)`, generic arguments (`ViewFactory.Create<Button>()`), base lists, `nameof`, etc. |
| Legacy "generate everything" mode | **Removed.** No `[assembly: GenerateMarkupExtensionsForAvalonia]` / `[assembly: GenerateMarkupExtensionsForAssembly]` full-scan escape hatch. |
| Scope of trimming | **Everything**: fluent extensions, `GeneratedControlFactories`, and `ViewFactoryRegistry` registrations are generated only for the used set (plus the required base-type closure). |
| Common controls | A curated allowlist of frequently used Avalonia controls is **always** generated, declared with `typeof(...)` in the runtime library and read by the generator from metadata. This is not the removed full-scan mode. |
| Factory syntax | `Button()` works in any file with no `using static` directive; the generator emits a global static import plus the type aliases needed to avoid shadowing types. |

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
3. **R3 — disambiguation.** Prefer a current-assembly match, then `Avalonia.Controls`. If still ambiguous, generate all candidates with distinct extension-class names and report informational `SVCUIGEN006`.
4. **R4 — base-type closure.** Add each root's full base chain up to `Avalonia.AvaloniaObject`.
5. **R5 — filtering.** Drop `Ignore = true` types and types inaccessible from generated code. Abstract types stay in the extension set but are excluded from factories/registrations by `IsConstructible`.

The result is a sorted `UsageResult` of stable `TypeKey`s (`assembly identity + fully qualified name`). Roslyn symbols and `ControlModel`s are never stored in incremental values; keys are re-resolved against the current compilation in the source-output stage.

### 2.2 Why base-type closure is mandatory

Setters are emitted on the type that *declares* the Avalonia property (`ExternalGeneratorHost.GetPropertyInfos` uses `DeclaredProperties`). Extension resolution walks base types, so `Button().Margin(...)` requires `Layoutable` in the set. Closing over the chain (`Button → ContentControl → TemplatedControl → Control → InputElement → Interactive → Layoutable → Visual → StyledElement → Animatable → AvaloniaObject`) preserves this. `StyledElement`'s style-field special case and attached setters (hosted by e.g. `LevelBadge`) also depend on the host being present. `ControlCatalog` indexes the generated attached-property extension names (`Host_Property`, including metadata aliases), so calling one in source — e.g. `Grid_Row(...)` or `ThirdPartyGrid_Dock(...)` — roots its host automatically, even when the host type is never named. This works for hosts that are controls and for hosts that are plain non-control classes; only the latter don't get factories or base-chain entries.

### 2.3 Control catalog

`ControlCatalog` walks local classes (including `internal`) and public classes of referenced assemblies that reference Avalonia (via `IModuleSymbol.ReferencedAssemblies`). It reads one metadata attribute per type; **no `ControlModel` is built here**. This is the main win: models are built only for types surviving usage filtering.

### 2.4 Incremental behavior

`UsageNameProvider` captures strings with a cheap syntax transform; unchanged trees are cached by Roslyn. `Collect()` is combined with `CompilationProvider`, and `RegisterSourceOutput` resolves keys, builds each `ControlModel` once in a shared cache, and emits all files from one generator instance. No signature strings are built just for comparison. The factory using/alias source is emitted from the same stage, not post-init, so usage analysis sees the pre-generation compilation where names resolve to real types.

The test `Comment_only_edit_keeps_generated_sources_stable_on_one_driver` reuses one `GeneratorDriver` across a real syntax-tree replacement and asserts the generated sources are unchanged.

### 2.5 Transparent factory calls

A generated `global using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;` makes bare `Button()` available in every file. Because that also makes same-named types resolve as method groups, `TypeAliasResolver` emits `global using X = <resolved type>;` for each factory name the app uses as a type (`Path = System.IO.Path`, `Grid = Avalonia.Controls.Grid`, etc.). Names resolving to several types or already having a user `using X = ...` alias are skipped to avoid `CS1537`, and `SVCUIGEN007` warns that those type usages should be namespace-qualified.

### 2.6 XML documentation on generated APIs

Extension classes, setters, bindings, events, style setters, and factory methods carry `///` comments. `DocumentationLookup` handles the compiler-host gap where metadata symbols have no documentation provider:

- source symbols → `GetDocumentationCommentXml()`;
- metadata symbols → the `.xml` file beside the reference, parsed into `<member name="...">` entries and served by `DocumentationCommentId`; fields also try the backing CLR property's `P:` ID;
- factories use the control type's docs, with a synthetic `<summary>` fallback.

Efficiency: reference→path is cached once per assembly identity, each XML file is parsed once, and each formatted entry is cached once — none of it repeats per member or per keystroke.

---

## 3. API and compatibility changes

**Removed:** both `GenerateMarkupExtensionsFor*` attributes, `AutoFrameworkAssemblies`, `GetTargetAssemblies`, `AddAvaloniaTargets`, `AddAssemblyAttributeTarget`, `AddTargetAssembly`, `HasGeneratePublicExtensions`, `TargetAssembly`, `GetPublicClasses`, `SVCUIGEN001`, duplicate-provider checks, public-extension publishing, `LocalControlExtensionsGenerator`, `ViewFactoryRegistrationGenerator`.

**Added:** `GenerateMarkupControlAttribute` + the `typeof` list in `CommonControls.cs`; `Usage/` (`UsageNameProvider`, `ControlCatalog`, `UsageResolver`, `UsageResult`, `CommonControls`, `TypeAliasResolver`, `TypeKey`); `DocumentationLookup`; `UsageDrivenMarkupGenerator`. `ExternalAssemblyExtensionsGenerator` and `ControlFactoryGenerator` remain as thin wrappers for direct test hosts.

**Kept:** `SVCSystemsCSharpUIGeneratorMetadataAttribute` (`Ignore`, `Alias`, `GenerateFactory`) — now the user's direct control over usage resolution and suppression. The inert `Content` option was removed.

**Diagnostics:** `SVCUIGEN003` (generation failure, reused), `SVCUIGEN006` (ambiguous control name, Info), and `SVCUIGEN007` (factory name that cannot be aliased, Warning). Registered in `AnalyzerReleases.Unshipped.md`.

The library is in alpha, so these breaking changes (public attributes removed, generated extensions now internal) need no compatibility shims or migration path beyond the `CHANGELOG.md` entry.

---

## 4. Implementation status

| Phase | Status |
| --- | --- |
| 0 — test-first groundwork | Done |
| 1 — usage detection and resolution | Done |
| 2 — route fluent extensions through used set | Done; consolidated into `UsageDrivenMarkupGenerator` |
| 3 — factories, registrations, global using/aliases | Done; registrations cover used controls from any assembly |
| 4 — remove legacy surface, update samples/docs | Done |
| 5 — verification and measurement | Done; numbers below |

## 5. Verification

- `dotnet build` and `dotnet test`: green on net10.0/net11.0, **248/248 tests**.
- Generated sources in the test project: **202 → 70** external extension files (258 → 170 total generated files).
- Measured full-solution builds (two runs each, HEAD worktree vs current tree):

| Metric | Before | After | Change |
| --- | --- | --- | --- |
| Clean rebuild | 8.74s / 8.39s | 6.43s / 6.18s | ~26% faster |
| Incremental (one view file edited) | 2.99s / 3.18s | 1.79s / 1.60s | ~45% faster |

The three samples compile and run with no generation attributes; inherited setters, attached setters, events, style setters, factories, `SourceInfo`, and `ViewFactory.Create<T>()` all work.

## 6. Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Control used only in compiled XAML, reflection, or a runtime-computed name | Local declarations are roots; referenced-library controls are found by name; `[assembly: GenerateMarkupControl(typeof(X))]` in the app is a narrow per-type opt-in. |
| Inherited setters break without closure | R4 base-type closure; sample compilation is the gate. |
| Name collisions | R3 preference + include-all + `SVCUIGEN006`. |
| Global static import shadows types | `TypeAliasResolver` emits global aliases; when a name cannot be aliased (multiple resolved types or a user alias), `SVCUIGEN007` tells the user to qualify type usages. |
| Metadata docs unavailable in compiler host | `DocumentationLookup` reads the XML beside each reference, cached at three levels. |
| Breaking change for consumers | Alpha library — breaking changes are acceptable; recorded in `CHANGELOG.md`. |
| Stale generated files when usage is removed | Source output emits the full set each run; Roslyn drops files not re-added. |

## 6b. Review hardening

Follow-up from PR review:

- The catalog records base-type symbols, so constructed generic bases (`Derived : GenericBase<int>`) resolve to real symbols and get their setters generated; open definitions are suppressed when a constructed counterpart exists.
- Referenced assemblies are discovered through transitive Avalonia references, nested control types are enumerated, and referenced controls are indexed by both factory alias and CLR name.
- Attached-property hosts are rooted from generated extension names (`Host_Property`), including non-control hosts.
- Name collisions now keep all distinct candidates instead of preferring one.
- Global aliases are skipped (with `SVCUIGEN007`) for inaccessible types, generic containers, user aliases, multiple resolved types, and global namespace conflicts.
- `ViewFactory` registrations are emitted for every eligible control even when factory names collide.
- Generation result is equatable and registered as the source-output input, so Roslyn caches unchanged files across edits.
- Generator tests resolve the Avalonia reference path from MSBuild instead of a hard-coded NuGet cache.

## 7. Remaining work

- The working tree is uncommitted; no release tag has been created.
