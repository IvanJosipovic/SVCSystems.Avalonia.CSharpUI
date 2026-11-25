namespace SVCSystems.Avalonia.CSharpUI.Analyzers;

/// <summary>
/// Jaro-Winkler similarity used by the did-you-mean rules.
/// </summary>
/// <remarks>
/// Kept local to the analyzer assembly: <c>netstandard2.0</c> hosts cannot depend on a helper
/// shipped in the CLI or generator. Any duplicate must be covered by a bit-identical parity test.
/// The implementation is case-sensitive; callers normalise casing if they need it.
/// </remarks>
internal static class StringSimilarity
{
    internal static double JaroWinkler(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1.0;
        }

        if (left.Length == 0 || right.Length == 0)
        {
            return 0.0;
        }

        var matchWindow = Math.Max(left.Length, right.Length) / 2 - 1;
        if (matchWindow < 0)
        {
            matchWindow = 0;
        }

        var leftMatched = new bool[left.Length];
        var rightMatched = new bool[right.Length];
        var matches = 0;

        for (var i = 0; i < left.Length; i++)
        {
            var start = Math.Max(0, i - matchWindow);
            var end = Math.Min(i + matchWindow + 1, right.Length);
            for (var j = start; j < end; j++)
            {
                if (rightMatched[j] || left[i] != right[j])
                {
                    continue;
                }

                leftMatched[i] = true;
                rightMatched[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0)
        {
            return 0.0;
        }

        var transpositions = 0;
        var rightIndex = 0;
        for (var i = 0; i < left.Length; i++)
        {
            if (!leftMatched[i])
            {
                continue;
            }

            while (!rightMatched[rightIndex])
            {
                rightIndex++;
            }

            if (left[i] != right[rightIndex])
            {
                transpositions++;
            }

            rightIndex++;
        }

        var jaro = ((double)matches / left.Length
                    + (double)matches / right.Length
                    + (double)(matches - transpositions / 2.0) / matches) / 3.0;

        var prefix = 0;
        while (prefix < 4 && prefix < left.Length && prefix < right.Length && left[prefix] == right[prefix])
        {
            prefix++;
        }

        return jaro + prefix * 0.1 * (1.0 - jaro);
    }
}
