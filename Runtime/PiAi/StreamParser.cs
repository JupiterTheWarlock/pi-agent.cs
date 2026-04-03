using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PiAgent.PiAi
{
    /// <summary>
    /// Events emitted during SSE stream parsing, matching pi-ai's AssistantMessageEvent protocol.
    /// </summary>
    public abstract class StreamEvent
    {
        public abstract string Type { get; }
    }

    public class StreamStartEvent : StreamEvent
    {
        public override string Type => "start";
        public AssistantMessage Partial { get; set; } = null!;
    }

    public class TextStartEvent : StreamEvent
    {
        public override string Type => "text_start";
        public int ContentIndex { get; set; }
        public AssistantMessage Partial { get; set; } = null!;
    }

    public class TextDeltaEvent : StreamEvent
    {
        public override string Type => "text_delta";
        public int ContentIndex { get; set; }
        public string Delta { get; set; } = "";
        public AssistantMessage Partial { get; set; } = null!;
    }

    public class TextEndEvent : StreamEvent
    {
        public override string Type => "text_end";
        public int ContentIndex { get; set; }
        public string Content { get; set; } = "";
        public AssistantMessage Partial { get; set; } = null!;
    }

    public class ThinkingStartEvent : StreamEvent
    {
        public override string Type => "thinking_start";
        public int ContentIndex { get; set; }
        public AssistantMessage Partial { get; set; } = null!;
    }

    public class ThinkingDeltaEvent : StreamEvent
    {
        public override string Type => "thinking_delta";
        public int ContentIndex { get; set; }
        public string Delta { get; set; } = "";
        public AssistantMessage Partial { get; set; } = null!;
    }

    public class ThinkingEndEvent : StreamEvent
    {
        public override string Type => "thinking_end";
        public int ContentIndex { get; set; }
        public string Content { get; set; } = "";
        public AssistantMessage Partial { get; set; } = null!;
    }

    public class ToolCallStartEvent : StreamEvent
    {
        public override string Type => "toolcall_start";
        public int ContentIndex { get; set; }
        public AssistantMessage Partial { get; set; } = null!;
    }

    public class ToolCallDeltaEvent : StreamEvent
    {
        public override string Type => "toolcall_delta";
        public int ContentIndex { get; set; }
        public string Delta { get; set; } = "";
        public AssistantMessage Partial { get; set; } = null!;
    }

    public class ToolCallEndEvent : StreamEvent
    {
        public override string Type => "toolcall_end";
        public int ContentIndex { get; set; }
        public ToolCall ToolCall { get; set; } = null!;
        public AssistantMessage Partial { get; set; } = null!;
    }

    public class StreamDoneEvent : StreamEvent
    {
        public override string Type => "done";
        public string Reason { get; set; } = "stop";
        public AssistantMessage Message { get; set; } = null!;
    }

    public class StreamErrorEvent : StreamEvent
    {
        public override string Type => "error";
        public string Reason { get; set; } = "error";
        public AssistantMessage Error { get; set; } = null!;
    }

    /// <summary>
    /// SSE stream parser for OpenAI-compatible streaming responses.
    /// Parses line-by-line and emits typed StreamEvents.
    /// </summary>
    public class StreamParser
    {
        private readonly AssistantMessage _partial = new AssistantMessage();
        private int _contentIndex;
        private string _currentText = "";
        private string _currentThinking = "";
        private readonly Dictionary<int, ToolCall> _toolCalls = new();
        private readonly Dictionary<int, string> _toolCallArgs = new();
        private string _finishReason = "stop";
        private Usage _usage = Usage.Zero;
        private bool _done;

        /// <summary>
        /// Get the final assembled AssistantMessage after parsing is complete.
        /// </summary>
        public AssistantMessage GetMessage() => _partial;

        /// <summary>
        /// Parse a single SSE line (e.g., "data: {...}").
        /// Returns events that should be emitted, or null if no event.
        /// </summary>
        public List<StreamEvent>? ParseLine(string line)
        {
            if (!line.StartsWith("data: ")) return null;
            var data = line.Substring(6).Trim();
            if (data == "[DONE]") return HandleDone();

            try
            {
                using var doc = JsonDocument.Parse(data);
                return ParseJson(doc.RootElement);
            }
            catch
            {
                return null; // skip malformed
            }
        }

        /// <summary>
        /// Parse all SSE data from a complete response body (for non-streaming scenarios).
        /// </summary>
        public List<StreamEvent> ParseAll(string body)
        {
            var events = new List<StreamEvent>();
            foreach (var line in body.Split('\n'))
            {
                var e = ParseLine(line.Trim());
                if (e != null) events.AddRange(e);
            }
            if (!_done) events.AddRange(HandleDone());
            return events;
        }

        private List<StreamEvent>? ParseJson(JsonElement root)
        {
            var events = new List<StreamEvent>();

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                // Might be a usage-only chunk
                if (root.TryGetProperty("usage", out var u))
                    _usage = ParseUsage(u);
                return null;
            }

            var choice = choices[0];
            var delta = choice.GetProperty("delta");

            // Parse content text delta
            if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind != JsonValueKind.Null)
            {
                var text = contentEl.GetString() ?? "";
                if (!string.IsNullOrEmpty(text))
                {
                    events.Add(new TextDeltaEvent
                    {
                        ContentIndex = _contentIndex,
                        Delta = text,
                        Partial = _partial
                    });
                    _currentText += text;
                }
            }

            // Parse reasoning/thinking delta
            if (delta.TryGetProperty("reasoning_content", out var reasoningEl) && reasoningEl.ValueKind != JsonValueKind.Null)
            {
                var text = reasoningEl.GetString() ?? "";
                if (!string.IsNullOrEmpty(text))
                {
                    events.Add(new ThinkingDeltaEvent
                    {
                        ContentIndex = _contentIndex,
                        Delta = text,
                        Partial = _partial
                    });
                    _currentThinking += text;
                }
            }

            // Parse tool call deltas
            if (delta.TryGetProperty("tool_calls", out var tcEl))
            {
                foreach (var tcItem in tcEl.EnumerateArray())
                {
                    var idx = tcItem.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : 0;

                    if (!_toolCalls.TryGetValue(idx, out var tc))
                    {
                        tc = new ToolCall();
                        _toolCalls[idx] = tc;
                        _toolCallArgs[idx] = "";
                        events.Add(new ToolCallStartEvent
                        {
                            ContentIndex = idx,
                            Partial = _partial
                        });
                    }

                    if (tcItem.TryGetProperty("id", out var idEl))
                        tc.Id = idEl.GetString() ?? tc.Id;
                    if (tcItem.TryGetProperty("function", out var fnEl))
                    {
                        if (fnEl.TryGetProperty("name", out var nameEl))
                            tc.Name = nameEl.GetString() ?? tc.Name;
                        if (fnEl.TryGetProperty("arguments", out var argsEl))
                        {
                            var argDelta = argsEl.GetString() ?? "";
                            _toolCallArgs[idx] += argDelta;
                            events.Add(new ToolCallDeltaEvent
                            {
                                ContentIndex = idx,
                                Delta = argDelta,
                                Partial = _partial
                            });
                        }
                    }
                }
            }

            // Parse finish reason
            if (choice.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind != JsonValueKind.Null)
                _finishReason = frEl.GetString() ?? _finishReason;

            // Parse usage
            if (root.TryGetProperty("usage", out var usageEl))
                _usage = ParseUsage(usageEl);

            return events.Count > 0 ? events : null;
        }

        private List<StreamEvent> HandleDone()
        {
            _done = true;
            var events = new List<StreamEvent>();

            // Finalize text
            if (!string.IsNullOrEmpty(_currentText))
            {
                _partial.Content.Add(new TextContent { Text = _currentText });
                events.Add(new TextEndEvent
                {
                    ContentIndex = _contentIndex,
                    Content = _currentText,
                    Partial = _partial
                });
            }

            // Finalize thinking
            if (!string.IsNullOrEmpty(_currentThinking))
            {
                _partial.Content.Add(new ThinkingContent { Thinking = _currentThinking });
                events.Add(new ThinkingEndEvent
                {
                    ContentIndex = _contentIndex,
                    Content = _currentThinking,
                    Partial = _partial
                });
            }

            // Finalize tool calls
            foreach (var kv in _toolCalls.OrderBy(x => x.Key))
            {
                var tc = kv.Value;
                try
                {
                    tc.Arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(_toolCallArgs[kv.Key]) ?? new();
                }
                catch
                {
                    tc.Arguments = new Dictionary<string, object?> { ["__raw"] = _toolCallArgs[kv.Key] };
                }
                _partial.Content.Add(tc);
                events.Add(new ToolCallEndEvent
                {
                    ContentIndex = kv.Key,
                    ToolCall = tc,
                    Partial = _partial
                });
            }

            _partial.Usage = _usage;
            _partial.StopReason = _finishReason == "tool_calls" ? "toolUse" : _finishReason;

            if (_partial.StopReason == "error" || _partial.StopReason == "aborted")
            {
                events.Add(new StreamErrorEvent
                {
                    Reason = _partial.StopReason,
                    Error = _partial
                });
            }
            else
            {
                events.Add(new StreamDoneEvent
                {
                    Reason = _partial.StopReason,
                    Message = _partial
                });
            }

            return events;
        }

        private static Usage ParseUsage(JsonElement u)
        {
            var usage = new Usage();
            if (u.TryGetProperty("prompt_tokens", out var input))
                usage.InputTokens = input.GetInt32();
            if (u.TryGetProperty("completion_tokens", out var output))
                usage.OutputTokens = output.GetInt32();
            if (u.TryGetProperty("total_tokens", out var total))
                usage.TotalTokens = total.GetInt32();
            if (u.TryGetProperty("prompt_tokens_details", out var details))
            {
                if (details.TryGetProperty("cached_tokens", out var cached))
                    usage.CacheReadTokens = cached.GetInt32();
            }
            return usage;
        }
    }
}
