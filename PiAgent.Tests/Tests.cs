using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;
using PiAgent.Core;
using PiAgent.PiAi;
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

    #region Message & Types Tests

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

        [Fact]
        public void ThinkingContent_HasCorrectType()
        {
            var tc = new ThinkingContent { Thinking = "Let me think..." };
            Assert.Equal("thinking", tc.Type);
            Assert.Equal("Let me think...", tc.Thinking);
        }

        [Fact]
        public void ThinkingContent_SupportsRedactedAndSignature()
        {
            var tc = new ThinkingContent
            {
                Thinking = "redacted",
                ThinkingSignature = "sig_123",
                Redacted = true
            };
            Assert.Equal("sig_123", tc.ThinkingSignature);
            Assert.True(tc.Redacted);
        }
    }

    #endregion

    #region Usage Tests

    public class UsageTests
    {
        [Fact]
        public void Zero_ReturnsZeroValues()
        {
            var u = Usage.Zero;
            Assert.Equal(0, u.InputTokens);
            Assert.Equal(0, u.OutputTokens);
            Assert.Equal(0, u.TotalTokens);
            Assert.Equal(0, u.CacheReadTokens);
            Assert.Equal(0, u.CacheWriteTokens);
        }

        [Fact]
        public void Constructor_SetsValues()
        {
            var u = new Usage(100, 200, 300);
            Assert.Equal(100, u.InputTokens);
            Assert.Equal(200, u.OutputTokens);
            Assert.Equal(300, u.TotalTokens);
        }

        [Fact]
        public void Cost_Fields()
        {
            var u = new Usage(100, 200, 300)
            {
                CacheReadTokens = 50,
                CacheWriteTokens = 30
            };
            u.Cost.Input = 0.01;
            u.Cost.Output = 0.02;
            u.Cost.Total = 0.03;
            Assert.Equal(0.01, u.Cost.Input);
            Assert.Equal(0.03, u.Cost.Total);
        }
    }

    #endregion

    #region ToolDefinition Tests

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

    #region ToolParam & ToolMetadata Tests

    public class ToolParamTests
    {
        [Fact]
        public void String_CreatesCorrectSchema()
        {
            var p = ToolParam.Str("name", "User name");
            Assert.Equal("name", p.Name);
            Assert.Equal("string", p.Type);
            Assert.Equal("User name", p.Description);
            Assert.True(p.Required);
        }

        [Fact]
        public void Int_CreatesCorrectSchema()
        {
            var p = ToolParam.Integer("age", "User age");
            Assert.Equal("integer", p.Type);
        }

        [Fact]
        public void Number_CreatesCorrectSchema()
        {
            var p = ToolParam.Float("score", "Score");
            Assert.Equal("number", p.Type);
        }

        [Fact]
        public void Bool_CreatesCorrectSchema()
        {
            var p = ToolParam.Boolean("active", "Is active");
            Assert.Equal("boolean", p.Type);
        }

        [Fact]
        public void Enum_CreatesSchemaWithEnumValues()
        {
            var p = ToolParam.Enum("color", new List<string> { "red", "green", "blue" }, "Color");
            Assert.Equal("string", p.Type);
            Assert.Equal(3, p.EnumValues!.Count);
            Assert.Contains("red", p.EnumValues);
        }

        [Fact]
        public void Array_CreatesSchemaWithItems()
        {
            var p = ToolParam.Array("tags", ToolParam.Str("tag"), "Tags");
            Assert.Equal("array", p.Type);
            Assert.NotNull(p.Items);
            Assert.Equal("string", p.Items!.Type);
        }

        [Fact]
        public void Object_CreatesSchemaWithProperties()
        {
            var p = ToolParam.Object("address", new List<ToolParam>
            {
                ToolParam.Str("city"),
                ToolParam.Str("country")
            }, "Address");
            Assert.Equal("object", p.Type);
            Assert.Equal(2, p.ObjectProperties!.Count);
        }

        [Fact]
        public void DefaultValue_IsSet()
        {
            var p = ToolParam.Str("lang", required: false);
            p.DefaultValue = "en";
            Assert.Equal("en", p.DefaultValue);
            Assert.False(p.Required);
        }

        [Fact]
        public void ToSchemaProperty_ConvertsCorrectly()
        {
            var p = ToolParam.Enum("size", new List<string> { "S", "M", "L" }, "Size", false);
            var schema = p.ToSchemaProperty();
            Assert.Equal("string", schema.Type);
            Assert.NotNull(schema.EnumValues);
            Assert.Equal(3, schema.EnumValues.Count);
            Assert.Equal("M", schema.EnumValues[1]);
        }

        [Fact]
        public void ToSchemaProperty_WithDefault()
        {
            var p = ToolParam.Integer("count", "Count", false);
            p.DefaultValue = 10;
            var schema = p.ToSchemaProperty();
            Assert.Equal(10, schema.DefaultValue);
        }
    }

    public class ToolMetadataTests
    {
        [Fact]
        public void ExtraParams_AreStored()
        {
            var meta = new ToolMetadata
            {
                ExtraParams = new List<ToolParam> { ToolParam.Str("extra_field", "Extra") }
            };
            Assert.Single(meta.ExtraParams);
            Assert.Equal("extra_field", meta.ExtraParams[0].Name);
        }

        [Fact]
        public void ExcludeParams_AreStored()
        {
            var meta = new ToolMetadata
            {
                ExcludeParams = new List<string> { "internal_field" }
            };
            Assert.Single(meta.ExcludeParams);
        }

        [Fact]
        public void DescriptionOverride_IsStored()
        {
            var meta = new ToolMetadata { DescriptionOverride = "New description" };
            Assert.Equal("New description", meta.DescriptionOverride);
        }
    }

    public class StrictModeTests
    {
        [Fact]
        public void On_EnabledIsTrue()
        {
            var s = StrictMode.On;
            Assert.True(s.Enabled);
        }

        [Fact]
        public void Off_EnabledIsFalse()
        {
            var s = StrictMode.Off;
            Assert.False(s.Enabled);
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

        [Fact]
        public async Task Define_ManualToolParam_ExecutesCorrectly()
        {
            var registry = new ToolRegistry();
            registry.Define(
                "greet",
                "Greet a user",
                new List<ToolParam>
                {
                    ToolParam.Str("name", "User name"),
                    ToolParam.Enum("style", new List<string> { "formal", "casual" }, "Greeting style", false)
                },
                (args, ct) =>
                {
                    var name = args.TryGetValue("name", out var n) ? n?.ToString() : "stranger";
                    var style = args.TryGetValue("style", out var s) ? s?.ToString() : "casual";
                    var greeting = style == "formal" ? $"Good day, {name}" : $"Hey, {name}!";
                    return Task.FromResult(greeting);
                });

            var tool = registry.Find("greet");
            Assert.NotNull(tool);

            // Check schema
            Assert.True(tool.Definition.Parameters.Properties.ContainsKey("name"));
            Assert.True(tool.Definition.Parameters.Properties.ContainsKey("style"));
            Assert.Contains("name", tool.Definition.Parameters.Required);
            Assert.DoesNotContain("style", tool.Definition.Parameters.Required);

            // Execute
            var result = await tool.Execute(
                new Dictionary<string, object?> { ["name"] = "Alice" },
                CancellationToken.None);
            Assert.Equal("Hey, Alice!", result);
        }

        [Fact]
        public async Task Define_ManualToolParam_StrictMode_SetsAdditionalProperties()
        {
            var registry = new ToolRegistry();
            registry.Define(
                "strict_tool",
                "A strict tool",
                new List<ToolParam> { ToolParam.Str("input") },
                (args, ct) => Task.FromResult("ok"),
                strict: true);

            var tool = registry.Find("strict_tool");
            Assert.NotNull(tool);
            Assert.False(tool.Definition.Parameters.AdditionalProperties);
        }

        [Fact]
        public async Task Define_EnumParam_GeneratesEnumSchema()
        {
            var registry = new ToolRegistry();
            registry.Define(
                "choose",
                "Choose an option",
                new List<ToolParam> { ToolParam.Enum("option", new List<string> { "A", "B", "C" }, "Option") },
                (args, ct) => Task.FromResult("done"));

            var tool = registry.Find("choose");
            var schema = tool!.Definition.Parameters.Properties["option"];
            Assert.NotNull(schema.EnumValues);
            Assert.Equal(new List<string> { "A", "B", "C" }, schema.EnumValues);
        }

        [Fact]
        public async Task DefineWithMetadata_ExcludeParams()
        {
            // Use a method with CancellationToken to test exclusion
            var method = typeof(ToolRegistryTests).GetMethod("SampleMethodWithExclusion",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var registry = new ToolRegistry();
            registry.DefineFromMethod("test", "Test", method!, null,
                new ToolMetadata { ExcludeParams = new List<string> { "secret" } });

            var tool = registry.Find("test");
            Assert.NotNull(tool);
            Assert.False(tool.Definition.Parameters.Properties.ContainsKey("secret"));
            Assert.True(tool.Definition.Parameters.Properties.ContainsKey("visible"));
        }

        // Helper for metadata test
        private static async Task<string> SampleMethodWithExclusion(string visible, string secret, CancellationToken ct)
        {
            await Task.Yield();
            return $"{visible}:{secret}";
        }
    }

    #endregion

    #region StreamParser Tests

    public class StreamParserTests
    {
        [Fact]
        public void ParseAll_TextOnly_ProducesCorrectEvents()
        {
            var sse = @"data: {""choices"":[{""delta"":{""role"":""assistant"",""content"":""Hello""},""index"":0}]}

data: {""choices"":[{""delta"":{""content"":"" World""},""index"":0}]}

data: [DONE]";

            var parser = new StreamParser();
            var events = parser.ParseAll(sse);

            Assert.Contains(events, e => e is TextDeltaEvent td && td.Delta == "Hello");
            Assert.Contains(events, e => e is TextDeltaEvent td && td.Delta == " World");
            Assert.Contains(events, e => e is StreamDoneEvent);
            Assert.Contains(events, e => e is TextEndEvent te && te.Content == "Hello World");

            var msg = parser.GetMessage();
            Assert.Equal("Hello World", msg.GetText());
            Assert.Equal("stop", msg.StopReason);
        }

        [Fact]
        public void ParseAll_ToolCalls_ProducesToolCallEvents()
        {
            var sse = @"data: {""choices"":[{""delta"":{""tool_calls"":[{""index"":0,""id"":""call_1"",""type"":""function"",""function"":{""name"":""weather"",""arguments"":""""}}]},""index"":0,""finish_reason"":null}]}

data: {""choices"":[{""delta"":{""tool_calls"":[{""index"":0,""function"":{""arguments"":""{\""city\"": \""Tokyo\""}""}}]},""index"":0,""finish_reason"":""tool_calls""}]}

data: [DONE]";

            var parser = new StreamParser();
            var events = parser.ParseAll(sse);

            Assert.Contains(events, e => e is ToolCallStartEvent);
            Assert.Contains(events, e => e is ToolCallDeltaEvent);
            Assert.Contains(events, e => e is ToolCallEndEvent tce && tce.ToolCall.Name == "weather");

            var msg = parser.GetMessage();
            var calls = msg.GetToolCalls();
            Assert.Single(calls);
            Assert.Equal("call_1", calls[0].Id);
            Assert.Equal("Tokyo", calls[0].Arguments["city"]?.ToString());
            Assert.Equal("toolUse", msg.StopReason);
        }

        [Fact]
        public void ParseAll_ReasoningContent_ProducesThinkingEvents()
        {
            var sse = @"data: {""choices"":[{""delta"":{""reasoning_content"":""Let me think""},""index"":0}]}

data: {""choices"":[{""delta"":{""reasoning_content"":"" about this...""},""index"":0}]}

data: {""choices"":[{""delta"":{""content"":""The answer is 42""},""index"":0}]}

data: [DONE]";

            var parser = new StreamParser();
            var events = parser.ParseAll(sse);

            Assert.Contains(events, e => e is ThinkingDeltaEvent td && td.Delta == "Let me think");
            Assert.Contains(events, e => e is ThinkingEndEvent te && te.Content == "Let me think about this...");
            Assert.Contains(events, e => e is TextDeltaEvent td && td.Delta == "The answer is 42");

            var msg = parser.GetMessage();
            Assert.Contains(msg.Content, c => c is ThinkingContent);
            Assert.Equal("The answer is 42", msg.GetText());
        }

        [Fact]
        public void ParseLine_IgnoresNonDataLines()
        {
            var parser = new StreamParser();
            Assert.Null(parser.ParseLine(": comment"));
            Assert.Null(parser.ParseLine(""));
        }

        [Fact]
        public void ParseAll_WithUsage_ParsesUsage()
        {
            var sse = @"data: {""choices"":[{""delta"":{""content"":""hi""},""index"":0}],""usage"":{""prompt_tokens"":10,""completion_tokens"":5,""total_tokens"":15}}

data: [DONE]";

            var parser = new StreamParser();
            parser.ParseAll(sse);

            var msg = parser.GetMessage();
            Assert.Equal(10, msg.Usage.InputTokens);
            Assert.Equal(5, msg.Usage.OutputTokens);
        }
    }

    #endregion

    #region ApiRegistry Tests

    public class ApiRegistryTests
    {
        [Fact]
        public void RegisterAndGet_Provider()
        {
            ApiRegistry.Clear();
            var provider = new MockApiProvider("test-api");
            ApiRegistry.RegisterProvider(provider);

            var found = ApiRegistry.GetProvider("test-api");
            Assert.NotNull(found);
            Assert.Equal("test-api", found!.Api);
        }

        [Fact]
        public void Unregister_Provider()
        {
            ApiRegistry.Clear();
            ApiRegistry.RegisterProvider(new MockApiProvider("temp"));
            ApiRegistry.UnregisterProvider("temp");
            Assert.Null(ApiRegistry.GetProvider("temp"));
        }

        [Fact]
        public void GetAllProviders_ReturnsAll()
        {
            ApiRegistry.Clear();
            ApiRegistry.RegisterProvider(new MockApiProvider("a"));
            ApiRegistry.RegisterProvider(new MockApiProvider("b"));
            Assert.Equal(2, ApiRegistry.GetAllProviders().Count);
        }

        [Fact]
        public void RegisterAndGet_Model()
        {
            ApiRegistry.Clear();
            var model = new ModelConfig("gpt-4", "https://api.openai.com/v1", "key");
            ApiRegistry.RegisterModel(model);

            var found = ApiRegistry.GetModel("gpt-4");
            Assert.NotNull(found);
            Assert.Equal("gpt-4", found!.Id);
        }

        [Fact]
        public void Clear_ClearsEverything()
        {
            ApiRegistry.Clear();
            ApiRegistry.RegisterProvider(new MockApiProvider("x"));
            ApiRegistry.RegisterModel(new ModelConfig("m", "url", "key"));
            ApiRegistry.Clear();
            Assert.Empty(ApiRegistry.GetAllProviders());
            Assert.Empty(ApiRegistry.GetAllModels());
        }
    }

    public class MockApiProvider : IApiProvider
    {
        public string Api { get; }
        public MockApiProvider(string api) { Api = api; }
        public Task<AssistantMessage> Stream(ModelConfig model, AgentContext context, CancellationToken ct = default)
            => Task.FromResult(new AssistantMessage());
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
            var client = MockLLM.Sequence(
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new ToolCall { Id = "tc1", Name = "weather", Arguments = new() { ["value"] = "Beijing" } } },
                    StopReason = "toolUse",
                    Usage = new Usage(10, 5, 15)
                }),
                (ctx, model) =>
                {
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

            Assert.Equal(4, result.Count);
            Assert.IsType<UserMessage>(result[0]);
            Assert.IsType<AssistantMessage>(result[1]);
            Assert.IsType<ToolResultMessage>(result[2]);
            Assert.IsType<AssistantMessage>(result[3]);
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
                (ctx, model) => throw new OperationCanceledException()
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
            var client = MockLLM.Sequence(
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

            var toolResult = Assert.IsType<ToolResultMessage>(result[2]);
            Assert.True(toolResult.IsError);
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

        [Fact]
        public async Task Run_BeforeToolCall_BlocksExecution()
        {
            var tools = new ToolRegistry();
            tools.Define("dangerous", "Dangerous op", () => "should not run");

            var client = MockLLM.Sequence(
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new ToolCall { Id = "tc1", Name = "dangerous", Arguments = new() } },
                    StopReason = "toolUse"
                }),
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new TextContent { Text = "blocked" } },
                    StopReason = "stop"
                })
            );

            var config = new AgentLoopConfig
            {
                Model = TestModel,
                BeforeToolCall = (ctx, ct) => Task.FromResult(new BeforeToolCallResult { Block = true, Reason = "Not allowed" })
            };

            var loop = new AgentLoop(client, TestModel);
            var context = new AgentContext();
            var result = await loop.Run(context,
                new List<Message> { new UserMessage("test") },
                tools.GetAll(), null, default, 10, config);

            var toolResult = Assert.IsType<ToolResultMessage>(result[2]);
            Assert.True(toolResult.IsError);
            Assert.Contains("Not allowed", toolResult.Content[0] is TextContent tc ? tc.Text : "");
        }

        [Fact]
        public async Task Run_AfterToolCall_OverridesResult()
        {
            var tools = new ToolRegistry();
            tools.Define("echo", "Echo", () => "original");

            var client = MockLLM.Sequence(
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new ToolCall { Id = "tc1", Name = "echo", Arguments = new() } },
                    StopReason = "toolUse"
                }),
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new TextContent { Text = "modified" } },
                    StopReason = "stop"
                })
            );

            var config = new AgentLoopConfig
            {
                Model = TestModel,
                AfterToolCall = (ctx, ct) => Task.FromResult(new AfterToolCallResult
                {
                    Content = new List<object> { new TextContent { Text = "overridden!" } }
                })
            };

            var loop = new AgentLoop(client, TestModel);
            var context = new AgentContext();
            var result = await loop.Run(context,
                new List<Message> { new UserMessage("test") },
                tools.GetAll(), null, default, 10, config);

            var toolResult = Assert.IsType<ToolResultMessage>(result[2]);
            Assert.Equal("overridden!", toolResult.Content[0] is TextContent tc ? tc.Text : "");
        }

        [Fact]
        public async Task Run_TransformContext_ModifiesMessages()
        {
            bool transformCalled = false;
            var client = MockLLM.TextResponse("ok");

            var config = new AgentLoopConfig
            {
                Model = TestModel,
                TransformContext = (messages, ct) =>
                {
                    transformCalled = true;
                    return Task.FromResult(messages);
                }
            };

            var loop = new AgentLoop(client, TestModel);
            var context = new AgentContext();
            await loop.Run(context,
                new List<Message> { new UserMessage("test") },
                null, null, default, 10, config);

            Assert.True(transformCalled);
        }

        [Fact]
        public async Task Run_GetApiKey_ResolvesKey()
        {
            string? resolvedKey = null;
            var client = new MockLLMClient(async (ctx, model, ct) =>
            {
                resolvedKey = model.ApiKey;
                return new AssistantMessage { Content = { new TextContent { Text = "ok" } }, StopReason = "stop" };
            });

            var config = new AgentLoopConfig
            {
                Model = TestModel,
                GetApiKey = (provider) => Task.FromResult<string?>("dynamic-key-123")
            };

            var loop = new AgentLoop(client, TestModel);
            var context = new AgentContext();
            await loop.Run(context,
                new List<Message> { new UserMessage("test") },
                null, null, default, 10, config);

            Assert.Equal("dynamic-key-123", resolvedKey);
        }

        [Fact]
        public async Task Run_SteeringMessages_InjectMidRun()
        {
            int steeringCallCount = 0;
            var client = MockLLM.Sequence(
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new ToolCall { Id = "tc1", Name = "tool1", Arguments = new() } },
                    StopReason = "toolUse"
                }),
                (ctx, model) =>
                {
                    // After first tool round, steering should have injected a message
                    return Task.FromResult(new AssistantMessage
                    {
                        Content = { new TextContent { Text = "done" } },
                        StopReason = "stop"
                    });
                }
            );

            var tools = new ToolRegistry();
            tools.Define("tool1", "Tool 1", () => "result1");

            var config = new AgentLoopConfig
            {
                Model = TestModel,
                GetSteeringMessages = () =>
                {
                    steeringCallCount++;
                    return Task.FromResult(new List<Message>
                    {
                        new UserMessage("steering: be concise")
                    });
                }
            };

            var loop = new AgentLoop(client, TestModel);
            var context = new AgentContext();
            var result = await loop.Run(context,
                new List<Message> { new UserMessage("test") },
                tools.GetAll(), null, default, 10, config);

            Assert.True(steeringCallCount > 0);
        }

        [Fact]
        public async Task Run_FollowUpMessages_ContinuesAfterStop()
        {
            int followUpCallCount = 0;
            var client = MockLLM.TextResponse("initial");

            var config = new AgentLoopConfig
            {
                Model = TestModel,
                GetFollowUpMessages = () =>
                {
                    followUpCallCount++;
                    if (followUpCallCount == 1)
                    {
                        return Task.FromResult(new List<Message>
                        {
                            new UserMessage("follow up question")
                        });
                    }
                    return Task.FromResult(new List<Message>());
                }
            };

            var loop = new AgentLoop(client, TestModel);
            var context = new AgentContext();
            var result = await loop.Run(context,
                new List<Message> { new UserMessage("test") },
                null, null, default, 10, config);

            // Should have run at least 2 turns (initial + follow-up)
            Assert.True(followUpCallCount >= 1);
        }

        [Fact]
        public async Task Run_ParallelToolExecution_ExecutesConcurrently()
        {
            var executionOrder = new System.Collections.Concurrent.ConcurrentBag<string>();
            var client = MockLLM.Sequence(
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content =
                    {
                        new ToolCall { Id = "tc1", Name = "tool_a", Arguments = new() },
                        new ToolCall { Id = "tc2", Name = "tool_b", Arguments = new() }
                    },
                    StopReason = "toolUse"
                }),
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new TextContent { Text = "done" } },
                    StopReason = "stop"
                })
            );

            var tools = new ToolRegistry();
            tools.Define("tool_a", "Tool A", async () =>
            {
                await Task.Delay(50);
                executionOrder.Add("a");
                return "a";
            });
            tools.Define("tool_b", "Tool B", async () =>
            {
                await Task.Delay(50);
                executionOrder.Add("b");
                return "b";
            });

            var config = new AgentLoopConfig
            {
                Model = TestModel,
                ToolExecution = ToolExecutionMode.Parallel
            };

            var loop = new AgentLoop(client, TestModel);
            var context = new AgentContext();
            await loop.Run(context,
                new List<Message> { new UserMessage("test") },
                tools.GetAll(), null, default, 10, config);

            Assert.Equal(2, executionOrder.Count);
            Assert.Contains("a", executionOrder);
            Assert.Contains("b", executionOrder);
        }

        [Fact]
        public async Task Run_SequentialToolExecution_ExecutesInOrder()
        {
            var executionOrder = new List<string>();
            var client = MockLLM.Sequence(
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content =
                    {
                        new ToolCall { Id = "tc1", Name = "tool_a", Arguments = new() },
                        new ToolCall { Id = "tc2", Name = "tool_b", Arguments = new() }
                    },
                    StopReason = "toolUse"
                }),
                (ctx, model) => Task.FromResult(new AssistantMessage
                {
                    Content = { new TextContent { Text = "done" } },
                    StopReason = "stop"
                })
            );

            var tools = new ToolRegistry();
            tools.Define("tool_a", "Tool A", async () =>
            {
                executionOrder.Add("a");
                return "a";
            });
            tools.Define("tool_b", "Tool B", async () =>
            {
                executionOrder.Add("b");
                return "b";
            });

            var config = new AgentLoopConfig
            {
                Model = TestModel,
                ToolExecution = ToolExecutionMode.Sequential
            };

            var loop = new AgentLoop(client, TestModel);
            var context = new AgentContext();
            await loop.Run(context,
                new List<Message> { new UserMessage("test") },
                tools.GetAll(), null, default, 10, config);

            Assert.Equal(new[] { "a", "b" }, executionOrder);
        }

        [Fact]
        public async Task Run_ConvertToLlm_TransformsMessages()
        {
            bool convertCalled = false;
            var client = new MockLLMClient(async (ctx, model, ct) =>
            {
                return new AssistantMessage { Content = { new TextContent { Text = "ok" } }, StopReason = "stop" };
            });

            var config = new AgentLoopConfig
            {
                Model = TestModel,
                ConvertToLlm = (messages) =>
                {
                    convertCalled = true;
                    return Task.FromResult(messages);
                }
            };

            var loop = new AgentLoop(client, TestModel);
            var context = new AgentContext();
            await loop.Run(context,
                new List<Message> { new UserMessage("test") },
                null, null, default, 10, config);

            Assert.True(convertCalled);
        }
    }

    #endregion

    #region AgentState Tests

    public class AgentStateTests
    {
        [Fact]
        public void DefaultValues()
        {
            var state = new AgentState();
            Assert.False(state.IsStreaming);
            Assert.Null(state.StreamingMessage);
            Assert.Empty(state.PendingToolCalls);
            Assert.Null(state.ErrorMessage);
            Assert.Equal(ThinkingLevel.Off, state.ThinkingLevel);
        }

        [Fact]
        public void Tools_CopiesOnAssign()
        {
            var state = new AgentState();
            var tools = new List<AgentTool>();
            state.Tools = tools;
            tools.Add(null!); // Modify original
            Assert.Empty(state.Tools); // Copy was made
        }

        [Fact]
        public void Messages_CopiesOnAssign()
        {
            var state = new AgentState();
            var msgs = new List<Message> { new UserMessage("test") };
            state.Messages = msgs;
            msgs.Add(new UserMessage("another"));
            Assert.Single(state.Messages);
        }

        [Fact]
        public void PendingToolCalls_TracksExecution()
        {
            var state = new AgentState();
            state.PendingToolCalls.Add("tc1");
            state.PendingToolCalls.Add("tc2");
            Assert.Equal(2, state.PendingToolCalls.Count);
            Assert.Contains("tc1", state.PendingToolCalls);
        }
    }

    #endregion

    #region Model & Compat Tests

    public class ModelConfigTests
    {
        [Fact]
        public void DefaultValues()
        {
            var model = new ModelConfig();
            Assert.Equal("", model.Id);
            Assert.Equal("openai", model.Provider);
            Assert.Equal("openai-completions", model.Api);
            Assert.Equal(4096, model.MaxTokens);
            Assert.Equal(128000, model.ContextWindow);
            Assert.False(model.Reasoning);
        }

        [Fact]
        public void ConvenienceConstructor()
        {
            var model = new ModelConfig("gpt-4", "https://api.openai.com/v1", "sk-test");
            Assert.Equal("gpt-4", model.Id);
            Assert.Equal("gpt-4", model.Name);
        }

        [Fact]
        public void OpenAICompletionsCompat_Defaults()
        {
            var compat = new OpenAICompletionsCompat();
            Assert.True(compat.SupportsUsageInStreaming);
            Assert.True(compat.SupportsStrictMode);
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

            Assert.Equal(2, agent.Messages.Count);
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
