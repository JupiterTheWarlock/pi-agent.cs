using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PiAgent.Models
{
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
}
