using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static SVCSystems.Avalonia.CSharpUI.SourceGenerator.MarkupTypeHelpers;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;

public class AvaloniaEventExtensionsGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
#if DEBUG
        if (!Debugger.IsAttached)
        {
            //Debugger.Launch();
        }
#endif
        Debug.WriteLine("Initialize AvaloniaEventExtensionsGenerator code generator");

        // Shares one class-declaration pass with the property generator when both are registered.
        ViewExtensionGenerator.Register(context, emitPropertyExtensions: false, emitEventExtensions: true);
    }

    internal static (string HintName, string Text)? GenerateSource(
        ClassDeclarationSyntax classDecl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = classDecl.SyntaxTree.GetRoot(cancellationToken);

        var ns = root
            .DescendantNodes()
            .FirstOrDefault(x => x is BaseNamespaceDeclarationSyntax) as BaseNamespaceDeclarationSyntax;

        var typeNamespace = ns?.Name.ToString() ?? string.Empty;

        var sb = new StringBuilder();

        sb.AppendLine("#nullable enable");
        sb.AppendLine("// Auto-generated code");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Runtime.CompilerServices;");

        if (root is CompilationUnitSyntax compilationUnit)
        {
            foreach (var usingDirective in compilationUnit.Usings)
            {
                sb.AppendLine(usingDirective.ToString());
            }
        }

        if (!string.IsNullOrWhiteSpace(typeNamespace))
            sb.AppendLine($"using {typeNamespace};");

        // Build the nested-qualified name (e.g., Outer.Inner for a nested class) so we can reference nested types.
        static string GetNestedQualifiedName(ClassDeclarationSyntax cls)
        {
            var names = new System.Collections.Generic.List<string>();
            SyntaxNode? node = cls;
            while (node is ClassDeclarationSyntax cds)
            {
                names.Insert(0, cds.Identifier.ToString());
                node = node.Parent;
            }
            return string.Join(".", names);
        }

        // The name to use in method signatures (within the containing namespace scope via using above)
        var nestedQualifiedTypeName = GetNestedQualifiedName(classDecl);

        // Create a unique and valid extension class/file name that includes namespace and nesting
        var safeId = NamingConventions.NestedPathIdentifier(typeNamespace, nestedQualifiedTypeName);

        sb.AppendLine("namespace SVCSystems.Avalonia.CSharpUI;");
        sb.AppendLine($"public static partial class {safeId}EventExtensions");
        sb.AppendLine("{");

        var processedMembersCount = 0;
        foreach (var member in classDecl.Members)
        {
            if (member is not EventFieldDeclarationSyntax @event)
                continue;

            var extensionString = GetEventExtension(nestedQualifiedTypeName, @event);
            if (string.IsNullOrWhiteSpace(extensionString))
                continue;

            sb.AppendLine(extensionString);
            processedMembersCount++;
        }

        sb.AppendLine("}");

        return processedMembersCount > 0
            ? ($"{safeId}EventExtensions.g.cs", sb.ToString())
            : null;
    }

    // Rewritten for clarity: separates logic for EventHandler, EventHandler<T>, Action, and Action<T>.
    private static string GetEventExtension(string controlTypeName, EventFieldDeclarationSyntax @event)
    {
        // Get the event type as string (e.g., EventHandler, EventHandler<T>, Action, Action<T>)
        var eventTypeSyntax = @event.Declaration.Type;
        var eventTypeString = eventTypeSyntax.ToString();

        // Get the event name (e.g., Click)
        var eventName = @event.Declaration.Variables[0].ToString();
        var extensionName = $"On{eventName}";

        // Try to get the generic type arguments if present
        var genericName = eventTypeSyntax.DescendantNodesAndSelf().OfType<GenericNameSyntax>().FirstOrDefault();
        var typeArguments = genericName?.TypeArgumentList.Arguments.Select(arg => arg.ToString()).ToArray();

        // Derive the delegate's argument types, then use the shared adapter-shape rule so this
        // generator and ActionToEventGenerator always produce the same Action signature.
        // EventHandler/EventHandler<T> have the standard (sender, event-data) signature: the public
        // callback takes only the event data, but the subscribed lambda must accept both arguments.
        var isEventHandler = eventTypeString.StartsWith("EventHandler", StringComparison.Ordinal);
        var isAction = eventTypeString.StartsWith("Action", StringComparison.Ordinal);
        string[] parameterTypes;
        var hasStandardSignature = false;
        if (isEventHandler)
        {
            var eventArgsType = typeArguments is { Length: 1 } ? typeArguments[0] : "global::System.EventArgs";
            parameterTypes = ["global::System.Object", eventArgsType];
            hasStandardSignature = true;
        }
        else if (isAction)
        {
            parameterTypes = typeArguments is { Length: 1 } ? [typeArguments[0]] : [];
        }
        else
        {
            parameterTypes = [];
        }

        var shape = EventAdapterShapes.For(parameterTypes, hasStandardSignature);
        var parameterList = shape.ParameterList;
        var lambdaParameters = shape.LambdaParameters;
        var lambdaBody = shape.ActionCall;

        // Compose the extension method
        var extensionText =
            $"    public static {controlTypeName} {extensionName}(this {controlTypeName} control, {parameterList})\n"
            + "    {\n"
            + $"        control.{eventName} += ({lambdaParameters}) => {lambdaBody};\n"
            + "        return control;\n"
            + "    }";

        return extensionText;
    }
}
