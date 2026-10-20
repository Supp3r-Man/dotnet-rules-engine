namespace RulesEngine;

/// <summary>Case-insensitive optimal-string-alignment distance (Levenshtein plus adjacent transposition).</summary>
public static class EditDistance
{
    private const int MaxInputLength = 64;

    public static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    v = Math.Min(v, d[i - 2, j - 2] + 1);
                }

                d[i, j] = v;
            }
        }

        return d[a.Length, b.Length];
    }

    /// <summary>
    /// The closest candidate within min(max(1, input.Length / 3), input.Length - 1) edits (so a one-letter name
    /// is only matched by a case difference), ties broken by ordinal order; null if none.
    /// Inputs longer than 64 characters get no suggestion (keeps the cost bounded).
    /// </summary>
    public static string? Suggest(string input, IEnumerable<string> candidates)
    {
        if (input.Length == 0 || input.Length > MaxInputLength)
        {
            return null;
        }

        int limit = Math.Min(Math.Max(1, input.Length / 3), input.Length - 1);
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string candidate in candidates.Distinct().OrderBy(c => c, StringComparer.Ordinal))
        {
            if (candidate.Length > MaxInputLength)
            {
                continue;
            }

            int distance = Distance(input, candidate);
            if (distance <= limit && distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }
}
