namespace ElunaWorkbench.Core.Analysis;

public static class TextUtil
{
    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Returns the best matching candidates for a misspelled name ("Meinten Sie ...?").</summary>
    public static List<string> Suggest(string name, IEnumerable<string> candidates, int max = 3)
    {
        var lower = name.ToLowerInvariant();
        var scored = new List<(string Name, int Score)>();
        foreach (var c in candidates.Distinct())
        {
            if (c == name) continue;
            var cl = c.ToLowerInvariant();
            int score;
            if (cl == lower) score = 0; // only case differs
            else
            {
                var d = Levenshtein(lower, cl);
                var limit = Math.Max(1, Math.Min(3, name.Length / 3));
                if (d > limit) continue;
                score = d + 1;
            }
            scored.Add((c, score));
        }
        return scored.OrderBy(s => s.Score).ThenBy(s => s.Name, StringComparer.Ordinal).Take(max).Select(s => s.Name).ToList();
    }

    /// <summary>Fuzzy "subsequence" match used by the completion list ("gpbn" matches "GetPlayerByName").</summary>
    public static bool FuzzyMatch(string candidate, string typed)
    {
        if (typed.Length == 0) return true;
        if (candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return true;
        if (candidate.Contains(typed, StringComparison.OrdinalIgnoreCase)) return true;
        var i = 0;
        foreach (var ch in candidate)
        {
            if (char.ToLowerInvariant(ch) == char.ToLowerInvariant(typed[i]))
            {
                i++;
                if (i == typed.Length) return true;
            }
        }
        return false;
    }
}
