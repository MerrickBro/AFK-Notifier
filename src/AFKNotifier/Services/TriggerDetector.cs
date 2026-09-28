namespace AFKNotifier.Services;

public static class TriggerDetector
{
    public static bool Matches(string recognizedText, string triggerPhrase)
    {
        var recognized = Normalize(recognizedText);
        var trigger = Normalize(triggerPhrase);
        return trigger.Length > 0 && recognized.Contains(trigger, StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }
}
