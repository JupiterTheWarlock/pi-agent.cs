using System.Collections.Generic;
using PiAgent.Models;

namespace PiAgent.Agent
{
    /// <summary>
    /// Events emitted by the Agent during its lifecycle.
    /// </summary>
    public abstract class AgentEvent
    {
        public abstract string Type { get; }
    }

    // Lifecycle events
    public class AgentStartEvent : AgentEvent { public override string Type => "agent_start"; }

    public class AgentEndEvent : AgentEvent
    {
        public override string Type => "agent_end";
        public List<Message> Messages { get; set; } = new();
    }

    // Turn events
    public class TurnStartEvent : AgentEvent { public override string Type => "turn_start"; }

    public class TurnEndEvent : AgentEvent
    {
        public override string Type => "turn_end";
        public AssistantMessage Message { get; set; } = null!;
        public List<ToolResultMessage> ToolResults { get; set; } = new();
    }

    // Message events
    public class MessageStartEvent : AgentEvent
    {
        public override string Type => "message_start";
        public Message Message { get; set; } = null!;
    }

    public class MessageUpdateEvent : AgentEvent
    {
        public override string Type => "message_update";
        public AssistantMessage Message { get; set; } = null!;
        public string? DeltaText { get; set; }
    }

    public class MessageEndEvent : AgentEvent
    {
        public override string Type => "message_end";
        public Message Message { get; set; } = null!;
    }

    // Tool execution events
    public class ToolExecutionStartEvent : AgentEvent
    {
        public override string Type => "tool_execution_start";
        public string ToolCallId { get; set; } = "";
        public string ToolName { get; set; } = "";
        public Dictionary<string, object?> Args { get; set; } = new();
    }

    public class ToolExecutionUpdateEvent : AgentEvent
    {
        public override string Type => "tool_execution_update";
        public string ToolCallId { get; set; } = "";
        public string ToolName { get; set; } = "";
        public string PartialResult { get; set; } = "";
    }

    public class ToolExecutionEndEvent : AgentEvent
    {
        public override string Type => "tool_execution_end";
        public string ToolCallId { get; set; } = "";
        public string ToolName { get; set; } = "";
        public string Result { get; set; } = "";
        public bool IsError { get; set; }
    }
}
