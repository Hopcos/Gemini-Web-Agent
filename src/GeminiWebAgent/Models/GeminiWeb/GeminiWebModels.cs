namespace GeminiWebAgent.Models.GeminiWeb;

/// <summary>
/// Represents session parameters extracted from the Gemini web page.
/// </summary>
public class GeminiWebSession
{
    /// <summary>
    /// The backend build label (bl parameter), e.g., "boq_assistant-bard-web-server_..."
    /// </summary>
    public string BuildLabel { get; set; } = string.Empty;

    /// <summary>
    /// The anti-XSRF token (at parameter).
    /// </summary>
    public string AtToken { get; set; } = string.Empty;

    /// <summary>
    /// The session ID (f.sid parameter).
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Incrementing request ID counter.
    /// </summary>
    public int RequestId { get; set; }

    /// <summary>
    /// Whether the session has been initialized.
    /// </summary>
    public bool IsInitialized => !string.IsNullOrEmpty(BuildLabel) && !string.IsNullOrEmpty(AtToken);
}

/// <summary>
/// Configuration for Gemini Web model mapping.
/// </summary>
public readonly record struct GeminiWebModelConfig(
    int ModeCategory,
    int ThinkMode,
    string CanonicalName,
    string DisplayName,
    string Description
);

/// <summary>
/// Model mapping between OpenAI model names and Gemini Web internal identifiers.
/// </summary>
public static class GeminiWebModelMap
{
    private static readonly string[] CanonicalModels =
    [
        "gemini-4-argon",
        "gemini-3.8-flash",
        "gemini-3.5-flash-lite",
        "gemini-3.1-pro",
        "gemini-2.5-pro",
        "gemini-2.5-flash",
        "gemini-2.5-flash-thinking",
        "gemini-2.0-flash",
        "gemini-2.0-flash-thinking",
        "gemini-1.5-pro",
        "gemini-1.5-flash"
    ];

    private static readonly GeminiWebModelConfig DefaultConfig = new(
        ModeCategory: 1,
        ThinkMode: 4,
        CanonicalName: "gemini-3.8-flash",
        DisplayName: "Gemini 3.8 Flash",
        Description: "high-speed multimodal model"
    );

    private static readonly Dictionary<string, GeminiWebModelConfig> ModelConfigMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        // 4.x Argon (Flagship Reasoning Model)
        ["gemini-4-argon"] = new(3, 4, "gemini-4-argon", "Gemini 4 Argon", "flagship reasoning AI model"),
        ["4-argon"] = new(3, 4, "gemini-4-argon", "Gemini 4 Argon", "flagship reasoning AI model"),
        ["4 argon"] = new(3, 4, "gemini-4-argon", "Gemini 4 Argon", "flagship reasoning AI model"),
        ["argon"] = new(3, 4, "gemini-4-argon", "Gemini 4 Argon", "flagship reasoning AI model"),

        // 3.8 Flash (High-Speed Multimodal)
        ["gemini-3.8-flash"] = new(1, 4, "gemini-3.8-flash", "Gemini 3.8 Flash", "high-speed multimodal model"),
        ["3.8-flash"] = new(1, 4, "gemini-3.8-flash", "Gemini 3.8 Flash", "high-speed multimodal model"),
        ["3.8 flash"] = new(1, 4, "gemini-3.8-flash", "Gemini 3.8 Flash", "high-speed multimodal model"),
        ["gemini-3.8"] = new(1, 4, "gemini-3.8-flash", "Gemini 3.8 Flash", "high-speed multimodal model"),

        // 3.5 Flash-Lite (Lightweight Model)
        ["gemini-3.5-flash-lite"] = new(6, 4, "gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite", "lightweight fast model"),
        ["3.5-flash-lite"] = new(6, 4, "gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite", "lightweight fast model"),
        ["3.5 flash-lite"] = new(6, 4, "gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite", "lightweight fast model"),
        ["3.5 flash lite"] = new(6, 4, "gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite", "lightweight fast model"),
        ["gemini-flash-lite"] = new(6, 4, "gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite", "lightweight fast model"),

        // 3.1 Pro (Advanced Reasoning)
        ["gemini-3.1-pro"] = new(3, 4, "gemini-3.1-pro", "Gemini 3.1 Pro", "advanced reasoning model"),
        ["3.1-pro"] = new(3, 4, "gemini-3.1-pro", "Gemini 3.1 Pro", "advanced reasoning model"),
        ["3.1 pro"] = new(3, 4, "gemini-3.1-pro", "Gemini 3.1 Pro", "advanced reasoning model"),
        ["gemini-3.1"] = new(3, 4, "gemini-3.1-pro", "Gemini 3.1 Pro", "advanced reasoning model"),

        // 2.5 Models
        ["gemini-2.5-pro"] = new(3, 4, "gemini-2.5-pro", "Gemini 2.5 Pro", "advanced reasoning model"),
        ["gemini-2.5-flash"] = new(1, 4, "gemini-2.5-flash", "Gemini 2.5 Flash", "fast multimodal model"),
        ["gemini-2.5-flash-thinking"] = new(2, 0, "gemini-2.5-flash-thinking", "Gemini 2.5 Flash Thinking", "deep thinking and reasoning model"),

        // 2.0 Models
        ["gemini-2.0-flash"] = new(1, 4, "gemini-2.0-flash", "Gemini 2.0 Flash", "fast multimodal model"),
        ["gemini-2.0-flash-thinking"] = new(2, 0, "gemini-2.0-flash-thinking", "Gemini 2.0 Flash Thinking", "deep thinking and reasoning model"),

        // 1.5 Models
        ["gemini-1.5-pro"] = new(3, 4, "gemini-1.5-pro", "Gemini 1.5 Pro", "advanced reasoning model"),
        ["gemini-1.5-flash"] = new(1, 4, "gemini-1.5-flash", "Gemini 1.5 Flash", "fast model")
    };

    public static GeminiWebModelConfig GetModelConfig(string openAIModelName)
    {
        var trimmed = openAIModelName.Trim();
        if (ModelConfigMapping.TryGetValue(trimmed, out var config))
        {
            return config;
        }

        // Fuzzy match or fallback
        if (trimmed.Contains("argon", StringComparison.OrdinalIgnoreCase))
            return ModelConfigMapping["gemini-4-argon"];

        if (trimmed.Contains("thinking", StringComparison.OrdinalIgnoreCase))
            return ModelConfigMapping["gemini-2.5-flash-thinking"];

        if (trimmed.Contains("pro", StringComparison.OrdinalIgnoreCase))
            return ModelConfigMapping["gemini-3.1-pro"];

        if (trimmed.Contains("lite", StringComparison.OrdinalIgnoreCase))
            return ModelConfigMapping["gemini-3.5-flash-lite"];

        return DefaultConfig;
    }

    public static string GetGeminiModelId(string openAIModelName) => GetModelConfig(openAIModelName).CanonicalName;

    public static List<string> GetAllModels() => [.. CanonicalModels];
}
