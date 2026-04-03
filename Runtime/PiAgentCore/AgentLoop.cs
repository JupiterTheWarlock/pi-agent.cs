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
    /// Configuration for the agent loop, matching pi-agent-core's AgentLoopConfig.
    /// </summary>
    public class AgentLoopConfig
    {
        public ModelConfig Model { get; set; } = null!;
        public ThinkingLevel ThinkingLevel { get; set; } = ThinkingLevel.Off;
        public ToolExecutionMode ToolExecution { get; set; } = ToolExecutionMode.Parallel;

        /// <summary>Convert AgentMessages to LLM-compatible Messages before each LLM call.</summary>
        public Func<List<Message>, Task<List<Message>>>? ConvertToLlm { get; set; }

        /// <summary>Optional transform applied to context before convertToLlm.</summary>
        public Func<List<Message>, CancellationToken, Task<List<Message>>>? TransformContext { get; set; }

        /// <summary>Resolve an API key dynamically for each LLM call.</summary>
        public Func<string, Task<string?>>? GetApiKey { get; set; }

        /// <summary>Returns steering messages to inject mid-run.</summary>
        public Func<Task<List<Message>>>? GetSteeringMessages { get; set; }

        /// <summary>Returns follow-up messages after agent would otherwise stop.</summary>
        public Func<Task<List<Message>>>? GetFollowUpMessages { get; set; }

        /// <summary>Called before a tool is executed. Return block:true to prevent.</summary>
        public Func<BeforeToolCallContext, CancellationToken, Task<BeforeToolCallResult?>>? BeforeToolCall { get; set; }

        /// <summary>Called after a tool finishes executing. Return overrides for the result.</summary>
        public Func<AfterToolCallContext, CancellationToken, Task<AfterToolCallResult?>>? AfterToolCall { get; set; }
    }

    #region Hook Contexts

    public class BeforeToolCallContext
    {
        public AssistantMessage AssistantMessage { get; set; } = null!;
        public ToolCall ToolCall { get; set; } = null!;
        public Dictionary<string, object?> Args { get; set; } = new();
        public AgentContext Context { get; set; } = null!;
    }

    public class BeforeToolCallResult
    {
        public bool Block { get; set; }
        public string? Reason { get; set; }
    }

    public class AfterToolCallContext
    {
        public AssistantMessage AssistantMessage { get; set; } = null!;
        public ToolCall ToolCall { get; set; } = null!;
        public Dictionary<string, object?> Args { get; set; } = new();
        public string Result { get; set; } = "";
        public bool IsError { get; set; }
        public AgentContext Context { get; set; } = null!;
    }

    public class AfterToolCallResult
    {
        public List<object>? Content { get; set; }
        public bool? IsError { get; set; }
    }

    #endregion

    /// <summary>
    /// Core agent loop: prompt → LLM → tool calls → execute → feed back → repeat.
    /// Supports sequential/parallel tool execution, steering, follow-ups, hooks, 
    /// dynamic API keys, context transforms, and message conversion.
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
            return await Run(context, newMessages, tools, onEvent, ct, maxToolRounds, null);
        }

        /// <summary>
        /// Run with full configuration support.
        /// </summary>
        public async Task<List<Message>> Run(
            AgentContext context,
            List<Message> newMessages,
            List<AgentTool>? tools,
            Action<AgentEvent>? onEvent,
            CancellationToken ct,
            int maxToolRounds,
            AgentLoopConfig? config)
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

                // Transform context if configured
                if (config?.TransformContext != null)
                {
                    try
                    {
                        context.Messages = await config.TransformContext(context.Messages, ct);
                    }
                    catch
                    {
                        // Keep original on error
                    }
                }

                // Convert to LLM messages if configured
                var llmContext = context;
                if (config?.ConvertToLlm != null)
                {
                    try
                    {
                        var converted = await config.ConvertToLlm(context.Messages);
                        llmContext = new AgentContext(context.SystemPrompt, converted, tools);
                    }
                    catch
                    {
                        llmContext = context;
                    }
                }

                // Resolve API key dynamically
                if (config?.GetApiKey != null)
                {
                    try
                    {
                        var key = await config.GetApiKey(_model.Provider);
                        if (key != null)
                            _model.ApiKey = key;
                    }
                    catch { /* keep existing key */ }
                }

                Emit(new TurnStartEvent());

                AssistantMessage assistant;
                try
                {
                    assistant = await _client.Complete(llmContext, _model, ct);
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
                    // Check for follow-up messages
                    if (config?.GetFollowUpMessages != null)
                    {
                        try
                        {
                            var followUps = await config.GetFollowUpMessages();
                            if (followUps.Count > 0)
                            {
                                foreach (var f in followUps)
                                {
                                    context.Messages.Add(f);
                                    produced.Add(f);
                                }
                                continue; // Another turn
                            }
                        }
                        catch { /* no follow-ups */ }
                    }

                    Emit(new TurnEndEvent { Message = assistant, ToolResults = new() });
                    break;
                }

                // Execute tools (sequential or parallel)
                var toolResults = config?.ToolExecution == ToolExecutionMode.Sequential
                    ? await ExecuteToolsSequential(toolCalls, tools, Emit, ct, config, context, assistant)
                    : await ExecuteToolsParallel(toolCalls, tools, Emit, ct, config, context, assistant);

                foreach (var result in toolResults)
                {
                    context.Messages.Add(result);
                    produced.Add(result);
                }

                // Check for steering messages
                if (config?.GetSteeringMessages != null)
                {
                    try
                    {
                        var steering = await config.GetSteeringMessages();
                        if (steering.Count > 0)
                        {
                            foreach (var s in steering)
                            {
                                context.Messages.Add(s);
                                produced.Add(s);
                            }
                        }
                    }
                    catch { /* no steering */ }
                }

                Emit(new TurnEndEvent { Message = assistant, ToolResults = toolResults });
            }

            Emit(new AgentEndEvent { Messages = produced });
            return produced;
        }

        /// <summary>
        /// Execute tools sequentially (one by one).
        /// </summary>
        private async Task<List<ToolResultMessage>> ExecuteToolsSequential(
            List<ToolCall> toolCalls,
            List<AgentTool>? tools,
            Action<AgentEvent> emit,
            CancellationToken ct,
            AgentLoopConfig? config,
            AgentContext context,
            AssistantMessage assistant)
        {
            var results = new List<ToolResultMessage>();
            foreach (var call in toolCalls)
            {
                var result = await ExecuteSingleTool(call, tools, emit, ct, config, context, assistant);
                results.Add(result);
            }
            return results;
        }

        /// <summary>
        /// Execute tools in parallel (prepare sequentially, execute concurrently).
        /// </summary>
        private async Task<List<ToolResultMessage>> ExecuteToolsParallel(
            List<ToolCall> toolCalls,
            List<AgentTool>? tools,
            Action<AgentEvent> emit,
            CancellationToken ct,
            AgentLoopConfig? config,
            AgentContext context,
            AssistantMessage assistant)
        {
            // Phase 1: beforeToolCall hooks (sequential)
            var preflightResults = new (ToolCall call, bool blocked, string? reason, AgentTool? tool)[toolCalls.Count];
            for (int i = 0; i < toolCalls.Count; i++)
            {
                var call = toolCalls[i];
                var tool = tools?.FirstOrDefault(t => t.Definition.Name == call.Name);

                if (config?.BeforeToolCall != null)
                {
                    try
                    {
                        var hookCtx = new BeforeToolCallContext
                        {
                            AssistantMessage = assistant,
                            ToolCall = call,
                            Args = call.Arguments,
                            Context = context
                        };
                        var result = await config.BeforeToolCall(hookCtx, ct);
                        if (result?.Block == true)
                        {
                            preflightResults[i] = (call, true, result.Reason, tool);
                            continue;
                        }
                    }
                    catch { /* allow execution */ }
                }
                preflightResults[i] = (call, false, null, tool);
            }

            // Phase 2: Execute allowed tools in parallel
            var tasks = new List<(int index, Task<ToolResultMessage> task)>();
            for (int i = 0; i < preflightResults.Length; i++)
            {
                var (call, blocked, reason, tool) = preflightResults[i];
                if (blocked)
                {
                    // Create error result inline (no async needed)
                    var errorResult = new ToolResultMessage
                    {
                        ToolCallId = call.Id,
                        ToolName = call.Name,
                        Content = { new TextContent { Text = reason ?? "Tool call was blocked" } },
                        IsError = true
                    };
                    emit(new ToolExecutionStartEvent { ToolCallId = call.Id, ToolName = call.Name, Args = call.Arguments });
                    emit(new ToolExecutionEndEvent { ToolCallId = call.Id, ToolName = call.Name, Result = errorResult.Content[0] is TextContent tc ? tc.Text : "", IsError = true });
                    tasks.Add((i, Task.FromResult(errorResult)));
                }
                else
                {
                    var capturedTool = tool;
                    var capturedCall = call;
                    tasks.Add((i, ExecuteSingleToolAsync(capturedCall, capturedTool, emit, ct)));
                }
            }

            await Task.WhenAll(tasks.Select(t => t.task));

            // Phase 3: afterToolCall hooks (sequential, in source order)
            var orderedResults = new ToolResultMessage[preflightResults.Length];
            foreach (var (index, task) in tasks.OrderBy(t => t.index))
            {
                var result = await task;
                orderedResults[index] = result;
            }

            for (int i = 0; i < orderedResults.Length; i++)
            {
                if (config?.AfterToolCall != null)
                {
                    try
                    {
                        var call = toolCalls[i];
                        var hookCtx = new AfterToolCallContext
                        {
                            AssistantMessage = assistant,
                            ToolCall = call,
                            Args = call.Arguments,
                            Result = orderedResults[i].Content[0] is TextContent tc ? tc.Text : "",
                            IsError = orderedResults[i].IsError,
                            Context = context
                        };
                        var overrideResult = await config.AfterToolCall(hookCtx, ct);
                        if (overrideResult != null)
                        {
                            if (overrideResult.Content != null)
                                orderedResults[i].Content = overrideResult.Content;
                            if (overrideResult.IsError.HasValue)
                                orderedResults[i].IsError = overrideResult.IsError.Value;
                        }
                    }
                    catch { /* keep original */ }
                }
            }

            return orderedResults.ToList();
        }

        /// <summary>
        /// Execute a single tool call with hooks.
        /// </summary>
        private async Task<ToolResultMessage> ExecuteSingleTool(
            ToolCall call, List<AgentTool>? tools, Action<AgentEvent> emit, CancellationToken ct,
            AgentLoopConfig? config, AgentContext context, AssistantMessage assistant)
        {
            // Before hook
            if (config?.BeforeToolCall != null)
            {
                try
                {
                    var hookCtx = new BeforeToolCallContext
                    {
                        AssistantMessage = assistant,
                        ToolCall = call,
                        Args = call.Arguments,
                        Context = context
                    };
                    var result = await config.BeforeToolCall(hookCtx, ct);
                    if (result?.Block == true)
                    {
                        var blockedMsg = new ToolResultMessage
                        {
                            ToolCallId = call.Id,
                            ToolName = call.Name,
                            Content = { new TextContent { Text = result.Reason ?? "Tool call was blocked" } },
                            IsError = true
                        };
                        emit(new ToolExecutionStartEvent { ToolCallId = call.Id, ToolName = call.Name, Args = call.Arguments });
                        emit(new ToolExecutionEndEvent { ToolCallId = call.Id, ToolName = call.Name, Result = blockedMsg.Content[0] is TextContent tc ? tc.Text : "", IsError = true });
                        return blockedMsg;
                    }
                }
                catch { /* allow execution */ }
            }

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

            // After hook
            if (config?.AfterToolCall != null)
            {
                try
                {
                    var hookCtx = new AfterToolCallContext
                    {
                        AssistantMessage = assistant,
                        ToolCall = call,
                        Args = call.Arguments,
                        Result = resultText,
                        IsError = isError,
                        Context = context
                    };
                    var overrideResult = await config.AfterToolCall(hookCtx, ct);
                    if (overrideResult != null)
                    {
                        if (overrideResult.Content != null)
                            resultMsg.Content = overrideResult.Content;
                        if (overrideResult.IsError.HasValue)
                            resultMsg.IsError = overrideResult.IsError.Value;
                    }
                }
                catch { /* keep original */ }
            }

            emit(new ToolExecutionEndEvent
            {
                ToolCallId = call.Id,
                ToolName = call.Name,
                Result = resultText,
                IsError = resultMsg.IsError
            });

            emit(new MessageStartEvent { Message = resultMsg });
            emit(new MessageEndEvent { Message = resultMsg });

            return resultMsg;
        }

        /// <summary>
        /// Fire-and-forget tool execution for parallel mode (no hooks, hooks handled in orchestrator).
        /// </summary>
        private async Task<ToolResultMessage> ExecuteSingleToolAsync(
            ToolCall call, AgentTool? tool, Action<AgentEvent> emit, CancellationToken ct)
        {
            emit(new ToolExecutionStartEvent
            {
                ToolCallId = call.Id,
                ToolName = call.Name,
                Args = call.Arguments
            });

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

            emit(new ToolExecutionEndEvent
            {
                ToolCallId = call.Id,
                ToolName = call.Name,
                Result = resultText,
                IsError = isError
            });

            return resultMsg;
        }
    }
}
