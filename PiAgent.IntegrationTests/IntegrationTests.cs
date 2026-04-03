using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using PiAgent.Core;
using PiAgent.PiAi;
using PiAgent.Tools;
using Xunit;

namespace PiAgent.IntegrationTests
{
    public class IntegrationTestFixture : IDisposable
    {
        public ModelConfig Model { get; }

        public IntegrationTestFixture()
        {
            // Clear proxy env vars that may interfere with localhost connections
            Environment.SetEnvironmentVariable("ALL_PROXY", null);
            Environment.SetEnvironmentVariable("all_proxy", null);
            Environment.SetEnvironmentVariable("HTTP_PROXY", null);
            Environment.SetEnvironmentVariable("http_proxy", null);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", null);
            Environment.SetEnvironmentVariable("https_proxy", null);

            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            var baseUrl = config["LLM:BaseUrl"] ?? "http://localhost:8082/v1";
            var apiKey = Environment.GetEnvironmentVariable("PI_AGENT_API_KEY") 
                ?? config["LLM:ApiKey"] 
                ?? throw new InvalidOperationException("PI_AGENT_API_KEY not set");
            var model = config["LLM:Model"] ?? "glm-4.6";

            Model = new ModelConfig(model, baseUrl, apiKey)
            {
                MaxTokens = 1024,
                Temperature = 0.7
            };
        }

        public HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler();
            handler.UseProxy = false;
            return new HttpClient(handler);
        }

        public Agent CreateAgent(ILLMClient? client = null, string? systemPrompt = null)
        {
            var http = client != null ? null : CreateHttpClient();
            var llmClient = client ?? new OpenAIClient(http);
            var agent = new Agent(Model, llmClient);
            if (systemPrompt != null)
                agent.SystemPrompt = systemPrompt;
            return agent;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ALL_PROXY", null);
            Environment.SetEnvironmentVariable("all_proxy", null);
        }
    }

    public class BasicChatTests : IClassFixture<IntegrationTestFixture>
    {
        private readonly IntegrationTestFixture _fixture;

        public BasicChatTests(IntegrationTestFixture fixture) => _fixture = fixture;

        [Fact(Timeout = 30000)]
        public async Task BasicChat_ReturnsNonEmptyText()
        {
            var agent = _fixture.CreateAgent();
            var result = await agent.Prompt("你好");

            Assert.NotEmpty(result);
            var lastResponse = agent.GetLastResponse();
            Assert.False(string.IsNullOrWhiteSpace(lastResponse), "Expected non-empty response text");
        }
    }

    public class ToolCallingTests : IClassFixture<IntegrationTestFixture>
    {
        private readonly IntegrationTestFixture _fixture;

        public ToolCallingTests(IntegrationTestFixture fixture) => _fixture = fixture;

        [Fact(Timeout = 30000)]
        public async Task ToolCall_ToolIsExecutedAndResultReturned()
        {
            var toolCalled = false;
            var agent = _fixture.CreateAgent();
            agent.SystemPrompt = "你有一个工具叫 get_current_time。当被问到时间时，请使用它。用中文回复。";
            agent.DefineTool("get_current_time", "获取当前日期和时间", () =>
            {
                toolCalled = true;
                return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            });

            var result = await agent.Prompt("现在几点了？");

            Assert.True(toolCalled, "Tool should have been called");
            Assert.True(result.Count >= 3, $"Expected at least 3 messages, got {result.Count}");

            var lastResponse = agent.GetLastResponse();
            Assert.False(string.IsNullOrWhiteSpace(lastResponse));
        }
    }

    public class MultiTurnTests : IClassFixture<IntegrationTestFixture>
    {
        private readonly IntegrationTestFixture _fixture;

        public MultiTurnTests(IntegrationTestFixture fixture) => _fixture = fixture;

        [Fact(Timeout = 30000)]
        public async Task MultiTurn_ContextIsMaintained()
        {
            var agent = _fixture.CreateAgent();
            agent.SystemPrompt = "你是一个简短的助手，用一句话回答。";

            await agent.Prompt("我叫小明");
            var firstResponse = agent.GetLastResponse();
            Assert.False(string.IsNullOrWhiteSpace(firstResponse));

            await agent.Prompt("我叫什么名字？");
            var secondResponse = agent.GetLastResponse();
            Assert.False(string.IsNullOrWhiteSpace(secondResponse));
            Assert.Contains("小明", secondResponse);
        }
    }

    public class AgentLoopTests : IClassFixture<IntegrationTestFixture>
    {
        private readonly IntegrationTestFixture _fixture;

        public AgentLoopTests(IntegrationTestFixture fixture) => _fixture = fixture;

        [Fact(Timeout = 60000)]
        public async Task AgentLoop_ToolReturn_AgentContinuesThinking()
        {
            var toolCalled = false;
            var agent = _fixture.CreateAgent();
            agent.SystemPrompt = "你必须使用 calculator_double 工具来计算任何数学问题。用中文回复。";
            agent.DefineTool<int>("calculator_double", "将一个数字翻倍", (n) =>
            {
                toolCalled = true;
                return $"结果: {n * 2}";
            });

            await agent.Prompt("请用计算器工具算一下 21 的两倍是多少？");

            Assert.True(toolCalled, "Calculator tool should have been called");

            var hasToolResult = agent.Messages.Any(m => m is ToolResultMessage);
            Assert.True(hasToolResult, "Should have a ToolResultMessage in the conversation");

            var finalResponse = agent.GetLastResponse();
            Assert.False(string.IsNullOrWhiteSpace(finalResponse));
            Assert.Contains("42", finalResponse);
        }
    }
}
