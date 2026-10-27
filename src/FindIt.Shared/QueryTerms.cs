using System.Text;
using System.Text.RegularExpressions;

namespace FindIt.Shared;

/// <summary>A query word or quoted phrase as keyword search matches it: a prefix of a word, or a whole word or phrase.</summary>
public readonly record struct QueryTerm(string Text, bool Phrase, bool Prefix);

/// <summary>Reads a query's words the way keyword search does, so snippets and previews mark what the search matched.</summary>
public static class QueryTerms
{
    private static readonly HashSet<string> EnglishStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","and","are","as","at","be","been","being","but","by","can","could","did","do","does","for","from","had","has","have","how","i","if","in","into","is","it","its","me","my","of","on","or","our","over","s","she","some","someone","something","than","that","the","their","them","then","there","these","they","this","those","to","under","up","us","was","we","were","what","when","where","which","who","why","will","with","would","you","your"
    };

    public static QueryTerm[] Parse(string query)
    {
        var result = new List<(string Text, bool Phrase)>();
        foreach (Match token in Regex.Matches(query, "\"(?<phrase>[^\"]+)\"|(?<token>[\\p{L}\\p{N}_]+)", RegexOptions.CultureInvariant))
        {
            bool phrase = token.Groups["phrase"].Success;
            string value = phrase ? token.Groups["phrase"].Value.Trim() : token.Groups["token"].Value;
            if (value.Length == 0 || !phrase && (EnglishStopWords.Contains(value) || value.Length == 1 && char.IsLetter(value[0]))) continue;
            result.Add((value, phrase)); if (result.Count == 48) break;
        }
        // Short English words prefix-match unrelated longer ones ("hot" finds "hotel", "air" finds "Airport"), so they match
        // whole words unless they are the last word, still being typed. Other scripts keep the prefix, which stands in for
        // suffixed forms such as Arabic's attached pronouns.
        return result.Select((t, i) => (t.Text, t.Phrase, Prefix: !t.Phrase && (i == result.Count - 1 || t.Text.Length > 3 || !Ascii.IsValid(t.Text))))
            .GroupBy(t => (t.Text.ToLowerInvariant(), t.Phrase)).Select(g => new QueryTerm(g.First().Text, g.Key.Phrase, g.Any(t => t.Prefix))).ToArray();
    }

    /// <summary>Matches the term only where a word starts, and to the end of a word unless it is a prefix: "get" is not found inside "together".</summary>
    public static string? Pattern(QueryTerm term)
    {
        var words = Regex.Matches(term.Text, @"[\p{L}\p{N}]+").Select(m => Regex.Escape(m.Value)).ToArray();
        return words.Length == 0 ? null : @"(?<![\p{L}\p{N}])" + string.Join(@"[^\p{L}\p{N}]+", words) + (term.Prefix ? "" : @"(?![\p{L}\p{N}])");
    }

    /// <summary>Finds every word of the query that keyword search would match, longest first; null when there is none.</summary>
    public static Regex? Highlighter(string query)
    {
        var patterns = Parse(query).OrderByDescending(t => t.Text.Length).Select(Pattern).OfType<string>().ToArray();
        return patterns.Length == 0 ? null : new Regex(string.Join("|", patterns), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    }
}
