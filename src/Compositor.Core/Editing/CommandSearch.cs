namespace Compositor.Core.Editing;

/// <summary>
/// The command palette's matching, as launchers do it: the query's letters must appear in order; each one scores,
/// more at the start of a word and more again right after the previous match, so "gb" finds Gaussian Blur before
/// Debug. Mirrors CommandPaletteSearch in CommandPalette.swift.
/// </summary>
public static class CommandSearch
{
    public static int? Score(string query, string text)
    {
        var wanted = query.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray();
        if (wanted.Length == 0) return 0;
        var letters = text.ToLowerInvariant();
        int total = 0, from = 0, previous = -2;
        foreach (var character in wanted)
        {
            int found = from < letters.Length ? letters.IndexOf(character, from) : -1;
            if (found < 0) return null;
            bool wordStart = found == 0 || !char.IsLetterOrDigit(letters[found - 1]) || IsIdeograph(letters[found]);
            total += 1 + (wordStart ? 8 : 0) + (found == previous + 1 ? 5 : 0);
            previous = found;
            from = found + 1;
        }
        // How the typed text was found, best first: whole at the start of a word; each typed word starting a word, in
        // order; whole inside a word; spaces aside; and last the letters scattered, as an abbreviation.
        var phrase = query.ToLowerInvariant().Trim();
        var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int tier;
        if (WordsStartWords(new[] { phrase }, letters)) tier = 5;
        else if (words.Length > 1 && WordsStartWords(words, letters)) tier = 4;
        else if (letters.Contains(phrase)) tier = 3;
        else if (letters.Replace(" ", "").Contains(new string(wanted))) tier = 2;
        else tier = 1;
        // Found the same way, a clearly shorter title is the likelier one; scattered letters rank by how well they fall.
        return tier * 1_000_000 + (tier == 1 ? total * 100 : 0) - letters.Length / 4;
    }

    /// <summary>Chinese text has no spaces: every character starts a "word".</summary>
    private static bool IsIdeograph(char c) => c >= '㐀' && c <= '鿿';

    /// <summary>Each word starts a word of <paramref name="title"/>, in order.</summary>
    private static bool WordsStartWords(IEnumerable<string> words, string title)
    {
        int from = 0;
        foreach (var word in words)
        {
            int? found = null;
            int search = from;
            while (search <= title.Length)
            {
                int at = title.IndexOf(word, search, StringComparison.Ordinal);
                if (at < 0) break;
                if (at == 0 || !char.IsLetter(title[at - 1]) || IsIdeograph(title[at])) { found = at + word.Length; break; }
                search = at + 1;
            }
            if (found is not { } next) return false;
            from = next;
        }
        return true;
    }

    /// <summary>
    /// The entries that match, best first, with enabled ones ahead of disabled; with no query, all of them in their
    /// order, enabled first.
    /// </summary>
    public static List<T> Rank<T>(IReadOnlyList<T> entries, string query, Func<T, string> title, Func<T, bool> isEnabled)
    {
        var scored = entries.Select((entry, index) => (Entry: entry, Score: Score(query, title(entry)), Index: index))
            .Where(e => e.Score is not null)
            .ToList();
        scored.Sort((a, b) =>
        {
            bool ae = isEnabled(a.Entry), be = isEnabled(b.Entry);
            if (ae != be) return ae ? -1 : 1;
            if (string.IsNullOrWhiteSpace(query) || a.Score == b.Score) return a.Index.CompareTo(b.Index);
            return b.Score!.Value.CompareTo(a.Score!.Value);
        });
        return scored.Select(e => e.Entry).ToList();
    }
}
