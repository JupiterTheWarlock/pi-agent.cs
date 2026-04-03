using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiAgent.Models
{
    /// <summary>
    /// Base marker for messages. Discriminated by Role.
    /// </summary>
    public abstract class Message
    {
        [JsonPropertyName("role")]
        public abstract string Role { get; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// A user message. Content can be a plain string or a list of content blocks.
    /// </summary>
    public class UserMessage : Message
    {
        public override string Role => "user";

        /// <summary>
        /// Plain text content. Used when the message is a simple string.
        /// </summary>
        [JsonIgnore]
        public string? Text { get; set; }

        /// <summary>
        /// Content blocks (text, image). Serialized as "content" array or string.
        /// </summary>
        [JsonPropertyName("content")]
        public object Content
        {
            get
            {
                if (Text != null) return Text;
                return ContentBlocks ?? new List<object>();
            }
            set
            {
                if (value is string s)
                    Text = s;
                else if (value is JsonElement el)
                {
                    if (el.ValueKind == JsonValueKind.String)
                        Text = el.GetString();
                }
            }
        }

        [JsonIgnore]
        public List<object>? ContentBlocks { get; set; }

        public UserMessage() { }

        public UserMessage(string text)
        {
            Text = text;
        }
    }

    /// <summary>
    /// An assistant message from the LLM. Contains text, thinking, and/or tool calls.
    /// </summary>
    public class AssistantMessage : Message
    {
        public override string Role => "assistant";

        [JsonPropertyName("content")]
        public List<object> Content { get; set; } = new();

        [JsonPropertyName("usage")]
        public Usage Usage { get; set; } = Usage.Zero;

        [JsonPropertyName("stopReason")]
        public string StopReason { get; set; } = "stop";

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        [JsonIgnore]
        public bool IsError => StopReason == "error" || StopReason == "aborted";

        /// <summary>
        /// Extract all tool calls from content blocks.
        /// </summary>
        public List<ToolCall> GetToolCalls()
        {
            var calls = new List<ToolCall>();
            foreach (var item in Content)
            {
                if (item is ToolCall tc) calls.Add(tc);
            }
            return calls;
        }

        /// <summary>
        /// Get the concatenated text from all text content blocks.
        /// </summary>
        public string GetText()
        {
            var parts = new List<string>();
            foreach (var item in Content)
            {
                if (item is TextContent tc) parts.Add(tc.Text);
            }
            return string.Join("", parts);
        }
    }

    /// <summary>
    /// A tool result message sent back to the LLM after executing a tool call.
    /// </summary>
    public class ToolResultMessage : Message
    {
        public override string Role => "toolResult";

        [JsonPropertyName("toolCallId")]
        public string ToolCallId { get; set; } = "";

        [JsonPropertyName("toolName")]
        public string ToolName { get; set; } = "";

        [JsonPropertyName("content")]
        public List<object> Content { get; set; } = new();

        [JsonPropertyName("isError")]
        public bool IsError { get; set; }
    }
}
