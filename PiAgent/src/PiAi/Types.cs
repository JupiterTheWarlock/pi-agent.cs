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

        /// <summary>Opaque signature for multi-turn continuity (e.g., OpenAI reasoning item ID).</summary>
        [JsonPropertyName("thinkingSignature")]
        public string? ThinkingSignature { get; set; }

        /// <summary>True if the thinking content was redacted by safety filters.</summary>
        [JsonPropertyName("redacted")]
        public bool Redacted { get; set; }
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

    #region Enums

    /// <summary>
    /// Reason why the LLM stopped generating.
    /// </summary>
    public enum StopReason
    {
        /// <summary>Normal completion.</summary>
        Stop,
        /// <summary>Max tokens reached.</summary>
        Length,
        /// <summary>Model wants to call a tool.</summary>
        ToolUse,
        /// <summary>API error occurred.</summary>
        Error,
        /// <summary>Request was aborted/cancelled.</summary>
        Aborted
    }

    /// <summary>
    /// Thinking/reasoning level for models that support it.
    /// </summary>
    public enum ThinkingLevel
    {
        Off,
        Minimal,
        Low,
        Medium,
        High
    }

    /// <summary>
    /// Tool execution mode for the agent loop.
    /// </summary>
    public enum ToolExecutionMode
    {
        Sequential,
        Parallel
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
    /// Supports type, description, properties, required, enum, default, strict.
    /// </summary>
    public class JsonSchema
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "object";

        [JsonPropertyName("properties")]
        public Dictionary<string, JsonSchemaProperty> Properties { get; set; } = new();

        [JsonPropertyName("required")]
        public List<string> Required { get; set; } = new();

        [JsonPropertyName("additionalProperties")]
        public bool? AdditionalProperties { get; set; }

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

        [JsonPropertyName("default")]
        public object? DefaultValue { get; set; }

        public JsonSchemaProperty() { }

        public JsonSchemaProperty(string type, string? description = null)
        {
            Type = type;
            Description = description;
        }
    }

    #endregion

    #region Tool Metadata & Params

    /// <summary>
    /// Metadata for tool registration: extra params, excluded params, description overrides.
    /// </summary>
    public class ToolMetadata
    {
        /// <summary>
        /// Extra parameters to include in the schema.
        /// </summary>
        public List<ToolParam>? ExtraParams { get; set; }

        /// <summary>
        /// Parameter names to exclude from the schema.
        /// </summary>
        public List<string>? ExcludeParams { get; set; }

        /// <summary>
        /// Override the tool description.
        /// </summary>
        public string? DescriptionOverride { get; set; }
    }

    /// <summary>
    /// Describes a single tool parameter for manual (non-reflection) tool definition.
    /// </summary>
    public class ToolParam
    {
        /// <summary>Parameter name.</summary>
        public string Name { get; set; } = "";

        /// <summary>Parameter description (for the LLM).</summary>
        public string? Description { get; set; }

        /// <summary>JSON Schema type: "string", "integer", "number", "boolean", "array", "object".</summary>
        public string Type { get; set; } = "string";

        /// <summary>Whether the parameter is required. Default true.</summary>
        public bool Required { get; set; } = true;

        /// <summary>Allowed enum values (generates "enum" in JSON Schema).</summary>
        public List<string>? EnumValues { get; set; }

        /// <summary>Default value for optional parameters.</summary>
        public object? DefaultValue { get; set; }

        /// <summary>Schema for array items (when Type is "array").</summary>
        public ToolParam? Items { get; set; }

        /// <summary>Schema for object properties (when Type is "object").</summary>
        public List<ToolParam>? ObjectProperties { get; set; }

        public ToolParam() { }

        public ToolParam(string name, string type, string? description = null, bool required = true)
        {
            Name = name;
            Type = type;
            Description = description;
            Required = required;
        }

        /// <summary>Convenience: string parameter.</summary>
        public static ToolParam Str(string name, string? description = null, bool required = true)
            => new ToolParam(name, "string", description, required);

        /// <summary>Convenience: integer parameter.</summary>
        public static ToolParam Integer(string name, string? description = null, bool required = true)
            => new ToolParam(name, "integer", description, required);

        /// <summary>Convenience: number (float) parameter.</summary>
        public static ToolParam Float(string name, string? description = null, bool required = true)
            => new ToolParam(name, "number", description, required);

        /// <summary>Convenience: boolean parameter.</summary>
        public static ToolParam Boolean(string name, string? description = null, bool required = true)
            => new ToolParam(name, "boolean", description, required);

        /// <summary>Convenience: enum parameter (string with restricted values).</summary>
        public static ToolParam Enum(string name, List<string> values, string? description = null, bool required = true)
            => new ToolParam(name, "string", description, required) { EnumValues = values };

        /// <summary>Convenience: array parameter.</summary>
        public static ToolParam Array(string name, ToolParam items, string? description = null, bool required = true)
            => new ToolParam(name, "array", description, required) { Items = items };

        /// <summary>Convenience: object parameter.</summary>
        public static ToolParam Object(string name, List<ToolParam> properties, string? description = null, bool required = true)
            => new ToolParam(name, "object", description, required) { ObjectProperties = properties };

        /// <summary>
        /// Convert this ToolParam to a JsonSchemaProperty for the schema.
        /// </summary>
        public JsonSchemaProperty ToSchemaProperty()
        {
            var prop = new JsonSchemaProperty(Type, Description);
            if (EnumValues != null && EnumValues.Count > 0)
                prop.EnumValues = new List<string>(EnumValues);
            if (DefaultValue != null)
                prop.DefaultValue = DefaultValue;
            if (Items != null)
                prop.Items = Items.ToSchemaProperty();
            return prop;
        }
    }

    /// <summary>
    /// Strict mode settings for OpenAI-compatible JSON schema validation.
    /// </summary>
    public class StrictMode
    {
        /// <summary>Whether strict mode is enabled.</summary>
        public bool Enabled { get; set; }

        public StrictMode(bool enabled = false)
        {
            Enabled = enabled;
        }

        public static StrictMode On => new StrictMode(true);
        public static StrictMode Off => new StrictMode(false);
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

        [JsonPropertyName("cacheRead")]
        public int CacheReadTokens { get; set; }

        [JsonPropertyName("cacheWrite")]
        public int CacheWriteTokens { get; set; }

        [JsonPropertyName("cost")]
        public UsageCost Cost { get; set; } = new UsageCost();

        public Usage() { }

        public Usage(int input, int output, int total)
        {
            InputTokens = input;
            OutputTokens = output;
            TotalTokens = total;
        }

        public static Usage Zero { get; } = new Usage(0, 0, 0);
    }

    /// <summary>
    /// Cost breakdown for token usage (USD).
    /// </summary>
    public class UsageCost
    {
        [JsonPropertyName("input")]
        public double Input { get; set; }

        [JsonPropertyName("output")]
        public double Output { get; set; }

        [JsonPropertyName("cacheRead")]
        public double CacheRead { get; set; }

        [JsonPropertyName("cacheWrite")]
        public double CacheWrite { get; set; }

        [JsonPropertyName("total")]
        public double Total { get; set; }
    }

    #endregion
}
