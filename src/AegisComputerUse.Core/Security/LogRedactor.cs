using System.Text.RegularExpressions;

namespace AegisComputerUse.Core.Security;

public static partial class LogRedactor
{
    [GeneratedRegex(@"AIza[0-9A-Za-z\-_]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleApiKeyPattern();

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9\-_]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex AnthropicKeyPattern();

    [GeneratedRegex(@"\bsk-(?:proj-)?[A-Za-z0-9\-_]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiKeyPattern();

    [GeneratedRegex(@"(?i)Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)(x-goog-api-key|x-api-key|authorization)\s*[:=]\s*(?:Bearer\s+)?[^\s,;""']+", RegexOptions.CultureInvariant)]
    private static partial Regex AuthHeaderPattern();

    [GeneratedRegex(@"(?i)([?&](?:key|api_key|token|access_token)=)[^&\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex QuerySecretPattern();

    public static string Redact(string? input, string? knownSecret = null)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        string result = input;
        if (!string.IsNullOrEmpty(knownSecret))
        {
            result = result.Replace(knownSecret, "[REDACTED_SECRET]", StringComparison.Ordinal);
        }

        result = GoogleApiKeyPattern().Replace(result, "[REDACTED_API_KEY]");
        result = AnthropicKeyPattern().Replace(result, "[REDACTED_API_KEY]");
        result = OpenAiKeyPattern().Replace(result, "[REDACTED_API_KEY]");
        result = BearerPattern().Replace(result, "Bearer [REDACTED]");
        result = AuthHeaderPattern().Replace(result, "$1=[REDACTED]");
        result = QuerySecretPattern().Replace(result, "$1[REDACTED]");
        return result;
    }
}
