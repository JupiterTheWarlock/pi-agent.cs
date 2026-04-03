using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PiAgent.Models
{
    /// <summary>
    /// A text content block within a message.
    /// </summary>
    public class TextContent
    {
        [JsonPropertyName("type")]
        public string Type => "text";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";
    }

    /// <summary>
    /// An image content block (base64-encoded).
    /// </summary>
    public class ImageContent
    {
        [JsonPropertyName("type")]
        public string Type => "image";

        [JsonPropertyName("data")]
        public string Data { get; set; } = "";

        [JsonPropertyName("mimeType")]
        public string MimeType { get; set; } = "image/png";
    }

    /// <summary>
    /// A tool call content block within an assistant message.
    /// </summary>
    public class ToolCall
    {
        [JsonPropertyName("type")]
        public string Type => "toolCall";

        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("arguments")]
        public Dictionary<string, object?> Arguments { get; set; } = new();
    }

    /// <summary>
    /// Thinking/reasoning content block (for models that support it).
    /// </summary>
    public class ThinkingContent
    {
        [JsonPropertyName("type")]
        public string Type => "thinking";

        [JsonPropertyName("thinking")]
        public string Thinking { get; set; } = "";
    }
}
