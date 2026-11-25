using System.Collections.Generic;
using System.Linq;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator;

/// <summary>
/// The single rule for turning a .NET event signature into an <c>Action</c> adapter parameter,
/// lambda parameters and call. Both the external-control event generator and the declarative-view
/// event generator use it, so the two paths cannot drift into different adapter shapes.
/// </summary>
internal static class EventAdapterShapes
{
    internal readonly struct Shape
    {
        internal Shape(string parameterList, string lambdaParameters, string actionCall)
        {
            ParameterList = parameterList;
            LambdaParameters = lambdaParameters;
            ActionCall = actionCall;
        }

        internal string ParameterList { get; }
        internal string LambdaParameters { get; }
        internal string ActionCall { get; }
    }

    internal static Shape For(IReadOnlyList<string> parameterTypes, bool hasStandardSignature = false)
    {
        if (parameterTypes.Count == 0)
        {
            return new Shape("Action action", string.Empty, "action()");
        }

        if (hasStandardSignature)
        {
            return new Shape(
                $"Action<{string.Join(", ", parameterTypes.Skip(1))}> action",
                string.Join(", ", parameterTypes.Select((_, index) => $"arg{index}")),
                "action(arg1)");
        }

        if (parameterTypes.Count == 1)
        {
            return new Shape($"Action<{parameterTypes[0]}> action", "arg0", "action(arg0)");
        }

        var lambdaParameters = string.Join(", ", parameterTypes.Select((_, index) => $"arg{index}"));
        return new Shape(
            $"Action<{string.Join(", ", parameterTypes)}> action",
            lambdaParameters,
            $"action({lambdaParameters})");
    }
}
