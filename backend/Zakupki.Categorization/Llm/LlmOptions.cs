namespace Zakupki.Categorization.Llm;

/// <summary>LLM provider settings. API key is never stored here — only env <c>LLM_API_KEY</c>.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";
    public const string ApiKeyEnvironmentVariable = "LLM_API_KEY";

    /// <summary>Logical provider id (e.g. fake, openai, yandexgpt). Not a hardcoded vendor SDK.</summary>
    public string Provider { get; set; } = "fake";

    /// <summary>Model name from config (empty for fake).</summary>
    public string Model { get; set; } = "";

    /// <summary>HTTP/call timeout for real providers (seconds).</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
