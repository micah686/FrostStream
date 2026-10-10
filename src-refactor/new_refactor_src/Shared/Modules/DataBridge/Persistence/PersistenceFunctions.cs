using System.Text;

namespace DataBridge.Persistence;

public static class PersistenceFunctions
{
    public static string GuidText(Guid value) => value.ToString("D");

    /// <summary>Translated to native PostgreSQL ILIKE or SQLite's Unicode-aware registered predicate.</summary>
    public static bool ILike(string? value, string? pattern, string escape)
    {
        if (value is null || pattern is null) return false;
        if (escape.Length != 1) throw new ArgumentException("LIKE escape must be one character.", nameof(escape));
        var text = value.EnumerateRunes().ToArray();
        var source = pattern.EnumerateRunes().ToArray();
        var tokens = new List<Token>(source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            var rune = source[index];
            if (rune.Value == escape[0])
            {
                if (++index == source.Length) throw new ArgumentException("LIKE pattern must not end with an escape.", nameof(pattern));
                tokens.Add(new Token(source[index], Wildcard.None));
            }
            else tokens.Add(new Token(rune, rune.Value switch { '%' => Wildcard.Many, '_' => Wildcard.One, _ => Wildcard.None }));
        }

        // Greedy wildcard matching avoids regex compilation limits and exponential backtracking.
        // A '_' consumes one Unicode scalar, including an astral character, rather than one UTF-16 unit.
        var textIndex = 0;
        var tokenIndex = 0;
        var starIndex = -1;
        var retryIndex = 0;
        while (textIndex < text.Length)
        {
            if (tokenIndex < tokens.Count && tokens[tokenIndex].Wildcard == Wildcard.Many)
            {
                starIndex = tokenIndex++;
                retryIndex = textIndex;
            }
            else if (tokenIndex < tokens.Count && (tokens[tokenIndex].Wildcard == Wildcard.One
                || Rune.ToLowerInvariant(tokens[tokenIndex].Literal) == Rune.ToLowerInvariant(text[textIndex])))
            {
                textIndex++;
                tokenIndex++;
            }
            else if (starIndex >= 0)
            {
                tokenIndex = starIndex + 1;
                textIndex = ++retryIndex;
            }
            else return false;
        }
        while (tokenIndex < tokens.Count && tokens[tokenIndex].Wildcard == Wildcard.Many) tokenIndex++;
        return tokenIndex == tokens.Count;
    }

    private enum Wildcard { None, One, Many }
    private readonly record struct Token(Rune Literal, Wildcard Wildcard);
}
