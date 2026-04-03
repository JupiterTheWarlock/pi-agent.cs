using System.Text.Json.Serialization;

namespace PiAgent.Models
{
    /// <summary>
    /// LLM model configuration.
    /// </summary>
    public class ModelConfig
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = "openai";

        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";

        [JsonPropertyName("apiKey")]
        public string? ApiKey { get; set; }

        [JsonPropertyName("maxTokens")]
        public int MaxTokens { get; set; } = 4096;

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; } = 0.7;

        /// <summary>
        /// Cost per million input tokens (USD).
        /// </summary>
        [JsonPropertyName("costInput")]
        public double CostInput { get; set; }

        /// <summary>
        /// Cost per million output tokens (USD).
        /// </summary>
        [JsonPropertyName("costOutput")]
        public double CostOutput { get; set; }

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
}
