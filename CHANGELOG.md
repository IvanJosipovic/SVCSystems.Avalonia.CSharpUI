# Changelog

## Unreleased

### Breaking changes

- Renamed the package, assemblies, projects and namespaces from `CSharpUI.Avalonia` to `SVCSystems.Avalonia.CSharpUI`.
- Renamed `CSharpUIGeneratorMetadataAttribute` to `SVCSystemsCSharpUIGeneratorMetadataAttribute` and analyzer IDs `CSUIGEN###` to `SVCUIGEN###`.
- Migrated the core package to Avalonia 12 and .NET 10/.NET 11.
- Removed ReactiveUI-specific and legacy MVU APIs from the core library.
- Removed the legacy `GenerateExtensionsForAssemblyAttribute`; use `GenerateMarkupExtensionsForAssemblyAttribute` instead.

### Added

- Added a semantic control model for generated setters, bindings, events, attached properties, styles, factories, and view-factory registrations.
- Added generated control factories with source file, line, view, and control metadata.
- Added runtime diagnostics and external assembly generation checks.
- Added reflection-free compiled XAML resource factories via `GenerateXamlResourceAttribute` and `XamlResourceGenerator` (.NET 10+; `SVCUIGEN002/004/005`).
- Ported the upstream binding test matrix (brush, cast expressions, commands, auto-conversion, list/tree/numeric controls, styles, templates, view models, `BindingBase` overloads).
- Ported upstream behavioral tests for build diagnostics, external property sets, routed events, modal hosts, custom controls, and attached properties.
- Added regression tests for repeated view reloads.
- Added Basic, MVVM, and Custom Controls samples.

### Fixed

- `ViewBase.Reload()` no longer accumulates styles produced by `BuildStyles()` on every rebuild; externally added styles are preserved.
- Generated common-property setters now fully qualify Avalonia type names, avoiding `SVCSystems.Avalonia.Controls`/`.Media` resolution errors inside the library namespace.
