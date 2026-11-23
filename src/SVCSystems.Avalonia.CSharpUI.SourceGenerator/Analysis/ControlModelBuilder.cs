using System.Runtime.CompilerServices;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Analysis;

internal static class ControlModelBuilder
{
    private sealed class ModelHolder
    {
        internal Dictionary<string, ControlModel> ByAssembly { get; } = new(StringComparer.Ordinal);
    }

    // Metadata type symbols are reused across compilations that share references. Cache the
    // resulting model per (symbol, generated assembly identity) so an edit that does not change
    // a control's API does not rebuild its model and base chain.
    private static readonly ConditionalWeakTable<INamedTypeSymbol, ModelHolder> ModelCache = new();

    internal static ControlModel Build(INamedTypeSymbol type, IAssemblySymbol? generatedAssembly = null)
    {
        var assemblyKey = generatedAssembly?.Identity.Name ?? string.Empty;
        var holder = ModelCache.GetValue(type, _ => new ModelHolder());
        if (holder.ByAssembly.TryGetValue(assemblyKey, out var cached))
        {
            return cached;
        }

        var perBuild = new Dictionary<INamedTypeSymbol, ControlModel>(SymbolEqualityComparer.Default);
        var model = Build(type, generatedAssembly, perBuild);
        foreach (var pair in perBuild)
        {
            ModelCache.GetValue(pair.Key, _ => new ModelHolder()).ByAssembly[assemblyKey] = pair.Value;
        }

        return model;
    }

    /// <summary>
    /// Builds a model reusing an existing cache. Used by the usage-driven generator so the same
    /// base-chain models are built once per compilation instead of once per used control.
    /// </summary>
    internal static ControlModel Build(
        INamedTypeSymbol type,
        IAssemblySymbol? generatedAssembly,
        Dictionary<INamedTypeSymbol, ControlModel> cache)
    {
        if (cache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        var baseType = type.BaseType is { } baseSymbol
            ? Build(baseSymbol, generatedAssembly, cache)
            : null;

        var declaredProperties = BuildDeclaredProperties(type, baseType);
        var properties = MergeProperties(baseType, declaredProperties);

        var declaredEvents = BuildDeclaredEvents(type, baseType);
        var events = MergeEvents(baseType, declaredEvents);

        var declaredAttachedProperties = type.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => field.IsAttachedPropertyField())
            .Select(static field =>
            {
                var metadata = GetMetadata(field);
                return new AttachedPropertyModel(
                    field,
                    PropertyInheritanceKind.Declared,
                    GetOwnershipKind(field),
                    metadata.Alias ?? field.GetMemberName(),
                    metadata.Ignore);
            })
            .ToArray();
        var attachedProperties = MergeAttachedProperties(baseType, declaredAttachedProperties);

        var stylePropertyFields = GetStylePropertyFields(type);
        var constructors = type.InstanceConstructors
            .Select(constructor => new ConstructorModel(
                constructor,
                IsAccessibleFromGeneratedCode(constructor, generatedAssembly)))
            .ToArray();
        var typeMetadata = GetMetadata(type);

        var model = new ControlModel(
            type,
            baseType,
            constructors,
            declaredProperties,
            properties,
            declaredEvents,
            events,
            declaredAttachedProperties,
            attachedProperties,
            stylePropertyFields,
            typeMetadata.Ignore,
            typeMetadata.GenerateFactory,
            typeMetadata.Alias ?? type.Name);
        cache.Add(type, model);
        return model;
    }

    private static PropertyModel[] MergeProperties(ControlModel? baseType, PropertyModel[] declaredProperties)
    {
        var properties = baseType?.Properties
            .Select(static property => property.AsInherited())
            .ToList() ?? [];

        foreach (var property in declaredProperties)
        {
            if (property.InheritanceKind == PropertyInheritanceKind.Override && property.ClrProperty?.OverriddenProperty is { } overridden)
            {
                var overriddenIndex = properties.FindLastIndex(candidate =>
                    candidate.ClrProperty is not null &&
                    SymbolEqualityComparer.Default.Equals(candidate.ClrProperty, overridden));
                if (overriddenIndex >= 0)
                {
                    properties.RemoveAt(overriddenIndex);
                }
            }
            else if (property.OwnershipKind is AvaloniaPropertyOwnershipKind.AddedOwner or AvaloniaPropertyOwnershipKind.MetadataOverride)
            {
                var ownedPropertyIndex = properties.FindLastIndex(candidate =>
                    StringComparer.Ordinal.Equals(candidate.Name, property.Name));
                if (ownedPropertyIndex >= 0)
                {
                    properties.RemoveAt(ownedPropertyIndex);
                }
            }

            properties.Add(property);
        }

        return properties.ToArray();
    }

    private static EventModel[] MergeEvents(ControlModel? baseType, EventModel[] declaredEvents)
    {
        var events = baseType?.Events
            .Select(static eventModel => eventModel.AsInherited())
            .ToList() ?? [];

        foreach (var eventModel in declaredEvents)
        {
            if (eventModel.InheritanceKind == PropertyInheritanceKind.Override && eventModel.Symbol.OverriddenEvent is { } overridden)
            {
                var overriddenIndex = events.FindLastIndex(candidate =>
                    SymbolEqualityComparer.Default.Equals(candidate.Symbol, overridden));
                if (overriddenIndex >= 0)
                {
                    events.RemoveAt(overriddenIndex);
                }
            }

            events.Add(eventModel);
        }

        return events.ToArray();
    }

    private static AttachedPropertyModel[] MergeAttachedProperties(
        ControlModel? baseType,
        AttachedPropertyModel[] declaredAttachedProperties)
    {
        var attachedProperties = baseType?.AttachedProperties
            .Select(static property => property.AsInherited())
            .ToList() ?? [];

        foreach (var property in declaredAttachedProperties)
        {
            if (property.OwnershipKind is AvaloniaPropertyOwnershipKind.AddedOwner or AvaloniaPropertyOwnershipKind.MetadataOverride)
            {
                var ownedPropertyIndex = attachedProperties.FindLastIndex(candidate =>
                    StringComparer.Ordinal.Equals(candidate.Name, property.Name));
                if (ownedPropertyIndex >= 0)
                {
                    attachedProperties.RemoveAt(ownedPropertyIndex);
                }
            }

            attachedProperties.Add(property);
        }

        return attachedProperties.ToArray();
    }

    private static bool IsAccessibleFromGeneratedCode(IMethodSymbol constructor, IAssemblySymbol? generatedAssembly)
    {
        if (constructor.DeclaredAccessibility == Accessibility.Public)
        {
            return true;
        }

        var isSameAssembly = generatedAssembly is not null &&
            SymbolEqualityComparer.Default.Equals(constructor.ContainingAssembly, generatedAssembly);

        return isSameAssembly &&
            constructor.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal;
    }

    private static PropertyModel[] BuildDeclaredProperties(INamedTypeSymbol type, ControlModel? baseType)
    {
        var inheritedByName = new Dictionary<string, PropertyModel>(StringComparer.Ordinal);
        if (baseType is not null)
        {
            foreach (var property in baseType.Properties)
            {
                if (!inheritedByName.ContainsKey(property.Name))
                {
                    inheritedByName.Add(property.Name, property);
                }
            }
        }

        // One member scan and one name lookup instead of GetMembers() per CLR property.
        var members = type.GetMembers();
        var fieldsByName = new Dictionary<string, IFieldSymbol>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (member is IFieldSymbol field && !fieldsByName.ContainsKey(field.Name))
            {
                fieldsByName.Add(field.Name, field);
            }
        }

        IFieldSymbol? FindAvaloniaField(string propertyName) =>
            fieldsByName.TryGetValue(propertyName + "Property", out var field) && field.IsAvaloniaPropertyField()
                ? field
                : null;

        var declared = new Dictionary<string, PropertyModel>(StringComparer.Ordinal);
        foreach (var property in members.OfType<IPropertySymbol>()
                     .Where(static property => property.DeclaredAccessibility == Accessibility.Public && !property.IsStatic))
        {
            var avaloniaField = FindAvaloniaField(property.Name);
            var propertyMetadata = GetMetadata(property);
            var fieldMetadata = avaloniaField is null ? default : GetMetadata(avaloniaField);
            var inheritanceKind = GetInheritanceKind(property.IsOverride, inheritedByName.ContainsKey(property.Name));
            declared[property.Name] = new PropertyModel(
                property.Name,
                propertyMetadata.Alias ?? fieldMetadata.Alias ?? property.Name,
                type,
                property,
                avaloniaField,
                property.Type,
                inheritanceKind,
                avaloniaField is null ? AvaloniaPropertyOwnershipKind.Registered : GetOwnershipKind(avaloniaField),
                propertyMetadata.Ignore || fieldMetadata.Ignore);
        }

        foreach (var field in members.OfType<IFieldSymbol>())
        {
            if (!field.IsAvaloniaPropertyField())
            {
                continue;
            }

            var name = field.GetMemberName();
            var fieldMetadata = GetMetadata(field);
            if (declared.TryGetValue(name, out var existing))
            {
                if (existing.AvaloniaPropertyField is null)
                {
                    declared[name] = new PropertyModel(
                        existing.Name,
                        fieldMetadata.Alias ?? existing.ExtensionName,
                        type,
                        existing.ClrProperty,
                        field,
                        field.Type.GetLastGenericArgument(),
                        existing.InheritanceKind,
                        GetOwnershipKind(field),
                        existing.IsIgnored || fieldMetadata.Ignore);
                }

                continue;
            }

            inheritedByName.TryGetValue(name, out var inherited);
            var ownershipKind = GetOwnershipKind(field);
            declared[name] = new PropertyModel(
                name,
                fieldMetadata.Alias ?? name,
                type,
                null,
                field,
                field.Type.GetLastGenericArgument(),
                inherited is null
                    ? PropertyInheritanceKind.Declared
                    : ownershipKind is AvaloniaPropertyOwnershipKind.AddedOwner or AvaloniaPropertyOwnershipKind.MetadataOverride
                        ? PropertyInheritanceKind.Inherited
                        : PropertyInheritanceKind.Shadow,
                ownershipKind,
                fieldMetadata.Ignore);
        }

        return declared.Values.ToArray();
    }

    private static EventModel[] BuildDeclaredEvents(INamedTypeSymbol type, ControlModel? baseType)
    {
        var inheritedNames = new HashSet<string>(StringComparer.Ordinal);
        if (baseType is not null)
        {
            foreach (var inheritedEvent in baseType.Events)
            {
                inheritedNames.Add(inheritedEvent.Name);
            }
        }

        return type.GetMembers().OfType<IEventSymbol>()
            .Where(static eventSymbol => eventSymbol.DeclaredAccessibility == Accessibility.Public && !eventSymbol.IsStatic)
            .Select(eventSymbol =>
            {
                var metadata = GetMetadata(eventSymbol);
                return new EventModel(
                    eventSymbol,
                    GetInheritanceKind(eventSymbol.IsOverride, inheritedNames.Contains(eventSymbol.Name)),
                    metadata.Alias ?? eventSymbol.Name,
                    metadata.Ignore);
            })
            .Where(static eventModel => !eventModel.IsIgnored)
            .ToArray();
    }

    private static IReadOnlyList<IFieldSymbol> GetStylePropertyFields(INamedTypeSymbol type)
    {
        IEnumerable<IFieldSymbol> fields = type.GetMembers().OfType<IFieldSymbol>();
        if (type.GetFullTypeName() == "global::Avalonia.StyledElement")
        {
            fields = fields.Concat(type.GetAllMembers()
                .OfType<IFieldSymbol>()
                .Where(field => field.ContainingType is not null &&
                    !SymbolEqualityComparer.Default.Equals(field.ContainingType, type) &&
                    !field.ContainingType.IsOrInheritsFrom("Avalonia.StyledElement")));
        }

        return fields.Where(static field => field.IsAcceptableStyledField()).ToArray();
    }

    private const string MetadataAttributeName = "SVCSystemsCSharpUIGeneratorMetadataAttribute";
    private const string MetadataAttributeFullName = "SVCSystems.Avalonia.CSharpUI." + MetadataAttributeName;
    private sealed class MetadataHolder(GeneratorMetadata metadata)
    {
        internal GeneratorMetadata Metadata { get; } = metadata;
    }

    private static readonly ConditionalWeakTable<ISymbol, MetadataHolder> MetadataCache = new();

    internal static GeneratorMetadata GetMetadata(ISymbol symbol) =>
        MetadataCache.GetValue(symbol, static value => new MetadataHolder(ReadMetadata(value))).Metadata;

    private static GeneratorMetadata ReadMetadata(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            // Attribute decoding is expensive; the simple name rejects almost every attribute
            // without formatting it. Only matching names pay for the full metadata-name check.
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null ||
                attributeClass.Name != MetadataAttributeName ||
                attributeClass.ToDisplayString() != MetadataAttributeFullName)
            {
                continue;
            }

            var ignore = false;
            var generateFactory = true;
            string? alias = null;
            foreach (var argument in attribute.NamedArguments)
            {
                switch (argument.Key)
                {
                    case "Ignore" when argument.Value.Value is bool ignoreValue:
                        ignore = ignoreValue;
                        break;
                    case "GenerateFactory" when argument.Value.Value is bool factoryValue:
                        generateFactory = factoryValue;
                        break;
                    case "Alias" when argument.Value.Value is string aliasValue:
                        alias = aliasValue;
                        break;
                }
            }

            return new GeneratorMetadata(ignore, generateFactory, alias);
        }

        return new GeneratorMetadata(false, true, null);
    }

    private static PropertyInheritanceKind GetInheritanceKind(bool isOverride, bool hidesBaseProperty) =>
        isOverride ? PropertyInheritanceKind.Override :
        hidesBaseProperty ? PropertyInheritanceKind.Shadow :
        PropertyInheritanceKind.Declared;

    internal static AvaloniaPropertyOwnershipKind GetOwnershipKind(IFieldSymbol field)
    {
        var initializer = field.DeclaringSyntaxReferences
            .Select(static reference => reference.GetSyntax())
            .OfType<VariableDeclaratorSyntax>()
            .Select(static variable => variable.Initializer?.Value)
            .FirstOrDefault(static value => value is not null);

        if (initializer is not null)
        {
            // Early exit on the first match; the old code built a string array for every field.
            if (HasInvocation(initializer, "AddOwner"))
            {
                return AvaloniaPropertyOwnershipKind.AddedOwner;
            }

            if (HasInvocation(initializer, "OverrideMetadata"))
            {
                return AvaloniaPropertyOwnershipKind.MetadataOverride;
            }
        }

        return field.ContainingType is null
            ? AvaloniaPropertyOwnershipKind.Inherited
            : AvaloniaPropertyOwnershipKind.Registered;
    }

    private static bool HasInvocation(SyntaxNode initializer, string methodName) =>
        initializer.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation =>
                invocation.Expression switch
                {
                    MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    _ => string.Empty
                } == methodName);
}

internal readonly struct GeneratorMetadata(bool ignore, bool generateFactory, string? alias)
{
    internal bool Ignore { get; } = ignore;

    internal bool GenerateFactory { get; } = generateFactory;

    internal string? Alias { get; } = alias;
}
