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

    private static readonly Dictionary<string, string> ModelMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        // 4.x Argon
        ["gemini-4-argon"] = "models/gemini-4-argon",
        ["4-argon"] = "models/gemini-4-argon",
        ["4 argon"] = "models/gemini-4-argon",
        ["argon"] = "models/gemini-4-argon",

        // 3.8 Flash
        ["gemini-3.8-flash"] = "models/gemini-3.8-flash",
        ["3.8-flash"] = "models/gemini-3.8-flash",
        ["3.8 flash"] = "models/gemini-3.8-flash",
        ["gemini-3.8"] = "models/gemini-3.8-flash",

        // 3.5 Flash-Lite
        ["gemini-3.5-flash-lite"] = "models/gemini-3.5-flash-lite",
        ["3.5-flash-lite"] = "models/gemini-3.5-flash-lite",
        ["3.5 flash-lite"] = "models/gemini-3.5-flash-lite",
        ["3.5 flash lite"] = "models/gemini-3.5-flash-lite",

        // 3.1 Pro
        ["gemini-3.1-pro"] = "models/gemini-3.1-pro",
        ["3.1-pro"] = "models/gemini-3.1-pro",
        ["3.1 pro"] = "models/gemini-3.1-pro",
        ["gemini-3.1"] = "models/gemini-3.1-pro",

        // 2.5 Models
        ["gemini-2.5-pro"] = "models/gemini-2.5-pro",
        ["gemini-2.5-flash"] = "models/gemini-2.5-flash",
        ["gemini-2.5-flash-thinking"] = "models/gemini-2.5-flash-thinking",

        // 2.0 Models
        ["gemini-2.0-flash"] = "models/gemini-2.0-flash",
        ["gemini-2.0-flash-thinking"] = "models/gemini-2.0-flash-thinking",

        // 1.5 Models
        ["gemini-1.5-pro"] = "models/gemini-1.5-pro",
        ["gemini-1.5-flash"] = "models/gemini-1.5-flash",
    };

    public static string GetGeminiModelId(string openAIModelName)
    {
        var trimmed = openAIModelName.Trim();
        if (ModelMapping.TryGetValue(trimmed, out var geminiId))
        {
            return geminiId;
        }

        return trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"models/{trimmed.ToLowerInvariant().Replace(' ', '-')}";
    }

    public static List<string> GetAllModels() => [.. CanonicalModels];
}
