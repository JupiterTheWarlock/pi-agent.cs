using System;
using System.Collections.Generic;
using System.Threading;
using PiAgent.PiAi;

namespace PiAgent.Core
{
    /// <summary>
    /// Public agent state, matching pi-agent-core's AgentState interface.
    /// </summary>
    public class AgentState
    {
        private List<Message> _messages = new();
        private List<AgentTool> _tools = new();

        /// <summary>System prompt sent with each model request.</summary>
        public string SystemPrompt { get; set; } = "";

        /// <summary>Active model used for future turns.</summary>
        public ModelConfig Model { get; set; } = null!;

        /// <summary>Requested reasoning level for future turns.</summary>
        public ThinkingLevel ThinkingLevel { get; set; } = ThinkingLevel.Off;

        /// <summary>Available tools.</summary>
        public List<AgentTool> Tools
        {
            get => _tools;
            set => _tools = value != null ? new List<AgentTool>(value) : new List<AgentTool>();
        }

        /// <summary>Conversation transcript.</summary>
        public List<Message> Messages
        {
            get => _messages;
            set => _messages = value != null ? new List<Message>(value) : new List<Message>();
        }

        /// <summary>True while the agent is processing a prompt or continuation.</summary>
        public bool IsStreaming { get; set; }

        /// <summary>Partial assistant message for the current streamed response, if any.</summary>
        public AssistantMessage? StreamingMessage { get; set; }

        /// <summary>Tool call IDs currently executing.</summary>
        public HashSet<string> PendingToolCalls { get; } = new();

        /// <summary>Error message from the most recent failed or aborted assistant turn.</summary>
        public string? ErrorMessage { get; set; }
    }
}
