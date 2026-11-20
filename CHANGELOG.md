# Changelog

## Unreleased

### Breaking changes

- Renamed the package, assemblies, projects and namespaces from `CSharpUI.Avalonia` to `SVCSystems.Avalonia.CSharpUI`.
- Renamed `CSharpUIGeneratorMetadataAttribute` to `SVCSystemsCSharpUIGeneratorMetadataAttribute` and analyzer IDs `CSUIGEN###` to `SVCUIGEN###`.
- Migrated the core package to Avalonia 12 and .NET 10/.NET 11.
- Removed ReactiveUI-specific and legacy MVU APIs from the core library.
- Removed `GenerateExtensionsForAssemblyAttribute`, `GenerateMarkupExtensionsForAssemblyAttribute`, and `GenerateMarkupExtensionsForAvaloniaAttribute`. Generation is now usage-driven.
- Generated extensions are always `internal` to the consuming compilation; libraries no longer publish public generated extensions, and diagnostic `SVCUIGEN001` was removed.

### Added

- Added usage-driven generation: extensions, factories, and registrations are emitted only for controls mentioned in source plus their base-type closure. A curated, compile-time-checked `typeof(...)` list of common Avalonia controls is always generated (`GenerateMarkupControlAttribute`), and the same attribute in an application is a narrow per-type opt-in for controls used only from compiled XAML. Ambiguous control names report `SVCUIGEN006`; factory names that cannot be aliased report `SVCUIGEN007`.
- Added shared public extensions: `[assembly: GeneratePublicMarkupExtensions]` in a library publishes its generated extensions as public APIs and emits `PublicMarkupExtensionControlsAttribute`. Referencing projects skip generation for covered controls, removing duplicate internal extensions between friend assemblies and reducing build time.
- Control factories are now imported globally by the generator, so `Button()` works in any source file without a `using static` directive. Generated type aliases keep same-named types usable (`Path.Combine`, `new Grid()`, etc.).
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

### Fixed

- The generator no longer emits instance event helpers for static public events, which previously produced invalid code (`CS0176`) for controls with static events such as `MarkdownRenderer.ConfigurePipeline`.
- `ViewBase.Reload()` no longer accumulates styles produced by `BuildStyles()` on every rebuild; externally added styles are preserved.
- Generated common-property setters now fully qualify Avalonia type names, avoiding `SVCSystems.Avalonia.Controls`/`.Media` resolution errors inside the library namespace.
