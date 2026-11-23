using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;

/// <summary>
/// A fingerprint of every input that can change the output of <see cref="UsageDrivenMarkupGenerator"/>.
/// It is built from *tokens* (plus documentation trivia), so edits that only add comments or
/// whitespace do not change it. That lets Roslyn skip the entire analysis and emit pipeline for
/// the common IDE case where the user types inside a method or edits a comment.
/// </summary>
internal readonly struct GenerationFingerprint : IEquatable<GenerationFingerprint>
{
    internal GenerationFingerprint(ulong hash1, ulong hash2)
    {
        Hash1 = hash1;
        Hash2 = hash2;
    }

    internal ulong Hash1 { get; }

    internal ulong Hash2 { get; }

    public bool Equals(GenerationFingerprint other) => Hash1 == other.Hash1 && Hash2 == other.Hash2;

    public override bool Equals(object? obj) => obj is GenerationFingerprint other && Equals(other);

    public override int GetHashCode() => unchecked((int)(Hash1 ^ (Hash1 >> 32) ^ Hash2 ^ (Hash2 >> 32)));

    private const ulong Offset1 = 14695981039346656037UL;
    private const ulong Prime1 = 1099511628211UL;
    private const ulong Offset2 = 14695981039346656011UL;
    private const ulong Prime2 = 1099511628217UL;

    private sealed class UlongBox
    {
        internal UlongBox(ulong value) => Value = value;

        internal ulong Value { get; }
    }

    private sealed class TreeHash
    {
        internal ulong Hash1;
        internal ulong Hash2;
    }

    // Metadata.Id does not override ToString(), so its string form is the same for every
    // reference. GetHashCode() is derived from the metadata content and is what distinguishes
    // two images of different assemblies (or different versions of the same assembly).
    private static readonly ConditionalWeakTable<PortableExecutableReference, UlongBox> MetadataIdHashCache = new();

    // Non-PE references (for example CompilationReference) expose no stable content id. They get a
    // fresh key on every call so reference-keyed caches cannot reuse results across API changes.
    private static long _nonPeUniqueKey;

    // Roslyn reuses the same SyntaxTree instance for every file that the user did not edit.
    // Caching each tree's token hash means an edit only rehashes the edited file instead of
    // re-walking the whole compilation on every keystroke.
    private static readonly ConditionalWeakTable<SyntaxTree, TreeHash> TreeHashes = new();

    internal static GenerationFingerprint Create(
        ImmutableArray<string> names,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var hash1 = GetReferenceHash(compilation, cancellationToken);
        var hash2 = Offset2;

        foreach (var name in names)
        {
            Add(ref hash1, ref hash2, name);
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Hash every input tree: a ".g.cs" file can still be a user input that affects
            // generation, so the filename must not decide which inputs are part of the key.
            var treeHash = GetTreeHash(tree, cancellationToken);
            hash1 = (hash1 ^ treeHash.Hash1) * Prime1;
            hash2 = (hash2 ^ treeHash.Hash2) * Prime2;
        }

        return new GenerationFingerprint(hash1, hash2);
    }

    /// <summary>Hashes assembly identity and metadata content for every reference.</summary>
    internal static ulong GetReferenceHash(Compilation compilation, CancellationToken cancellationToken = default)
    {
        var hash = Offset1;
        Add(ref hash, compilation.Assembly.Identity.GetDisplayName());
        foreach (var reference in compilation.References)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reference is not PortableExecutableReference)
            {
                // No stable content id is available; give this compilation a unique key so no
                // reference-keyed cache can return a result computed from different metadata.
                hash = (ulong)Interlocked.Increment(ref _nonPeUniqueKey) * Prime1;
                return hash;
            }

            // Display distinguishes file references by path and keeps references that cannot be
            // opened from colliding with each other.
            Add(ref hash, reference.Display ?? string.Empty);

            try
            {
                var metadataHash = MetadataIdHashCache.GetValue((PortableExecutableReference)reference, static reference =>
                    new UlongBox((ulong)(reference.GetMetadata()?.Id.GetHashCode() ?? 0))).Value;
                Add(ref hash, metadataHash.ToString("x"));
            }
            catch (Exception exception) when (exception is BadImageFormatException or IOException)
            {
                // A reference that cannot be opened is already identified by Display.
            }
        }

        return hash;
    }

    /// <summary>
    /// Hashes the token stream of one tree; comments and whitespace are ignored because they
    /// cannot change generated output or semantic binding. The result is cached for the lifetime
    /// of the tree, which Roslyn keeps stable across compilations for unchanged files.
    /// </summary>
    private static TreeHash GetTreeHash(SyntaxTree tree, CancellationToken cancellationToken)
    {
        if (TreeHashes.TryGetValue(tree, out var cached))
        {
            return cached;
        }

        ulong hash1 = Offset1;
        ulong hash2 = Offset2;
        Add(ref hash1, ref hash2, tree.FilePath ?? string.Empty);
        foreach (var token in tree.GetRoot(cancellationToken).DescendantTokens(descendIntoTrivia: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(ref hash1, ref hash2, token.Text);

            // XML documentation is emitted into generated code, so it is part of the fingerprint.
            foreach (var trivia in token.LeadingTrivia)
            {
                if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                    trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                {
                    Add(ref hash1, ref hash2, trivia.ToFullString());
                }
            }
        }

        return TreeHashes.GetValue(tree, _ => new TreeHash { Hash1 = hash1, Hash2 = hash2 });
    }

    /// <summary>Key for a tree's cached type bindings in a given reference set.</summary>
    internal static ulong GetTreeKey(SyntaxTree tree, ulong referenceHash)
    {
        var treeHash = GetTreeHash(tree, default);
        var hash = referenceHash;
        hash = (hash ^ treeHash.Hash1) * Prime1;
        hash = (hash ^ treeHash.Hash2) * Prime2;
        return hash;
    }

    private static void Add(ref ulong hash, string value)
    {
        unchecked
        {
            foreach (var ch in value)
            {
                hash = (hash ^ ch) * Prime1;
            }

            hash = (hash ^ 0xFFFF) * Prime1;
        }
    }

    private static void Add(ref ulong hash1, ref ulong hash2, string value)
    {
        unchecked
        {
            foreach (var ch in value)
            {
                hash1 = (hash1 ^ ch) * Prime1;
                hash2 = (hash2 + ch) * Prime2;
            }

            hash1 = (hash1 ^ 0xFFFF) * Prime1;
            hash2 = (hash2 ^ 0xFFFF) * Prime2;
        }
    }
}

/// <summary>
/// Bounded cache of generation results. A hit returns the very same immutable result instance,
/// which also makes the incremental comparer O(1) via reference equality.
/// </summary>
internal static class GenerationCache
{
    private const int MaxEntries = 4;
    private static readonly ConcurrentDictionary<GenerationFingerprint, GenerationResult> Results = new();

    internal static ulong GetReferenceHash(Compilation compilation, CancellationToken cancellationToken = default) =>
        GenerationFingerprint.GetReferenceHash(compilation, cancellationToken);

    internal static ulong GetTreeKey(SyntaxTree tree, ulong referenceHash) =>
        GenerationFingerprint.GetTreeKey(tree, referenceHash);

    internal static GenerationResult GetOrAdd(
        GenerationFingerprint fingerprint,
        Func<GenerationResult> create)
    {
        var result = Results.GetOrAdd(fingerprint, _ => create());
        if (Results.Count > MaxEntries)
        {
            // Generation is deterministic, so dropping cached entries is always safe.
            Results.Clear();
        }

        return result;
    }
}
