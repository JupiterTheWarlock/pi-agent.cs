using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PiAgent.Models;

namespace PiAgent.LLM
{
    /// <summary>
    /// OpenAI-compatible API client. Covers OpenAI, Azure, Groq, Together, 
    /// OpenRouter, Zai, and any other provider with the same chat completions format.
    /// </summary>
    public class OpenAIClient : ILLMClient
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly HttpClient _http;

        public OpenAIClient(HttpClient? http = null)
        {
            _http = http ?? new HttpClient();
        }

        public async Task<AssistantMessage> Complete(AgentContext context, ModelConfig model, CancellationToken ct = default)
        {
            var request = BuildRequest(context, model, stream: false);
            var json = JsonSerializer.Serialize(request, JsonOpts);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{model.BaseUrl}/chat/completions", content, ct);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new LLMException($"LLM API error {response.StatusCode}: {body}");

            return ParseResponse(body, model);
        }

        public async Task<AssistantMessage> Stream(AgentContext context, ModelConfig model,
            Action<string>? onTextDelta = null, Action<ToolCall>? onToolCallDelta = null,
            CancellationToken ct = default)
        {
            var request = BuildRequest(context, model, stream: true);
            var json = JsonSerializer.Serialize(request, JsonOpts);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{model.BaseUrl}/chat/completions", content,
                HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new LLMException($"LLM API error {response.StatusCode}: {body}");

            return ParseStreamResponse(body, model, onTextDelta, onToolCallDelta);
        }

        private Dictionary<string, object?> BuildRequest(AgentContext context, ModelConfig model, bool stream)
        {
            var messages = new List<Dictionary<string, object?>>();

            if (!string.IsNullOrEmpty(context.SystemPrompt))
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "system",
                    ["content"] = context.SystemPrompt
                });
            }

            foreach (var msg in context.Messages)
            {
                messages.Add(SerializeMessage(msg));
            }

            var req = new Dictionary<string, object?>
            {
                ["model"] = model.Id,
                ["messages"] = messages,
                ["max_tokens"] = model.MaxTokens,
                ["temperature"] = model.Temperature,
                ["stream"] = stream,
            };

            if (context.Tools != null && context.Tools.Count > 0)
            {
                req["tools"] = context.Tools.Select(t => new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = t.Definition.Name,
                        ["description"] = t.Definition.Description,
                        ["parameters"] = t.Definition.Parameters,
                    }
                }).ToArray();
            }

            return req;
        }

        private Dictionary<string, object?> SerializeMessage(Message msg)
        {
            return msg switch
            {
                UserMessage um => new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = um.Text ?? ""
                },
                AssistantMessage am => SerializeAssistantMessage(am),
                ToolResultMessage tr => new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = tr.ToolCallId,
                    ["content"] = GetTextContent(tr.Content)
                },
                _ => throw new ArgumentException($"Unknown message type: {msg.GetType()}")
            };
        }

        private Dictionary<string, object?> SerializeAssistantMessage(AssistantMessage am)
        {
            var dict = new Dictionary<string, object?> { ["role"] = "assistant" };

            var textParts = new List<string>();
            var toolCalls = new List<ToolCall>();

            foreach (var item in am.Content)
            {
                switch (item)
                {
                    case TextContent tc:
                        textParts.Add(tc.Text);
                        break;
                    case ToolCall tc:
                        toolCalls.Add(tc);
                        break;
                }
            }

            if (textParts.Count > 0)
                dict["content"] = string.Join("", textParts);

            if (toolCalls.Count > 0)
            {
                dict["tool_calls"] = toolCalls.Select(tc => new Dictionary<string, object?>
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = JsonSerializer.Serialize(tc.Arguments)
                    }
                }).ToArray();
            }

            return dict;
        }

        private static string GetTextContent(List<object> content)
        {
            var parts = new List<string>();
            foreach (var item in content)
            {
                if (item is TextContent tc) parts.Add(tc.Text);
                else if (item is string s) parts.Add(s);
            }
            return string.Join("", parts);
        }

        private AssistantMessage ParseResponse(string body, ModelConfig model)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var choice = root.GetProperty("choices")[0];
            var msg = choice.GetProperty("message");

            var assistant = new AssistantMessage
            {
                Usage = ParseUsage(root),
                StopReason = choice.GetProperty("finish_reason").GetString() ?? "stop"
            };

            if (msg.TryGetProperty("content", out var contentEl) && contentEl.ValueKind != JsonValueKind.Null)
            {
                var text = contentEl.GetString() ?? "";
                if (!string.IsNullOrEmpty(text))
                    assistant.Content.Add(new TextContent { Text = text });
            }

            if (msg.TryGetProperty("tool_calls", out var tcEl))
            {
                foreach (var tc in tcEl.EnumerateArray())
                {
                    var fn = tc.GetProperty("function");
                    var argsStr = fn.GetProperty("arguments").GetString() ?? "{}";
                    var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsStr) ?? new();

                    assistant.Content.Add(new ToolCall
                    {
                        Id = tc.GetProperty("id").GetString() ?? "",
                        Name = fn.GetProperty("name").GetString() ?? "",
                        Arguments = args
                    });
                }

                if (assistant.Content.Count > 0 && assistant.StopReason != "tool_calls")
                    assistant.StopReason = "toolUse";
            }

            return assistant;
        }

        /// <summary>
        /// Parse SSE stream response (simplified: processes all lines at once).
        /// For production, use a proper streaming parser with逐行 processing.
        /// </summary>
        private AssistantMessage ParseStreamResponse(string body, ModelConfig model,
            Action<string>? onTextDelta, Action<ToolCall>? onToolCallDelta)
        {
            var content = new StringBuilder();
            var toolCalls = new Dictionary<int, ToolCall>();
            var finishReason = "stop";
            Usage? usage = null;

            foreach (var line in body.Split('\n'))
            {
                if (!line.StartsWith("data: ")) continue;
                var data = line.Substring(6).Trim();
                if (data == "[DONE]") continue;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    var choice = root.GetProperty("choices")[0];
                    var delta = choice.GetProperty("message");

                    if (delta.TryGetProperty("content", out var cEl) && cEl.ValueKind != JsonValueKind.Null)
                    {
                        var text = cEl.GetString() ?? "";
                        content.Append(text);
                        onTextDelta?.Invoke(text);
                    }

                    if (delta.TryGetProperty("tool_calls", out var tcEl))
                    {
                        foreach (var tcItem in tcEl.EnumerateArray())
                        {
                            var idx = tcItem.GetProperty("index").GetInt32();
                            if (!toolCalls.TryGetValue(idx, out var tc))
                            {
                                tc = new ToolCall();
                                toolCalls[idx] = tc;
                            }

                            if (tcItem.TryGetProperty("id", out var idEl))
                                tc.Id = idEl.GetString() ?? tc.Id;
                            if (tcItem.TryGetProperty("function", out var fnEl))
                            {
                                if (fnEl.TryGetProperty("name", out var nameEl))
                                    tc.Name = nameEl.GetString() ?? tc.Name;
                                if (fnEl.TryGetProperty("arguments", out var argsEl))
                                {
                                    var partialArgs = argsEl.GetString() ?? "";
                                    // Merge partial JSON arguments
                                    if (tc.Arguments.Count == 0)
                                    {
                                        try
                                        {
                                            tc.Arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(partialArgs) ?? new();
                                        }
                                        catch
                                        {
                                            // Partial JSON, store raw
                                            tc.Arguments["__raw"] = partialArgs;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (choice.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind != JsonValueKind.Null)
                        finishReason = frEl.GetString() ?? finishReason;

                    if (root.TryGetProperty("usage", out var usageEl))
                        usage = new Usage(
                            usageEl.GetProperty("prompt_tokens").GetInt32(),
                            usageEl.GetProperty("completion_tokens").GetInt32(),
                            usageEl.GetProperty("total_tokens").GetInt32()
                        );
                }
                catch { /* skip malformed lines */ }
            }

            var assistant = new AssistantMessage
            {
                Usage = usage ?? Usage.Zero,
                StopReason = finishReason == "tool_calls" ? "toolUse" : finishReason
            };

            if (content.Length > 0)
                assistant.Content.Add(new TextContent { Text = content.ToString() });

            foreach (var tc in toolCalls.Values.OrderBy(kv => true))
            {
                assistant.Content.Add(tc);
                onToolCallDelta?.Invoke(tc);
            }

            return assistant;
        }

        private static Usage ParseUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var u)) return Usage.Zero;
            return new Usage(
                u.GetProperty("prompt_tokens").GetInt32(),
                u.GetProperty("completion_tokens").GetInt32(),
                u.GetProperty("total_tokens").GetInt32()
            );
        }
    }

    public class LLMException : Exception
    {
        public LLMException(string message) : base(message) { }
    }
}
