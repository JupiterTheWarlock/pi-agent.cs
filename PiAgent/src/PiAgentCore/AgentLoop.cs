using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PiAgent.PiAi;
using PiAgent.Tools;

namespace PiAgent.Core
{
    /// <summary>
    /// Core agent loop: prompt → LLM → tool calls → execute → feed back → repeat.
    /// Inspired by pi-mono's agent-loop.ts but simplified for game-friendly usage.
    /// </summary>
    public class AgentLoop
    {
        private readonly ILLMClient _client;
        private readonly ModelConfig _model;

        public AgentLoop(ILLMClient client, ModelConfig model)
        {
            _client = client;
            _model = model;
        }

        /// <summary>
        /// Run one full agent loop: process user messages through LLM, 
        /// execute any tool calls, feed results back, and repeat until done.
        /// </summary>
        public async Task<List<Message>> Run(
            AgentContext context,
            List<Message> newMessages,
            List<AgentTool>? tools,
            Action<AgentEvent>? onEvent = null,
            CancellationToken ct = default,
            int maxToolRounds = 10)
        {
            var produced = new List<Message>();

            foreach (var msg in newMessages)
            {
                context.Messages.Add(msg);
                produced.Add(msg);
            }

            void Emit(AgentEvent e) => onEvent?.Invoke(e);

            Emit(new AgentStartEvent());

            int rounds = 0;
            while (rounds++ < maxToolRounds)
            {
                ct.ThrowIfCancellationRequested();

                Emit(new TurnStartEvent());

                AssistantMessage assistant;
                try
                {
                    assistant = await _client.Complete(context, _model, ct);
                }
                catch (OperationCanceledException)
                {
                    assistant = new AssistantMessage
                    {
                        StopReason = "aborted",
                        ErrorMessage = "Cancelled"
                    };
                }
                catch (Exception ex)
                {
                    assistant = new AssistantMessage
                    {
                        StopReason = "error",
                        ErrorMessage = ex.Message,
                        Content = { new TextContent { Text = $"Error: {ex.Message}" } }
                    };
                }

                context.Messages.Add(assistant);
                produced.Add(assistant);

                Emit(new MessageStartEvent { Message = assistant });
                Emit(new MessageUpdateEvent { Message = assistant });
                Emit(new MessageEndEvent { Message = assistant });

                if (assistant.IsError)
                {
                    Emit(new TurnEndEvent { Message = assistant, ToolResults = new() });
                    break;
                }

                var toolCalls = assistant.GetToolCalls();
                if (toolCalls.Count == 0 && assistant.StopReason != "toolUse")
                {
                    Emit(new TurnEndEvent { Message = assistant, ToolResults = new() });
                    break;
                }

                var toolResults = await ExecuteTools(toolCalls, tools, Emit, ct);
                foreach (var result in toolResults)
                {
                    context.Messages.Add(result);
                    produced.Add(result);
                }

                Emit(new TurnEndEvent { Message = assistant, ToolResults = toolResults });
            }

            Emit(new AgentEndEvent { Messages = produced });
            return produced;
        }

        private async Task<List<ToolResultMessage>> ExecuteTools(
            List<ToolCall> toolCalls,
            List<AgentTool>? tools,
            Action<AgentEvent> emit,
            CancellationToken ct)
        {
            var results = new List<ToolResultMessage>();

            foreach (var call in toolCalls)
            {
                ct.ThrowIfCancellationRequested();

                emit(new ToolExecutionStartEvent
                {
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    Args = call.Arguments
                });

                var tool = tools?.FirstOrDefault(t => t.Definition.Name == call.Name);
                string resultText;
                bool isError = false;

                if (tool == null)
                {
                    resultText = $"Tool '{call.Name}' not found";
                    isError = true;
                }
                else
                {
                    try
                    {
                        resultText = await tool.Execute(call.Arguments, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        resultText = "Tool execution cancelled";
                        isError = true;
                    }
                    catch (Exception ex)
                    {
                        resultText = $"Tool error: {ex.Message}";
                        isError = true;
                    }
                }

                var resultMsg = new ToolResultMessage
                {
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    Content = { new TextContent { Text = resultText } },
                    IsError = isError
                };

                results.Add(resultMsg);

                emit(new ToolExecutionEndEvent
                {
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    Result = resultText,
                    IsError = isError
                });

                emit(new MessageStartEvent { Message = resultMsg });
                emit(new MessageEndEvent { Message = resultMsg });
            }

            return results;
        }
    }
}
