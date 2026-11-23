#nullable enable

using Microsoft.CodeAnalysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;

internal sealed class EventExtensionInfo : IMemberExtensionInfo
{
    public string MemberName { get; }
    public string ControlTypeName { get; }
    public string EventHandler { get; }
    public string EventName { get; }
    public string ExtensionName { get; }
    public List<string> EventParameterTypes { get; } = [];
    public bool ReturnsVoid { get; private set; } = true;
    public string ReturnType { get; }
    public string XmlDoc { get; }
    public string GenericConstraint { get; } = "";
    public string GenericArg { get; } = "";
    public bool IsRoutedEvent { get; }
    public bool SupportsAddHandler { get; }
    public bool IsObsolete { get; }

    public bool HasStandardSignature =>
        EventParameterTypes.Count == 2 &&
        EventParameterTypes[0] == "global::System.Object?" &&
        EventParameterTypes[1].Contains("EventArgs", StringComparison.Ordinal);

    public EventExtensionInfo(IEventSymbol eventInfo, string? extensionName = null, Compilation? compilation = null)
    {
        ControlTypeName = eventInfo.ContainingType.GetFullTypeName();
        EventName = eventInfo.Name;
        ExtensionName = extensionName ?? eventInfo.Name;
        MemberName = EventName;
        EventHandler = eventInfo.Type.GetFullTypeName();
        XmlDoc = SymbolUtilities.FormatXmlDoc(eventInfo, compilation);
        IsObsolete = eventInfo.IsObsolete();
        var delegateType = eventInfo.Type as INamedTypeSymbol;
        var invokeMethod = delegateType?.DelegateInvokeMethod;
        IsRoutedEvent = IsRoutedEventSymbol(eventInfo, invokeMethod);
        SupportsAddHandler = eventInfo.ContainingType.IsOrInheritsFrom("Avalonia.Interactivity.Interactive");

        if (!eventInfo.ContainingType.IsSealed)
        {
            ReturnType = "T";
            GenericConstraint = $"where T : {ControlTypeName}";
            GenericArg = "<T>";
        }
        else
        {
            ReturnType = ControlTypeName;
        }

        if (invokeMethod is not null)
        {
            ReturnsVoid = invokeMethod.ReturnsVoid;

            foreach (var parameter in invokeMethod.Parameters)
            {
                EventParameterTypes.Add(parameter.Type.GetFullTypeName());
            }
        }
    }

    private static bool IsRoutedEventSymbol(IEventSymbol eventInfo, IMethodSymbol? invokeMethod)
    {
        if (invokeMethod is null)
        {
            return false;
        }

        var hasRoutedArgs = invokeMethod.Parameters.Any(static x => x.Type.IsOrInheritsFrom("Avalonia.Interactivity.RoutedEventArgs"));
        if (!hasRoutedArgs)
        {
            return false;
        }

        return eventInfo.ContainingType.GetMembers($"{eventInfo.Name}Event")
            .OfType<IFieldSymbol>()
            .Any(static x => x.IsStatic && x.DeclaredAccessibility == Accessibility.Public);
    }
}
