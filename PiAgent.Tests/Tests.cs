using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PiAgent.Core;
using PiAgent.LLM;
using PiAgent.Models;
using PiAgent.Tools;
using Xunit;

namespace PiAgent.Tests
{
    #region Mock HTTP Handler

    /// <summary>
    /// Mock HttpMessageHandler for testing without real API calls.
    /// </summary>
    public class MockHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Handler { get; set; } = null!;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Handler(request);
        }
    }

    /// <summary>
    /// Creates mock LLM responses for testing.
    /// </summary>
    public static class MockLLM
    {
        /// <summary>
        /// Create a mock ILLMClient that returns a text response.
        /// </summary>
        public static ILLMClient TextResponse(string text, string stopReason = "stop")
        {
            return new MockLLMClient(async (ctx, model, ct) =>
            {
                await Task.Yield();
                return new AssistantMessage
                {
                    Content = { new TextContent { Text = text } },
                    StopReason = stopReason,
                    Usage = new Usage(10, 20, 30)
                };
            });
        }

        /// <summary>
        /// Create a mock ILLMClient that returns a tool call response.
        /// </summary>
        public static ILLMClient ToolCallResponse(string toolName, string toolCallId, Dictionary<string, object?> args)
        {
            return new MockLLMClient(async (ctx, model, ct) =>
            {
                await Task.Yield();
                return new AssistantMessage
                {
                    Content =
                    {
                        new TextContent { Text = "I'll use a tool." },
                        new ToolCall { Id = toolCallId, Name = toolName, Arguments = args }
                    },
                    StopReason = "toolUse",
                    Usage = new Usage(10, 20, 30)
                };
            });
        }

        /// <summary>
        /// Create a mock that returns a sequence of responses (for multi-turn testing).
        /// </summary>
        public static ILLMClient Sequence(params Func<AgentContext, ModelConfig, Task<AssistantMessage>>[] responses)
        {
            var index = 0;
            return new MockLLMClient(async (ctx, model, ct) =>
            {
                var i = index++;
                if (i < responses.Length)
                    return await responses[i](ctx, model);
                return new AssistantMessage
                {
                    Content = { new TextContent { Text = "No more responses configured" } },
                    StopReason = "stop"
                };
            });
        }
    }

    /// <summary>
    /// Mock LLM client for testing.
    /// </summary>
    public class MockLLMClient : ILLMClient
    {
        private readonly Func<AgentContext, ModelConfig, CancellationToken, Task<AssistantMessage>> _handler;

        public MockLLMClient(Func<AgentContext, ModelConfig, CancellationToken, Task<AssistantMessage>> handler)
        {
            _handler = handler;
        }

        public Task<AssistantMessage> Complete(AgentContext context, ModelConfig model, CancellationToken ct = default)
        {
            return _handler(context, model, ct);
        }

        public Task<AssistantMessage> Stream(AgentContext context, ModelConfig model,
            Action<string>? onTextDelta = null, Action<ToolCall>? onToolCallDelta = null,
            CancellationToken ct = default)
        {
            return _handler(context, model, ct);
        }
    }

    #endregion

    #region Model Tests

    public class MessageTests
    {
        [Fact]
        public void UserMessage_HasCorrectRole()
        {
            var msg = new UserMessage("hello");
            Assert.Equal("user", msg.Role);
            Assert.Equal("hello", msg.Text);
        }

        [Fact]
        public void AssistantMessage_GetText_ReturnsConcatenatedText()
        {
            var msg = new AssistantMessage();
            msg.Content.Add(new TextContent { Text = "Hello " });
            msg.Content.Add(new TextContent { Text = "World" });
            Assert.Equal("Hello World", msg.GetText());
        }

        [Fact]
        public void AssistantMessage_GetToolCalls_ExtractsToolCalls()
        {
            var msg = new AssistantMessage();
            msg.Content.Add(new TextContent { Text = "text" });
            msg.Content.Add(new ToolCall { Id = "tc1", Name = "foo", Arguments = new() });
            msg.Content.Add(new ToolCall { Id = "tc2", Name = "bar", Arguments = new() });

            var calls = msg.GetToolCalls();
            Assert.Equal(2, calls.Count);
            Assert.Equal("tc1", calls[0].Id);
            Assert.Equal("bar", calls[1].Name);
        }

        [Fact]
        public void AssistantMessage_IsError_ForStopReasons()
        {
            var err = new AssistantMessage { StopReason = "error" };
            var abort = new AssistantMessage { StopReason = "aborted" };
            var ok = new AssistantMessage { StopReason = "stop" };

            Assert.True(err.IsError);
            Assert.True(abort.IsError);
            Assert.False(ok.IsError);
        }

        [Fact]
        public void ToolResultMessage_HasCorrectRole()
        {
            var msg = new ToolResultMessage
            {
                ToolCallId = "tc1",
                ToolName = "foo",
                Content = { new TextContent { Text = "result" } }
            };
            Assert.Equal("toolResult", msg.Role);
            Assert.Equal("tc1", msg.ToolCallId);
        }
    }

    public class UsageTests
    {
        [Fact]
        public void Zero_ReturnsZeroValues()
        {
            var u = Usage.Zero;
            Assert.Equal(0, u.InputTokens);
            Assert.Equal(0, u.OutputTokens);
            Assert.Equal(0, u.TotalTokens);
        }

        [Fact]
        public void Constructor_SetsValues()
        {
            var u = new Usage(100, 200, 300);
            Assert.Equal(100, u.InputTokens);
            Assert.Equal(200, u.OutputTokens);
            Assert.Equal(300, u.TotalTokens);
        }
    }

    public class ToolDefinitionTests
    {
        [Fact]
        public void NoParams_CreatesEmptySchema()
        {
            var def = ToolDefinition.NoParams("test", "A test tool");
            Assert.Equal("test", def.Name);
            Assert.Equal("A test tool", def.Description);
            Assert.Empty(def.Parameters.Properties);
        }
    }

    #endregion

    #region ToolRegistry Tests

    public class ToolRegistryTests
    {
        [Fact]
        public void Define_NoParam_RegistersTool()
        {
            var registry = new ToolRegistry();
            registry.Define("echo", "Echo hello", () => "hello");

            Assert.Single(registry.Tools);
            Assert.Equal("echo", registry.Tools[0].Definition.Name);
        }

        [Fact]
        public async Task Define_NoParam_ExecutesCorrectly()
        {
            var registry = new ToolRegistry();
            registry.Define("greet", "Greet", () => "Hi!");

            var tool = registry.Find("greet");
            Assert.NotNull(tool);
            var result = await tool.Execute(new(), CancellationToken.None);
            Assert.Equal("Hi!", result);
        }

        [Fact]
        public async Task Define_WithParam_ExecutesCorrectly()
        {
            var registry = new ToolRegistry();
            registry.Define<string>("echo", "Echo input", (s) => Task.FromResult(s));

            var tool = registry.Find("echo");
            Assert.NotNull(tool);

            // Schema should have "value" property for string param
            Assert.True(tool.Definition.Parameters.Properties.ContainsKey("value"));

            var result = await tool.Execute(
                new Dictionary<string, object?> { ["value"] = "test input" },
                CancellationToken.None);
            Assert.Equal("test input", result);
        }

        [Fact]
        public async Task Define_WithParam_CancellationToken_ExecutesCorrectly()
        {
            var registry = new ToolRegistry();
            registry.Define<string>("work", "Do work", async (s, ct) =>
            {
                await Task.Delay(10, ct);
                return $"done: {s}";
            });

            var tool = registry.Find("work");
            Assert.NotNull(tool);
            var result = await tool.Execute(
                new Dictionary<string, object?> { ["value"] = "task1" },
                CancellationToken.None);
            Assert.Equal("done: task1", result);
        }

        [Fact]
        public void Find_ReturnsNull_WhenNotFound()
        {
            var registry = new ToolRegistry();
            Assert.Null(registry.Find("nonexistent"));
        }

        [Fact]
        public async Task DefineAsync_NoParam_ExecutesCorrectly()
        {
            var registry = new ToolRegistry();
            registry.Define("compute", "Compute", async () =>
            {
                await Task.Yield();
                return "42";
            });

            var tool = registry.Find("compute");
            var result = await tool.Execute(new(), CancellationToken.None);
            Assert.Equal("42", result);
        }
    }

    #endregion

    #region AgentLoop Tests

    public class AgentLoopTests
    {
        private static ModelConfig TestModel => new("test-model", "https://fake.api/v1", "test-key");

        [Fact]
        public async Task Run_TextOnly_ReturnsAssistantMessage()
        {
            var client = MockLLM.TextResponse("Hello!");
            var loop = new AgentLoop(client, TestModel);

            var events = new List<AgentEvent>();
            var context = new AgentContext();
            var result = await loop.Run(context,
                new List<Message> { new UserMessage("Hi") },
                null, e => events.Add(e));

            Assert.Contains(result, m => m is AssistantMessage am && am.GetText() == "Hello!");
            Assert.Single(events.OfType<AgentEndEvent>());
        }

        [Fact]
        public async Task Run_ToolCall_ExecutesAndFeedsBack()
        {
            // First call: LLM returns tool call, second call: LLM returns final text
            var client = MockLLM.Sequence(
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new ToolCall { Id = "tc1", Name = "weather", Arguments = new() { ["value"] = "Beijing" } } },
                    StopReason = "toolUse",
                    Usage = new Usage(10, 5, 15)
                }),
                (ctx, model) =>
                {
                    // Verify tool result was added to context
                    var lastMsg = ctx.Messages[^1];
                    Assert.IsType<ToolResultMessage>(lastMsg);
                    return Task.FromResult(new AssistantMessage
                    {
                        Content = { new TextContent { Text = "Weather in Beijing: Sunny" } },
                        StopReason = "stop",
                        Usage = new Usage(20, 10, 30)
                    });
                }
            );

            var tools = new ToolRegistry();
            tools.Define<string>("weather", "Get weather", (city) => $"Weather in {city}: Sunny, 25°C");

            var loop = new AgentLoop(client, TestModel);
            var context = new AgentContext();
            var result = await loop.Run(context,
                new List<Message> { new UserMessage("What's the weather in Beijing?") },
                tools.GetAll());

            // Should have: user msg, assistant (tool call), tool result, assistant (final)
            Assert.Equal(4, result.Count);
            Assert.IsType<UserMessage>(result[0]);
            Assert.IsType<AssistantMessage>(result[1]); // tool call
            Assert.IsType<ToolResultMessage>(result[2]);
            Assert.IsType<AssistantMessage>(result[3]); // final response
            Assert.Equal("Weather in Beijing: Sunny", ((AssistantMessage)result[3]).GetText());
        }

        [Fact]
        public async Task Run_Error_StopGracefully()
        {
            var client = MockLLM.TextResponse("Oops", "error");
            var loop = new AgentLoop(client, TestModel);

            var context = new AgentContext();
            var result = await loop.Run(context,
                new List<Message> { new UserMessage("test") },
                null);

            var last = Assert.IsType<AssistantMessage>(result[^1]);
            Assert.Equal("error", last.StopReason);
        }

        [Fact]
        public async Task Run_Cancellation_StopsGracefully()
        {
            var client = MockLLM.Sequence(
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new TextContent { Text = "partial" } },
                    StopReason = "toolUse"
                }),
                (ctx, model) =>
                {
                    throw new OperationCanceledException();
                }
            );

            var loop = new AgentLoop(client, TestModel);
            var context = new AgentContext();
            var result = await loop.Run(context,
                new List<Message> { new UserMessage("test") },
                null);

            var last = Assert.IsType<AssistantMessage>(result[^1]);
            Assert.Equal("aborted", last.StopReason);
        }

        [Fact]
        public async Task Run_EmitsLifecycleEvents()
        {
            var client = MockLLM.TextResponse("OK");
            var loop = new AgentLoop(client, TestModel);

            var events = new List<AgentEvent>();
            var context = new AgentContext();
            await loop.Run(context,
                new List<Message> { new UserMessage("hi") },
                null, e => events.Add(e));

            Assert.Contains(events, e => e is AgentStartEvent);
            Assert.Contains(events, e => e is TurnStartEvent);
            Assert.Contains(events, e => e is TurnEndEvent);
            Assert.Contains(events, e => e is AgentEndEvent);
            Assert.Contains(events, e => e is MessageStartEvent);
            Assert.Contains(events, e => e is MessageEndEvent);
        }

        [Fact]
        public async Task Run_ToolNotFound_ReturnsError()
        {
            var client = MockLLM.TextResponse("", "stop"); // won't be reached
            client = MockLLM.Sequence(
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new ToolCall { Id = "tc1", Name = "nonexistent", Arguments = new() } },
                    StopReason = "toolUse"
                }),
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new TextContent { Text = "final" } },
                    StopReason = "stop"
                })
            );

            var loop = new AgentLoop(client, TestModel);
            var context = new AgentContext();
            var result = await loop.Run(context,
                new List<Message> { new UserMessage("test") },
                null);

            // Should have error tool result
            var toolResult = Assert.IsType<ToolResultMessage>(result[2]);
            Assert.True(toolResult.IsError);
            Assert.Contains("not found", toolResult.Content[0] is TextContent tc ? tc.Text : "");
        }

        [Fact]
        public async Task Run_ToolException_ReturnsErrorResult()
        {
            var tools = new ToolRegistry();
            tools.Define("boom", "Boom", (Func<string>)(() => throw new Exception("Kaboom!")));

            var client = MockLLM.Sequence(
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new ToolCall { Id = "tc1", Name = "boom", Arguments = new() } },
                    StopReason = "toolUse"
                }),
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new TextContent { Text = "recovered" } },
                    StopReason = "stop"
                })
            );

            var loop = new AgentLoop(client, TestModel);
            var context = new AgentContext();
            var result = await loop.Run(context,
                new List<Message> { new UserMessage("test") },
                tools.GetAll());

            var toolResult = Assert.IsType<ToolResultMessage>(result[2]);
            Assert.True(toolResult.IsError);
            Assert.Contains("Kaboom!", toolResult.Content[0] is TextContent tc ? tc.Text : "");
        }
    }

    #endregion

    #region Agent Tests

    public class AgentTests
    {
        private static ModelConfig TestModel => new("test-model", "https://fake.api/v1", "test-key");

        [Fact]
        public async Task Prompt_AddsMessagesAndReturns()
        {
            var agent = new Agent(TestModel, MockLLM.TextResponse("Hello!"));
            var result = await agent.Prompt("Hi");

            Assert.Equal(2, agent.Messages.Count); // user + assistant
            Assert.IsType<UserMessage>(agent.Messages[0]);
            Assert.IsType<AssistantMessage>(agent.Messages[1]);
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public async Task Prompt_WithTools_ExecutesTools()
        {
            var client = MockLLM.Sequence(
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new ToolCall { Id = "tc1", Name = "add", Arguments = new() { ["value"] = "5" } } },
                    StopReason = "toolUse"
                }),
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new TextContent { Text = "Result: 5" } },
                    StopReason = "stop"
                })
            );

            var agent = new Agent(TestModel, client);
            agent.DefineTool("add", "Add a number", (int n) => (n * 2).ToString());

            await agent.Prompt("Add 5");

            Assert.Equal(4, agent.Messages.Count);
            Assert.Equal("Result: 5", agent.GetLastResponse());
        }

        [Fact]
        public async Task GetLastResponse_ReturnsLastAssistantText()
        {
            var agent = new Agent(TestModel, MockLLM.TextResponse("Final answer"));
            await agent.Prompt("Question");

            Assert.Equal("Final answer", agent.GetLastResponse());
        }

        [Fact]
        public async Task Reset_ClearsMessages()
        {
            var agent = new Agent(TestModel, MockLLM.TextResponse("hi"));
            await agent.Prompt("test");
            Assert.NotEmpty(agent.Messages);

            agent.Reset();
            Assert.Empty(agent.Messages);
        }

        [Fact]
        public async Task Prompt_WhileRunning_Throws()
        {
            var agent = new Agent(TestModel, MockLLM.TextResponse(""));

            // Use a client that never completes
            var slowClient = new MockLLMClient(async (ctx, model, ct) =>
            {
                await Task.Delay(10000, ct);
                return new AssistantMessage();
            });

            var runningAgent = new Agent(TestModel, slowClient);
            var task = runningAgent.Prompt("test");

            await Task.Delay(100);
            Assert.True(runningAgent.IsRunning);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runningAgent.Prompt("another"));
        }

        [Fact]
        public async Task SystemPrompt_IncludedInContext()
        {
            string? capturedPrompt = null;
            var client = new MockLLMClient(async (ctx, model, ct) =>
            {
                capturedPrompt = ctx.SystemPrompt;
                return new AssistantMessage
                {
                    Content = { new TextContent { Text = "ok" } },
                    StopReason = "stop"
                };
            });

            var agent = new Agent(TestModel, client)
            {
                SystemPrompt = "You are a helpful assistant."
            };
            await agent.Prompt("hi");

            Assert.Equal("You are a helpful assistant.", capturedPrompt);
        }
    }

    #endregion

    #region OpenAI Client Serialization Tests

    public class OpenAIClientSerializationTests
    {
        [Fact]
        public async Task Complete_SendsCorrectRequest_AndParsesResponse()
        {
            var capturedJson = "";
            var responseJson = @"{
                ""id"": ""chatcmpl-123"",
                ""object"": ""chat.completion"",
                ""choices"": [{
                    ""index"": 0,
                    ""message"": {
                        ""role"": ""assistant"",
                        ""content"": ""Hello from test!""
                    },
                    ""finish_reason"": ""stop""
                }],
                ""usage"": {
                    ""prompt_tokens"": 10,
                    ""completion_tokens"": 5,
                    ""total_tokens"": 15
                }
            }";

            var handler = new MockHttpHandler
            {
                Handler = async (req) =>
                {
                    capturedJson = await req.Content!.ReadAsStringAsync();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                    };
                }
            };

            var client = new OpenAIClient(new HttpClient(handler));
            var model = new ModelConfig("gpt-3.5-turbo", "https://api.openai.com/v1", "sk-test");

            var context = new AgentContext
            {
                SystemPrompt = "Be helpful",
                Messages = { new UserMessage("Say hello") }
            };

            var result = await client.Complete(context, model);

            Assert.Equal("Hello from test!", result.GetText());
            Assert.Equal("stop", result.StopReason);
            Assert.Equal(10, result.Usage.InputTokens);

            // Verify request structure
            Assert.Contains("Be helpful", capturedJson);
            Assert.Contains("Say hello", capturedJson);
            Assert.Contains("gpt-3.5-turbo", capturedJson);
        }

        [Fact]
        public async Task Complete_ParsesToolCalls()
        {
            var responseJson = @"{
                ""id"": ""chatcmpl-123"",
                ""choices"": [{
                    ""message"": {
                        ""role"": ""assistant"",
                        ""content"": null,
                        ""tool_calls"": [{
                            ""id"": ""call_abc"",
                            ""type"": ""function"",
                            ""function"": {
                                ""name"": ""get_weather"",
                                ""arguments"": ""{\""city\"": \""Tokyo\""}""
                            }
                        }]
                    },
                    ""finish_reason"": ""tool_calls""
                }],
                ""usage"": { ""prompt_tokens"": 10, ""completion_tokens"": 5, ""total_tokens"": 15 }
            }";

            var handler = new MockHttpHandler
            {
                Handler = async (req) => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                }
            };

            var client = new OpenAIClient(new HttpClient(handler));
            var model = new ModelConfig("gpt-4", "https://api.openai.com/v1", "sk-test");

            var result = await client.Complete(new AgentContext(), model);

            Assert.Equal("toolUse", result.StopReason);
            var calls = result.GetToolCalls();
            Assert.Single(calls);
            Assert.Equal("call_abc", calls[0].Id);
            Assert.Equal("get_weather", calls[0].Name);
            Assert.Equal("Tokyo", calls[0].Arguments["city"]?.ToString());
        }

        [Fact]
        public async Task Complete_IncludesToolDefinitions()
        {
            string? capturedJson = null;
            var handler = new MockHttpHandler
            {
                Handler = async (req) =>
                {
                    capturedJson = await req.Content!.ReadAsStringAsync();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{""choices"":[{""message"":{""role"":""assistant"",""content"":""ok""},""finish_reason"":""stop""}],""usage"":{""prompt_tokens"":1,""completion_tokens"":1,""total_tokens"":2}}", Encoding.UTF8, "application/json")
                    };
                }
            };

            var client = new OpenAIClient(new HttpClient(handler));
            var model = new ModelConfig("gpt-4", "https://api.openai.com/v1", "sk-test");

            var tools = new ToolRegistry();
            tools.Define<string>("search", "Search the web", (q) => $"Results for: {q}");

            var context = new AgentContext
            {
                Messages = { new UserMessage("Find something") },
                Tools = tools.GetAll()
            };

            await client.Complete(context, model);

            Assert.NotNull(capturedJson);
            Assert.Contains("search", capturedJson);
            Assert.Contains("Search the web", capturedJson);
        }
    }

    #endregion
}
