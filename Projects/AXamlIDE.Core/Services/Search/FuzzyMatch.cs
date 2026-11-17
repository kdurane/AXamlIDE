using System;

namespace AXamlIDE.Services.Search;

/// <summary>
/// How well a name matches what was typed in Search Everywhere, as Rider matches: the whole name, its start, its word
/// starts ("sev" or "SEV" finds SearchEverywhereViewModel, "tc" finds Toggle Comment), or anywhere in it. Letters
/// merely in order ("main" in "seMAntIc highlightiNg") don't count: they swamp the list with noise.
/// Higher is better; null is no match. Case is ignored.
/// </summary>
public static class FuzzyMatch
{
    public static int? Score(string name, string query)
    {
        query = query.Trim();
        if (query.Length == 0)
            return 0;

        // "toggle com": every word has to be in the name.
        if (query.Contains(' '))
        {
            var total = 0;
            foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Score(name, word) is not { } score)
                    return null;
                total += score;
            }
            return total / 2;
        }

        if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 1000;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 900 - Math.Min(name.Length - query.Length, 100);
        if (MatchesWordStarts(name, query))
            return 700 - Math.Min(name.Length, 100);
        var index = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? 500 - Math.Min(index, 100) : null;
    }

    /// <summary>
    /// Each typed letter starts a word of the name, or carries on the word the letter before it matched in:
    /// "SeEv" and "sev" both match SearchEverywhere. Tried from each word start, so "ev" finds the second word.
    /// </summary>
    private static bool MatchesWordStarts(string name, string query)
    {
        if (name.Length > 200)
            return false;   // the search backtracks: keep it to names, not whole texts
        for (var start = 0; start < name.Length; start++)
            if (IsWordStart(name, start) && Humps(name, start, query, 0))
                return true;
        return false;
    }

    private static bool Humps(string name, int i, string query, int j)
    {
        if (j == query.Length)
            return true;
        if (i >= name.Length || char.ToLowerInvariant(name[i]) != char.ToLowerInvariant(query[j]))
            return false;
        // Carry on in this word...
        if (Humps(name, i + 1, query, j + 1))
            return true;
        // ...or jump to a later word start for the next letter.
        for (var next = i + 1; next < name.Length; next++)
            if (IsWordStart(name, next) && Humps(name, next, query, j + 1))
                return true;
        return false;
    }

    /// <summary>The first letter, a capital after a small letter, or a letter after a space, dot, slash, _ or -.</summary>
    private static bool IsWordStart(string name, int i)
    {
        if (i == 0)
            return true;
        var before = name[i - 1];
        var c = name[i];
        if (before is ' ' or '.' or '_' or '-' or '/' or '\\' or '(' or '<')
            return char.IsLetterOrDigit(c);
        return char.IsUpper(c) && !char.IsUpper(before) || char.IsDigit(c) && !char.IsDigit(before);
    }

}
