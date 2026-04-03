using System;
using System.Threading;
using System.Threading.Tasks;

namespace PiAgent.PiAi
{
    /// <summary>
    /// Unified interface for LLM clients.
    /// Implementations: OpenAIClient covers 90%+ of providers.
    /// </summary>
    public interface ILLMClient
    {
        /// <summary>
        /// Send a complete (non-streaming) request to the LLM.
        /// </summary>
        Task<AssistantMessage> Complete(AgentContext context, ModelConfig model, CancellationToken ct = default);

        /// <summary>
        /// Stream a response from the LLM. Returns chunks via callback.
        /// </summary>
        Task<AssistantMessage> Stream(AgentContext context, ModelConfig model,
            Action<string>? onTextDelta = null, Action<ToolCall>? onToolCallDelta = null,
            CancellationToken ct = default);
    }
}
