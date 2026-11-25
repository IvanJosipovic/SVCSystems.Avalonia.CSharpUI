# Changelog

## Unreleased

### Breaking changes

- Renamed the package, assemblies, projects and namespaces from `CSharpUI.Avalonia` to `SVCSystems.Avalonia.CSharpUI`.
- Renamed `CSharpUIGeneratorMetadataAttribute` to `SVCSystemsCSharpUIGeneratorMetadataAttribute` and analyzer IDs `CSUIGEN###` to `SVCUIGEN###`.
- Migrated the core package to Avalonia 12 and .NET 10/.NET 11.
- Removed ReactiveUI-specific and legacy MVU APIs from the core library.
- Removed `GenerateExtensionsForAssemblyAttribute`, `GenerateMarkupExtensionsForAssemblyAttribute`, and `GenerateMarkupExtensionsForAvaloniaAttribute`. Generation is now usage-driven.
- Generated extensions are `internal` to the consuming compilation by default; libraries can publish them explicitly with `[assembly: GeneratePublicMarkupExtensions]`. Diagnostic `SVCUIGEN001` was removed.

### Added

- Added usage-driven generation: extensions, factories, and registrations are emitted only for controls mentioned in source plus their base-type closure. A curated, compile-time-checked `typeof(...)` list of common Avalonia controls is always generated (`GenerateMarkupControlAttribute`), and the same attribute in an application is a narrow per-type opt-in for controls used only from compiled XAML. Ambiguous control names report `SVCUIGEN006`; colliding factory names report `SVCUIGEN009` at the declaration.
- Added shared public extensions: `[assembly: GeneratePublicMarkupExtensions]` in a library publishes its generated extensions as public APIs and emits `PublicMarkupExtensionControlsAttribute`. Referencing projects skip generation for covered controls, removing duplicate internal extensions between friend assemblies and reducing build time.
- Bare control factory calls are now consumer-controlled: use a per-file `using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;`, a project-level `<Using Static="..." />` item, or opt into a generated global import with `[assembly: GenerateGlobalControlFactories]`. Generated type aliases and `SVCUIGEN007` were removed because injected names cannot be reliably reproduced by formatters and IDEs.
- Using an attached-property extension such as `Grid_Row(...)` now roots the type that hosts the property, including hosts declared on plain non-control classes, so the setter is generated even when the host type is never named in source.
- Generated extension classes, setters, bindings, events, style setters, and factory methods now carry XML documentation. Documentation for referenced controls is read from the XML files shipped beside the reference assemblies, since the compiler host does not attach them to metadata symbols.
- Added a semantic control model for generated setters, bindings, events, attached properties, styles, factories, and view-factory registrations.
- Added generated control factories with source file, line, view, and control metadata.
- Added runtime diagnostics and external assembly generation checks.
- Added reflection-free compiled XAML resource factories via `GenerateXamlResourceAttribute` and `XamlResourceGenerator` (.NET 10+; `SVCUIGEN002/004/005`).
- Ported the upstream binding test matrix (brush, cast expressions, commands, auto-conversion, list/tree/numeric controls, styles, templates, view models, `BindingBase` overloads).
- Ported upstream behavioral tests for build diagnostics, external property sets, routed events, modal hosts, custom controls, and attached properties.
- Added regression tests for repeated view reloads.
- Added Basic, MVVM, and Custom Controls samples.
- Added the `SVCUI001` analyzer and code fix: reports a factory name shadowing a type in a member-access expression and offers a `global::` qualification that survives simplification.
- Added `ReloadAsync()` for deterministic, observable view reloads; `Reload()` remains for `IReloadable` compatibility.
- Added `[assembly: GenerateGlobalControlFactories]` as an explicit opt-in for a generated global static factory import.

### Fixed

- Shared public extensions no longer publish types with inaccessible type arguments, no longer drop setters for controls excluded from the publication filter, and no longer emit duplicate setters for declarative views (which are already public via the property/event generators). Overlapping independent publishers now report `SVCUIGEN008` while coverage propagated from a single publisher is accepted.
- Generated `<see cref>` documentation for generic controls now uses XML documentation-ID syntax (`{...}` for type arguments) instead of raw C# angle brackets, which produced malformed XML (`CS1570`) for controls such as LiveCharts `XamlGaugeSeries<TVisual, TLabel>`.
- The generator no longer emits instance event helpers for static public events, which previously produced invalid code (`CS0176`) for controls with static events such as `MarkdownRenderer.ConfigurePipeline`.
- `ViewBase.Reload()` no longer accumulates styles produced by `BuildStyles()` on every rebuild; externally added styles are preserved.
- Generated common-property setters now fully qualify Avalonia type names, avoiding `SVCSystems.Avalonia.Controls`/`.Media` resolution errors inside the library namespace.
- `ViewBase.Build()` returning a non-`Control` now throws a descriptive `ViewBuildingException` instead of silently setting `Child` to null.
- `ViewFactoryRegistry.Register` is now first-wins and records a diagnostic on duplicate registration instead of silently overwriting.
- AppBuilder configuration is latched: a second builder setting a different value now throws instead of silently replacing process-wide state.
- Style selectors accept an explicit `treatAsTraversal` override; converter failures in style bindings now carry caller file/line.
- Build diagnostics now include a per-view correlation id.
- Removed `TypeAliasResolver` and the generated global aliases; the post-init global-usings source now carries a `// <auto-generated />` header.
