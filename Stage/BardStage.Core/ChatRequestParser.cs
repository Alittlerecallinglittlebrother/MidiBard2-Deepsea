namespace BardStage.Core;

public static class ChatRequestParser
{
    public static bool TryParse(string message, string prefix, out string query)
    {
        query = string.Empty;
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(prefix)) return false;
        var text = message.Trim();
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || text.Length <= prefix.Length
            || !char.IsWhiteSpace(text[prefix.Length])) return false;
        var candidate = text[prefix.Length..].Trim();
        if (candidate.Length is 0 or > 256 || candidate.Any(char.IsControl)) return false;
        query = candidate;
        return true;
    }
}
