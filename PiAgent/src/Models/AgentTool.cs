using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PiAgent.Models
{
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
}
