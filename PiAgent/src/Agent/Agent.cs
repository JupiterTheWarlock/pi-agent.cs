using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PiAgent.LLM;
using PiAgent.Models;
using PiAgent.Tools;

namespace PiAgent.Core
{
    /// <summary>
    /// High-level Agent: state management, tool registration, prompt/continue API.
    /// Wraps AgentLoop with persistent state and convenient methods.
    /// </summary>
    public class Agent
    {
        private readonly AgentLoop _loop;
        private readonly ToolRegistry _tools = new();

        public ModelConfig Model { get; }
        public string SystemPrompt { get; set; } = "";
        public List<Message> Messages { get; } = new();
        public bool IsRunning { get; private set; }
        public AgentLoop Loop => _loop;

        public event Action<AgentEvent>? OnEvent;

        public Agent(ModelConfig model, ILLMClient? client = null)
        {
            Model = model;
            _loop = new AgentLoop(client ?? new OpenAIClient(), model);
        }

        /// <summary>
        /// Define a tool with no parameters.
        /// </summary>
        public AgentTool DefineTool(string name, string description, Func<string> handler)
            => _tools.Define(name, description, handler);

        /// <summary>
        /// Define an async tool with no parameters.
        /// </summary>
        public AgentTool DefineTool(string name, string description, Func<Task<string>> handler)
            => _tools.Define(name, description, handler);

        /// <summary>
        /// Define an async tool with no parameters (with cancellation).
        /// </summary>
        public AgentTool DefineTool(string name, string description, Func<CancellationToken, Task<string>> handler)
            => _tools.Define(name, description, handler);

        /// <summary>
        /// Define a tool with a single parameter.
        /// </summary>
        public AgentTool DefineTool<T>(string name, string description, Func<T, Task<string>> handler)
            => _tools.Define(name, description, handler);

        /// <summary>
        /// Define a tool with a single parameter (sync).
        /// </summary>
        public AgentTool DefineTool<T>(string name, string description, Func<T, string> handler)
            => _tools.Define(name, description, handler);

        /// <summary>
        /// Send a user message and run the agent loop.
        /// </summary>
        public async Task<List<Message>> Prompt(string text, CancellationToken ct = default)
        {
            if (IsRunning) throw new InvalidOperationException("Agent is already running");
            IsRunning = true;
            try
            {
                var userMsg = new UserMessage(text);
                var context = new AgentContext(SystemPrompt, Messages, _tools.GetAll());
                var result = await _loop.Run(context, new List<Message> { userMsg }, _tools.GetAll(), OnEvent, ct);

                // context.Messages IS agent.Messages (same reference), 
                // so Run already appended to it — no need to re-sync
                return result;
            }
            finally
            {
                IsRunning = false;
            }
        }

        /// <summary>
        /// Continue the conversation from the current state.
        /// Last message must be a user or toolResult message.
        /// </summary>
        public async Task<List<Message>> Continue(CancellationToken ct = default)
        {
            if (IsRunning) throw new InvalidOperationException("Agent is already running");
            if (Messages.Count == 0) throw new InvalidOperationException("No messages to continue from");

            var last = Messages[^1];
            if (last is AssistantMessage)
                throw new InvalidOperationException("Cannot continue from an assistant message");

            IsRunning = true;
            try
            {
                var context = new AgentContext(SystemPrompt, Messages, _tools.GetAll());
                var result = await _loop.Run(context, new List<Message>(), _tools.GetAll(), OnEvent, ct);

                // context.Messages IS agent.Messages (same reference), no need to re-sync
                return result;
            }
            finally
            {
                IsRunning = false;
            }
        }

        /// <summary>
        /// Clear all messages and reset state.
        /// </summary>
        public void Reset()
        {
            Messages.Clear();
            IsRunning = false;
        }

        /// <summary>
        /// Get the last assistant message text.
        /// </summary>
        public string? GetLastResponse()
        {
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i] is AssistantMessage am)
                    return am.GetText();
            }
            return null;
        }
    }
}
