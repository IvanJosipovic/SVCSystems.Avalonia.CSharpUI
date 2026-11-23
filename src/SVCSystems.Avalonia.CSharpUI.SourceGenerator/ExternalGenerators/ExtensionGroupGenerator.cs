using System.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.Analysis;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;
using Microsoft.CodeAnalysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;

internal sealed class ExtensionGroupGenerator(
    string groupName,
    Func<ControlModel, Compilation?, IEnumerable<IMemberExtensionInfo>> membersQuery,
    params ExtensionGeneratorBase[] generators)
{
    public string GroupName { get; } = groupName;

    public string? Generate(
        ControlModel controlModel,
        Compilation? compilation,
        bool suppressOverlappingSetters,
        out int generationsCount)
    {
        generationsCount = 0;

        var sb = new StringBuilder(1024);
        foreach (var extensionInfo in membersQuery(controlModel, compilation))
        {
            sb.AppendLine($" // {extensionInfo.MemberName}");
            foreach (var generator in generators)
            {
                if (suppressOverlappingSetters && generator.OverlapsWithDeclarativeGenerators)
                {
                    continue;
                }

                var lengthBefore = sb.Length;
                sb.AppendLine();
                sb.AppendLine($"/*{generator.GetType().Name}*/");
                var headerLength = sb.Length - lengthBefore;
                generator.AppendExtension(sb, extensionInfo);
                if (sb.Length == lengthBefore + headerLength)
                {
                    // Nothing was emitted; roll the header back to keep output identical.
                    sb.Length = lengthBefore;
                    continue;
                }

                sb.AppendLine();
                generationsCount++;
            }

            sb.AppendLine();
            sb.AppendLine();
        }

        return generationsCount == 0 ? null : sb.ToString();
    }
}
