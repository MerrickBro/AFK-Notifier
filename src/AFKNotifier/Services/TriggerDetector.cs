using System.Text;

namespace AFKNotifier.Services;

public static class TriggerDetector
{
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

        if (triggerTokens.Count == 1 && triggerTokens[0].Length is >= 2 and <= 8)
        {
            var acronym = triggerTokens[0];
            for (var start = 0; start + acronym.Length <= recognizedTokens.Count; start++)
            {
                var matchesSpelledOut = true;
                for (var index = 0; index < acronym.Length; index++)
                {
                    var token = recognizedTokens[start + index];
                    if (token.Length != 1 || token[0] != acronym[index])
                    {
                        matchesSpelledOut = false;
                        break;
                    }
                }

                if (matchesSpelledOut)
                {
                    return true;
                }
            }
        }

        if (triggerTokens.Count is >= 2 and <= 8 && triggerTokens.All(token => token.Length == 1))
        {
            var compactTrigger = string.Concat(triggerTokens);
            if (recognizedTokens.Any(token => token.Equals(compactTrigger, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
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
