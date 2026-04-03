using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PiAgent.Models
{
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
}
