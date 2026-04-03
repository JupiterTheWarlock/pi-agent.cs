using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PiAgent.PiAi
{
    /// <summary>
    /// Complete LLM model configuration, matching pi-ai's Model interface.
    /// </summary>
    public class ModelConfig
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = "openai";

        [JsonPropertyName("api")]
        public string Api { get; set; } = "openai-completions";

        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";

        [JsonPropertyName("apiKey")]
        public string? ApiKey { get; set; }

        [JsonPropertyName("maxTokens")]
        public int MaxTokens { get; set; } = 4096;

        [JsonPropertyName("contextWindow")]
        public int ContextWindow { get; set; } = 128000;

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; } = 0.7;

        /// <summary>Whether this model supports reasoning/thinking.</summary>
        [JsonPropertyName("reasoning")]
        public bool Reasoning { get; set; }

        /// <summary>Supported input modalities.</summary>
        [JsonPropertyName("input")]
        public List<string> Input { get; set; } = new() { "text" };

        /// <summary>Cost per million tokens (USD).</summary>
        [JsonPropertyName("cost")]
        public ModelCost Cost { get; set; } = new ModelCost();

        /// <summary>Custom headers for API requests.</summary>
        [JsonIgnore]
        public Dictionary<string, string>? Headers { get; set; }

        /// <summary>OpenAI completions compatibility overrides.</summary>
        [JsonIgnore]
        public OpenAICompletionsCompat? Compat { get; set; }

        public ModelConfig() { }

        /// <summary>
        /// Convenience constructor for common OpenAI-compatible providers.
        /// </summary>
        public ModelConfig(string id, string baseUrl, string apiKey, int maxTokens = 4096)
        {
            Id = id;
            Name = id;
            BaseUrl = baseUrl;
            ApiKey = apiKey;
            MaxTokens = maxTokens;
        }
    }

    /// <summary>
    /// Cost per million tokens (USD).
    /// </summary>
    public class ModelCost
    {
        [JsonPropertyName("input")]
        public double Input { get; set; }

        [JsonPropertyName("output")]
        public double Output { get; set; }

        [JsonPropertyName("cacheRead")]
        public double CacheRead { get; set; }

        [JsonPropertyName("cacheWrite")]
        public double CacheWrite { get; set; }
    }

    /// <summary>
    /// Compatibility settings for OpenAI-compatible completions APIs.
    /// </summary>
    public class OpenAICompletionsCompat
    {
        /// <summary>Whether the provider supports the `store` field.</summary>
        public bool? SupportsStore { get; set; }

        /// <summary>Whether the provider supports the `developer` role (vs `system`).</summary>
        public bool? SupportsDeveloperRole { get; set; }

        /// <summary>Whether the provider supports `reasoning_effort`.</summary>
        public bool? SupportsReasoningEffort { get; set; }

        /// <summary>Whether the provider supports `stream_options: { include_usage: true }`.</summary>
        public bool? SupportsUsageInStreaming { get; set; } = true;

        /// <summary>Which field to use for max tokens.</summary>
        public string? MaxTokensField { get; set; }

        /// <summary>Whether tool results require the `name` field.</summary>
        public bool? RequiresToolResultName { get; set; }

        /// <summary>Whether a user message after tool results requires an assistant message in between.</summary>
        public bool? RequiresAssistantAfterToolResult { get; set; }

        /// <summary>Whether thinking blocks must be converted to text blocks with &lt;thinking&gt; delimiters.</summary>
        public bool? RequiresThinkingAsText { get; set; }

        /// <summary>Format for reasoning/thinking parameter.</summary>
        public string? ThinkingFormat { get; set; }

        /// <summary>Whether the provider supports the `strict` field in tool definitions.</summary>
        public bool? SupportsStrictMode { get; set; } = true;
    }
}
