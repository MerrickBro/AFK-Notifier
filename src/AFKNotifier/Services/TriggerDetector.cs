using System.Text;

namespace AFKNotifier.Services;

public static class TriggerDetector
{
    private static readonly Dictionary<string, string> SpokenLetterNames = new(StringComparer.Ordinal)
    {
        ["AY"] = "A",
        ["BEE"] = "B",
        ["SEE"] = "C",
        ["SEA"] = "C",
        ["DEE"] = "D",
        ["EFF"] = "F",
        ["GEE"] = "G",
        ["AITCH"] = "H",
        ["EYE"] = "I",
        ["JAY"] = "J",
        ["KAY"] = "K",
        ["EL"] = "L",
        ["EM"] = "M",
        ["EN"] = "N",
        ["OH"] = "O",
        ["PEE"] = "P",
        ["CUE"] = "Q",
        ["QUEUE"] = "Q",
        ["ARE"] = "R",
        ["ESS"] = "S",
        ["TEE"] = "T",
        ["YOU"] = "U",
        ["VEE"] = "V",
        ["EX"] = "X",
        ["WHY"] = "Y",
        ["ZEE"] = "Z",
        ["ZED"] = "Z"
    };

    public static bool Matches(string recognizedText, string triggerPhrase)
    {
        var recognizedTokens = Tokenize(recognizedText);
        var triggerTokens = Tokenize(triggerPhrase);

        if (recognizedTokens.Count == 0 || triggerTokens.Count == 0)
        {
            return false;
        }

        if (ContainsSequence(recognizedTokens, triggerTokens))
        {
            return true;
        }

        var triggerAcronym = GetAcronym(triggerTokens);
        if (triggerAcronym is null)
        {
            return false;
        }

        for (var start = 0; start < recognizedTokens.Count; start++)
        {
            var compact = new StringBuilder();

            for (var index = start; index < recognizedTokens.Count && compact.Length <= triggerAcronym.Length; index++)
            {
                var normalized = NormalizeAcronymToken(recognizedTokens[index]);
                if (normalized is null)
                {
                    break;
                }

                compact.Append(normalized);

                if (compact.Length == triggerAcronym.Length &&
                    compact.ToString().Equals(triggerAcronym, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!triggerAcronym.StartsWith(compact.ToString(), StringComparison.Ordinal))
                {
                    break;
                }
            }
        }

        return false;
    }

    private static string? GetAcronym(IReadOnlyList<string> triggerTokens)
    {
        if (triggerTokens.Count == 1 && triggerTokens[0].Length is >= 2 and <= 8)
        {
            return triggerTokens[0];
        }

        if (triggerTokens.Count is >= 2 and <= 8 && triggerTokens.All(token => token.Length == 1))
        {
            return string.Concat(triggerTokens);
        }

        return null;
    }

    private static string? NormalizeAcronymToken(string token)
    {
        if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
        {
            return token;
        }

        if (SpokenLetterNames.TryGetValue(token, out var letter))
        {
            return letter;
        }

        if (token.Length <= 8 && token.All(character => character is >= 'A' and <= 'Z'))
        {
            return token;
        }

        return null;
    }

    private static bool ContainsSequence(IReadOnlyList<string> source, IReadOnlyList<string> target)
    {
        if (target.Count > source.Count)
        {
            return false;
        }

        for (var start = 0; start + target.Count <= source.Count; start++)
        {
            var matches = true;
            for (var index = 0; index < target.Count; index++)
            {
                if (!source[start + index].Equals(target[index], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToUpperInvariant(character));
                continue;
            }

            FlushToken(current, tokens);
        }

        FlushToken(current, tokens);
        return tokens;
    }

    private static void FlushToken(StringBuilder current, ICollection<string> tokens)
    {
        if (current.Length == 0)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
    }
}
