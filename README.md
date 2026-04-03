# pi-agent.cs

极简 C# AI Agent 框架，直接复刻自 [badlogic/pi-mono](https://github.com/badlogic/pi-mono) 的 `@mariozechner/pi-ai` 和 `@mariozechner/pi-agent-core` 两个模块，用 C# 重新实现。

面向游戏场景设计，零 Unity 依赖，纯 `System.Net.Http` + `System.Text.Json` + `System.Reflection`。

## 特性

- **Agent 运行时** — LLM 调用 → tool calling → 结果回传 → 循环
- **C# Delegate → Tool** — 用反射自动生成 JSON Schema，一行定义工具
- **流式响应** — SSE 逐行解析，`IAsyncEnumerable` 事件流
- **并行/顺序 Tool 执行** — 支持 sequential 和 parallel 模式
- **Steering/FollowUp** — 运行中注入消息引导 agent
- **Tool Hooks** — beforeToolCall / afterToolCall 拦截器
- **上下文管理** — token 计数 + 自动裁剪
- **多 Provider** — OpenAI 兼容格式覆盖 90%+ 的 LLM 服务
- **netstandard2.1** — 兼容 Unity 2021+

## 快速开始

```csharp
using PiAgent.Core;
using PiAgent.PiAi;

// 1. 创建 Agent
var model = new ModelConfig("gpt-4o", "https://api.openai.com/v1", "your-api-key");
var agent = new Agent(model);

// 2. 设置 System Prompt
agent.SystemPrompt = "You are a helpful game narrator.";

// 3. 定义工具（C# delegate → JSON Schema）
agent.DefineTool("look_around", "查看周围环境", () => GetSurroundings());
agent.DefineTool("move_to", "移动到指定位置", (string direction) => Move(direction));
agent.DefineTool("check_inventory", "查看背包物品", () => inventory.GetItems());
agent.DefineTool("attack", "攻击目标", (string target, int power) => Combat(target, power));

// 4. 发送消息
var messages = await agent.Prompt("我往北走");

// 5. 继续对话
var more = await agent.Continue();
```

## 安装

### Unity (UPM)
Window → Package Manager → + → Add package from git URL
```
https://github.com/JupiterTheWarlock/pi-agent.cs.git
```

### NuGet
```
dotnet add package PiAgent
```

### 直接拷贝
复制 `Runtime/` 目录到你的项目中即可。

## 项目结构

```
Runtime/
├── PiAi/                    # namespace: PiAgent.PiAi
│   ├── Types.cs             # 消息类型、Tool 定义、Context、Usage
│   ├── ILLMClient.cs        # LLM 调用接口
│   ├── OpenAIClient.cs      # OpenAI 兼容实现
│   ├── StreamParser.cs      # SSE 流式响应解析
│   ├── ApiRegistry.cs       # Provider 注册
│   └── Models.cs            # Model 定义与兼容配置
│
├── PiAgentCore/             # namespace: PiAgent.Core
│   ├── Agent.cs             # 主类：状态管理、prompt/continue/reset
│   ├── AgentLoop.cs         # 核心循环：LLM → tool_calls → execute → feed back
│   ├── AgentEvent.cs        # 事件类型（start/end/update/tool_execution）
│   └── AgentState.cs        # Agent 状态快照
│
└── Tools/                   # namespace: PiAgent.Tools
    └── ToolRegistry.cs      # Delegate → JSON Schema 反射映射
```

对应关系：
- **PiAi/** → `@mariozechner/pi-ai`（TypeScript）
- **PiAgentCore/** → `@mariozechner/pi-agent-core`（TypeScript）
- **Tools/** → 独有增强（灵感来自 [LLMTornado](https://github.com/lofcz/LLMTornado)）

## Agent 循环

```
用户消息 → LLM 调用 → 有 tool_calls？
  ├─ 否 → 返回 assistant 消息（stop/length）
  └─ 是 → 执行 tools → 结果回传 → 再调 LLM（循环）
         ├─ sequential: 逐个执行
         └─ parallel:    并发执行
```

## Tool 定义

### 基础用法

```csharp
// 无参数
agent.DefineTool("look_around", "查看周围环境", () => GetSurroundings());

// 有参数（自动推断类型）
agent.DefineTool("move_to", "移动到指定位置", (string direction) => Move(direction));

// 多参数
agent.DefineTool("attack", "攻击目标", (string target, int power) => Combat(target, power));
```

### 高级特性

```csharp
// 枚举参数
agent.DefineTool("set_difficulty", "设置难度",
    (string level) => SetDifficulty(level),
    new ToolMetadata
    {
        EnumValues = new Dictionary<string, string[]>
        {
            ["level"] = new[] { "easy", "normal", "hard", "nightmare" }
        }
    });

// 参数默认值
agent.DefineTool("search", "搜索物品",
    (string query, int limit) => Search(query, limit),
    new ToolMetadata
    {
        Defaults = new Dictionary<string, object>
        {
            ["limit"] = 10
        }
    });

// 手动定义参数（不依赖反射）
var tool = new ToolDefinition
{
    Name = "craft",
    Description = "合成物品",
    Parameters = new JsonSchema
    {
        Type = "object",
        Properties = new List<JsonSchemaProperty>
        {
            new() { Name = "recipe", Type = "string", Description = "配方名称", Required = true },
            new() { Name = "count", Type = "integer", Description = "数量", Default = "1" }
        }
    }
};
agent.AddTool(tool);

// Strict 模式（OpenAI 结构化输出）
agent.DefineTool("open_door", "打开门", (string doorId) => OpenDoor(doorId),
    new ToolMetadata { Strict = true });
```

### Tool Hooks

```csharp
// 执行前拦截（可用于权限检查、参数修改）
agent.BeforeToolCall = async (context) =>
{
    if (context.ToolName == "attack" && context.Args is JObject args)
    {
        var target = args["target"]?.ToString();
        if (target == "ally") return new BeforeToolCallResult { Block = true, Reason = "不能攻击友军" };
    }
    return null;
};

// 执行后拦截（可用于结果修改、日志记录）
agent.AfterToolCall = async (context) =>
{
    if (context.ToolName == "look_around")
    {
        // 在结果中追加时间信息
        return new AfterToolCallResult
        {
            Content = new[] { new TextContent { Text = context.ResultContent + "\n[时间: " + DateTime.Now + "]" } }
        };
    }
    return null;
};
```

## 流式响应

```csharp
// 使用事件订阅
agent.Subscribe((evt) =>
{
    switch (evt)
    {
        case TextDeltaEvent textDelta:
            Console.Write(textDelta.Delta); // 逐字输出
            break;
        case ToolCallStartEvent toolStart:
            Console.WriteLine($"\n[调用工具: {toolStart.ToolName}]");
            break;
        case ToolExecutionEndEvent toolEnd:
            Console.WriteLine($"[工具完成: {toolEnd.IsError ? "失败" : "成功"}]");
            break;
    }
});

await agent.Prompt("给我讲个故事");
```

## Steering & FollowUp

```csharp
// 运行中注入消息引导 agent（在当前 turn 结束后插入）
agent.Steer(new UserMessage { Content = "等等，先检查一下陷阱" });

// agent 停止后追加消息（触发新一轮对话）
agent.FollowUp(new UserMessage { Content = "现在继续探索" });
```

## 上下文管理

```csharp
// 自定义上下文转换（token 裁剪、注入世界状态等）
agent.TransformContext = (messages) =>
{
    // 注入当前游戏状态
    var worldState = new UserMessage
    {
        Content = $"[系统: 当前位置={player.Position}, 生命值={player.HP}]"
    };
    
    // 裁剪过长历史
    var maxMessages = 50;
    if (messages.Count > maxMessages)
        messages = messages.Skip(messages.Count - maxMessages).ToList();
    
    return Task.FromResult(messages);
};
```

## 构建与测试

```bash
# 构建
dotnet build PiAgent/PiAgent.csproj

# 单元测试（78 个）
dotnet test PiAgent.Tests/PiAgent.Tests.csproj

# 集成测试（需要 API key）
PI_AGENT_API_KEY="your-key" dotnet test PiAgent.IntegrationTests/PiAgent.IntegrationTests.csproj
```

## 致谢

本项目直接复刻自 [badlogic/pi-mono](https://github.com/badlogic/pi-mono) 的两个核心模块：

- `@mariozechner/pi-ai` → `PiAgent.PiAi`
- `@mariozechner/pi-agent-core` → `PiAgent.Core`

感谢 Mario Zechner 的优秀设计和开源贡献。Tool 系统的 C# delegate 映射思路参考了 [lofcz/LLMTornado](https://github.com/lofcz/LLMTornado)。

## License

MIT
