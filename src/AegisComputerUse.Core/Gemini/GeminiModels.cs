using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace AegisComputerUse.Core.Gemini;

public enum AiProviderKind
{
    GoogleGemini,
    OpenAI,
    Anthropic
}

public sealed record ProviderModelCatalogItem(
    AiProviderKind Provider,
    string ModelId,
    string DisplayName,
    int ContextLimit,
    string Notes);

public static class AiProviderCatalog
{
    private static readonly ConcurrentDictionary<string, int> DiscoveredContextLimits = new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterDiscoveredContextLimits(IEnumerable<ProviderModelCatalogItem> items)
    {
        foreach (var item in items)
        {
            if (item.ContextLimit > 0)
            {
                DiscoveredContextLimits[$"{item.Provider}:{item.ModelId}"] = item.ContextLimit;
            }
        }
    }

    public static string GetProviderDisplayName(AiProviderKind provider) => provider switch
    {
        AiProviderKind.GoogleGemini => "Google (Gemini)",
        AiProviderKind.OpenAI => "OpenAI",
        AiProviderKind.Anthropic => "Anthropic (Claude)",
        _ => provider.ToString()
    };

    public static IReadOnlyList<ProviderModelCatalogItem> GetModelsForProvider(AiProviderKind provider) => provider switch
    {
        AiProviderKind.GoogleGemini =>
        [
            new(AiProviderKind.GoogleGemini, "gemini-2.5-flash", "Gemini 2.5 Flash", 1_048_576, "Быстрая • работает на Free Tier"),
            new(AiProviderKind.GoogleGemini, "gemini-3.8-flash", "Gemini 3.8 Flash", 1_048_576, "Серия 3.x • по доступности ключа"),
            new(AiProviderKind.GoogleGemini, "gemini-4-argon", "Gemini 4 Argon", 1_048_576, "Флагман 4.x • требует Billing (Free = 0)"),
            new(AiProviderKind.GoogleGemini, "gemini-2.5-pro", "Gemini 2.5 Pro", 1_048_576, "Сложный код и рассуждения"),
            new(AiProviderKind.GoogleGemini, "gemini-2.5-computer-use-preview-10-2025", "Gemini 2.5 Computer Use", 131_072, "Превью управления экраном")
        ],
        AiProviderKind.OpenAI =>
        [
            new(AiProviderKind.OpenAI, "gpt-5.4", "GPT-5.4", 1_000_000, "Флагман OpenAI + рассуждения"),
            new(AiProviderKind.OpenAI, "gpt-4.1", "GPT-4.1", 1_000_000, "Универсальная модель для кода и задач"),
            new(AiProviderKind.OpenAI, "gpt-5.2", "GPT-5.2", 400_000, "Продвинутая модель OpenAI"),
            new(AiProviderKind.OpenAI, "o3", "OpenAI o3", 200_000, "Глубокое пошаговое рассуждение"),
            new(AiProviderKind.OpenAI, "computer-use-preview", "OpenAI Computer Use", 200_000, "Агент управления экраном")
        ],
        AiProviderKind.Anthropic =>
        [
            new(AiProviderKind.Anthropic, "claude-opus-4-6", "Claude Opus 4.6", 200_000, "Максимальный интеллект + Extended Thinking"),
            new(AiProviderKind.Anthropic, "claude-sonnet-4-6", "Claude Sonnet 4.6", 200_000, "Оптимальный баланс скорости и качества"),
            new(AiProviderKind.Anthropic, "claude-haiku-4-5", "Claude Haiku 4.5", 200_000, "Быстрый отклик и экономия токенов"),
            new(AiProviderKind.Anthropic, "claude-3-7-sonnet-20250219", "Claude 3.7 Sonnet", 200_000, "Гибридные рассуждения + Computer Use")
        ],
        _ => []
    };

    public static int ResolveContextLimit(AiProviderKind provider, string modelId)
    {
        if (DiscoveredContextLimits.TryGetValue($"{provider}:{modelId}", out int discovered) && discovered > 0)
            return discovered;

        foreach (var item in GetModelsForProvider(provider))
        {
            if (string.Equals(item.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                return item.ContextLimit;
        }
        return provider == AiProviderKind.Anthropic ? 200_000 : 1_048_576;
    }
}

public enum ComputerUseToolMode
{
    Auto,
    NativeDesktop,
    FunctionCallingFallback
}

public sealed record AttachmentInput(
    string FileName,
    string MimeType,
    byte[] DataBytes,
    bool IsTextDocument = false);

public sealed record GeminiRequestOptions(
    string Model = "gemini-4-argon",
    string ThinkingLevel = "MEDIUM",
    ComputerUseToolMode ToolMode = ComputerUseToolMode.Auto,
    bool EnableComputerUse = true,
    bool EnableFileCreation = true,
    AiProviderKind Provider = AiProviderKind.GoogleGemini);

public sealed record GeminiFunctionCall(
    string Name,
    JsonNode ArgsNode,
    string? CallId = null);

public sealed record GeminiUsageMetadata(
    int PromptTokens,
    int CandidatesTokens,
    int TotalTokens,
    int CachedTokens,
    int ThoughtsTokens);

public sealed record GeminiTurnResponse(
    string Text,
    IReadOnlyList<GeminiFunctionCall> FunctionCalls,
    JsonNode RawModelContent,
    string ThoughtText = "",
    GeminiUsageMetadata? Usage = null);

public sealed record ConfirmationRequest(
    string ActionName,
    string ActionSummary,
    string Reason,
    bool IsFromModelSafetyDecision);

public enum AgentEventKind
{
    Info,
    Progress,
    ModelThoughtOrText,
    ModelReasoning,
    UsageUpdated,
    ActionExecuted,
    ActionBlocked,
    FileCreated,
    SecurityWarning,
    Completed,
    Error
}

public sealed record AgentEvent(
    AgentEventKind Kind,
    string Message,
    string? Detail = null,
    GeminiUsageMetadata? Usage = null);
