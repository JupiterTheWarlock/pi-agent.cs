using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PiAgent.PiAi
{
    #region Content Types

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

    #endregion

    #region Messages

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

    #endregion

    #region Tool Definitions

    /// <summary>
    /// Definition of a tool that can be called by the LLM.
    /// </summary>
    public class ToolDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("parameters")]
        public JsonSchema Parameters { get; set; } = new JsonSchema();

        public ToolDefinition() { }

        public ToolDefinition(string name, string description, JsonSchema parameters)
        {
            Name = name;
            Description = description;
            Parameters = parameters;
        }

        /// <summary>
        /// A tool with no parameters.
        /// </summary>
        public static ToolDefinition NoParams(string name, string description)
        {
            return new ToolDefinition(name, description, new JsonSchema());
        }
    }

    /// <summary>
    /// Internal runtime tool that wraps a ToolDefinition with an execution delegate.
    /// </summary>
    public class AgentTool
    {
        public ToolDefinition Definition { get; }
        public Func<Dictionary<string, object?>, CancellationToken, Task<string>> Execute { get; }
        public string Label { get; }

        public AgentTool(ToolDefinition definition, Func<Dictionary<string, object?>, CancellationToken, Task<string>> execute, string? label = null)
        {
            Definition = definition;
            Execute = execute;
            Label = label ?? definition.Name;
        }
    }

    #endregion

    #region JSON Schema

    /// <summary>
    /// JSON Schema representation for tool parameters.
    /// Simplified version — supports type, description, properties, required.
    /// </summary>
    public class JsonSchema
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "object";

        [JsonPropertyName("properties")]
        public Dictionary<string, JsonSchemaProperty> Properties { get; set; } = new();

        [JsonPropertyName("required")]
        public List<string> Required { get; set; } = new();

        public JsonSchema() { }

        public JsonSchema(Dictionary<string, JsonSchemaProperty> properties, List<string>? required = null)
        {
            Properties = properties;
            Required = required ?? new List<string>();
        }
    }

    public class JsonSchemaProperty
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "string";

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("enum")]
        public List<string>? EnumValues { get; set; }

        [JsonPropertyName("items")]
        public JsonSchemaProperty? Items { get; set; }

        public JsonSchemaProperty() { }

        public JsonSchemaProperty(string type, string? description = null)
        {
            Type = type;
            Description = description;
        }
    }

    #endregion

    #region Context

    /// <summary>
    /// The context sent to the LLM: system prompt + conversation + tools.
    /// </summary>
    public class AgentContext
    {
        [JsonPropertyName("systemPrompt")]
        public string SystemPrompt { get; set; } = "";

        [JsonPropertyName("messages")]
        public List<Message> Messages { get; set; } = new();

        [JsonPropertyName("tools")]
        public List<AgentTool>? Tools { get; set; }

        public AgentContext() { }

        public AgentContext(string systemPrompt, List<Message> messages, List<AgentTool>? tools = null)
        {
            SystemPrompt = systemPrompt;
            Messages = messages;
            Tools = tools;
        }
    }

    #endregion

    #region Usage

    /// <summary>
    /// Token usage statistics from an LLM response.
    /// </summary>
    public class Usage
    {
        [JsonPropertyName("input")]
        public int InputTokens { get; set; }

        [JsonPropertyName("output")]
        public int OutputTokens { get; set; }

        [JsonPropertyName("total")]
        public int TotalTokens { get; set; }

        public Usage() { }

        public Usage(int input, int output, int total)
        {
            InputTokens = input;
            OutputTokens = output;
            TotalTokens = total;
        }

        public static Usage Zero { get; } = new Usage(0, 0, 0);
    }

    #endregion
}
