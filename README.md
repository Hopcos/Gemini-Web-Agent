# GeminiWebAgent 🚀

> **基于 .NET 10 技术栈的 Google Gemini Web (`gemini.google.com`) 反向代理与 OpenAI 规范兼容网关**

GeminiWebAgent 是一个专为 AI Agent（Cursor、Dify、LangChain、Cline / Roo Code、Continue、NextChat、Open-WebUI 等）打造的高性能本地/私有网关。它通过深度解析 Google 官方 Web 网页版端点（`https://gemini.google.com`），无缝封装底层的 `batchexecute` RPC 协议，将其转化为完全符合 **OpenAI 官方规范** 的 API 接口：
- `POST /v1/chat/completions` (支持非流式与流式 SSE)
- `GET /v1/models` (列出可用模型列表)
- `GET /v1/health` (服务健康检查)

项目结构、代码规范与工程质量完全对标并严格参考 [`E:\Personal\GeminiAgent`](file:///E:/Personal/GeminiAgent)。

---

## 🌟 核心特性与技术架构

### 1. 🔄 Gemini Web 协议自适应解析 (batchexecute Protocol)
- **动态会话参数嗅探 (`GeminiWebSessionService`)**：Gemini Web 前端依赖动态的后端构建标识（`bl` / `cfb2h`）以及 anti-XSRF 校验凭证（`at` / `SNlM0e`）。GeminiWebAgent 在后台自动从首页 HTML 中高效抽取并缓存参数（30 分钟自适应巡检），彻底规避传统硬编码导致的 HTTP 400/405 失效问题。
- **深层 RPC 报文编解码**：封装 Google `otAQ7b`（`StreamGenerate`）RPC 调用，在 URL-encoded `f.req` 结构中完成提示词与模型标识（Position `[79]`）的注入与序列化。
- **全自动 XSRF 防御剥离**：毫秒级移除响应体起始的 `)]}'` 安全防注入标记，递归解码多维嵌套数组结构，精确提取生成的文本分块。

```mermaid
flowchart TD
    A["客户端请求 (OpenAI 格式)"] --> B{"鉴权中间件 (ApiKeyAuthMiddleware)"}
    B -- 验证通过 --> C["GeminiWebChatService 转译请求"]
    C --> D{"GeminiWebSessionService 检查会话参数"}
    D -- 参数过期或未初始化 --> E["请求 gemini.google.com 提取 bl (buildLabel) 与 at (SNlM0e)"]
    E --> F["缓存会话状态 (30分钟保活)"]
    D -- 会话有效 --> G["构建 batchexecute RPC 报文 (f.req, otAQ7b)"]
    F --> G
    G --> H["发送 POST 至 /_/BardChatUi/.../StreamGenerate"]
    H --> I{"是否流式 (stream=true)?"}
    I -- 是 --> J["逐行消费分块，剥离 )]}' 前缀并转为 chat.completion.chunk SSE"]
    I -- 否 --> K["内存高效聚合全文并转为 chat.completion 响应"]
    J --> L["返回客户端 (OpenAI 兼容)"]
    K --> L
```

### 2. 🔑 灵活的多源认证与自动滚动续期 (Cookie Auto-Renewal)
为解决 Linux / Docker 等无头环境下难以人工反复按 F12 复制 Cookie 的痛点，网关设计了内置开关与自动保活机制：
- **开关配置 (`CookieSource`)**：
  - `Manual` *(默认)*：保留原有手工模式，可通过配置文件预设或客户端在 `Authorization: Bearer <cookies>` 头中透传。
  - `File`：从指定文件（如 `cookies.json`）读取，支持 Docker volume 挂载与热重载。
- **Google 滚动 Token 拦截自动回写 (`EnableAutoRenewal = true`)**：
  - Google Web 体系中 `__Secure-1PSID` 为长效主令牌（寿命 1~2 年），而 `__Secure-1PSIDTS` 为滚动安全时间戳（约 12~24 小时过期）。
  - 网关内置 `GeminiWebCookieManager` 自动拦截 Google 响应中的 `Set-Cookie`，捕获最新 `__Secure-1PSIDTS` 并自动写回磁盘文件（`cookies.json`），实现**永续保活**。
- **后台主动巡检保活 (`CookieKeepAliveBackgroundService`)**：
  - 后台每 120 分钟轻量 ping 访问一次 Gemini Web，主动触发 Google 滚动签发最新 Cookies，实现 7x24 小时无人值守运行。

### 3. ⚡ 纯粹、现代、零冗余的 .NET 10 技术栈
- **.NET 10 Minimal APIs**：零控制器模板，高并发微秒级路由分发。
- **Native AOT 友好与纯官方库**：无需依赖任何第三方非官方封装库，100% 采用官方 `System.Text.Json`、`IHttpClientFactory` 及现代 C# 特性（Primary Constructors、Collection Expressions `[]`、Generated Regex 等）。
- **零警告 (0 Warnings) 严苛构建**：Nullable Reference Types 全面启用，代码健壮规范。

### 4. 🛠️ OpenAI Tool Calling / Function Calling 双向转译 (Agent 编程助手原生支持)

> [!IMPORTANT]
> **排坑经验沉淀：解决 Coding Agent 接入时的“模型拒绝修改本地文件”痛点**
> 
> 在将普通 Web 代理接入本地编程 Agent（如 Cursor、Cline、Roo Code、Aider、LiveConversation Agent 等）时，经常会遇到以下现象：
> 外部 Agent 期望模型自动修改代码，但模型却输出长篇大论的建议，被催促“直接修改文件”时甚至道歉拒绝：
> *“作为纯文本交互的 AI 语言模型，我并没有直接挂载或访问您本地计算机物理文件系统的权限，因此我无法在您的电脑上直接执行文件覆盖或修改。请手动复制上述代码到 VS Code 中替换……”*

#### 💡 深度机理与排坑经验
1. **云端模型并不物理读写本地磁盘**：任何商业 API（如 Claude 3.5 Sonnet、GPT-4o、Gemini 官方 API）在配合 Agent 修改代码时，模型本身**从来不会**直接访问用户的物理文件系统。其背后的本质是标准 **OpenAI Function Calling（工具调用）协议**——客户端在请求中下发 `tools: [...]`（如 `read_file`、`write_to_file` 等本地工具定义），模型依据逻辑计算在响应中返回结构化的 `tool_calls` 参数块（`finish_reason: "tool_calls"`），再由**运行在用户电脑上的 Agent 宿主程序**完成磁盘 IO。
2. **传统 Web 代理的技术盲区**：
   - **`tools` 契约静默丢弃**：绝大多数反代未定义 `tools`/`functions` 接收属性，导致客户端发送的工具声明在 JSON 反序列化时被全部丢失；
   - **多轮会话断裂**：未对 `role: "tool"`（工具执行结果）及 `tool_call_id` 进行适配，Agent 执行完工具后的回执请求直接报错或失真；
   - **纯文本人设误导**：若网关在提示词中固定注入纯文本闲聊身份，模型在感知不到任何工具的情况下面对用户“直接修改本地文件”的要求，只能给出严谨但无用的免责道歉；
   - **负面暗示陷阱**：如果在系统提示词中写入“禁止回答无法访问文件”，反而会触发 LLM 负向提示词聚焦效应（Pink Elephant Effect），诱发模型反复复读拒绝语句。

#### 🚀 GeminiWebAgent 的双向转译解决方案
GeminiWebAgent 在底层原生打通了 OpenAI Tool Calling 规范：
- **全契约双向透传**：支持 OpenAI 标准 `tools`、`functions`、`tool_choice`、`tool_calls`、`tool_call_id` 及 `role: "tool"` 消息。
- **Agent 宿主环境正向引导与自主工程探索**：在包含工具的请求中，自动将客户端提供的 JSON Schema 工具定义与交互协议编译为结构化指令，明确指导模型在承接功能实现任务时，主动调用 `list_files` 或 `read_file` 探测工程目录与配置文件（如 `package.json`），禁止反问用户技术栈或输出身份冗余废话。
- **多轮工具回执持续执行 (Tool Continuation)**：针对 Agent 提交的 `role: "tool"` 运行结果，自动衔接下一步执行指令，驱动模型实现多轮多工具（如并行读取多文件、写回修改）的自主迭代循环。
- **DeepSeek Harness / 文本工具模式自适应赋能**：
  - 针对 DeepSeek Harness (`dsh`)、Roo Code 等在系统提示词中声明指令（如 `pwsh`、`glob`、`web_search` 等）而未在请求体下发 JSON `tools` 字段的场景，网关自适应注入嵌入式智能体运行态指令；
  - 严正告知模型宿主环境指令处于完全可用状态，彻底根除模型输出 *"与标准 DeepSeek Harness 智能体工作区指令（如 pwsh、glob、web_search 等）在此对话层并未直接开放或已被限制"* 的认知偏差；
  - 驱动模型在承接功能创建/修改指令时，主动通过 `glob` 或 `pwsh` 巡检当前工程，杜绝向用户反复索要文件路径或技术栈。
- **严格符合 OpenAI 规范的流式 SSE 分块 (修复 Client Agent 崩溃)**：
  - **强制包含 `index` 属性**：在 `delta.tool_calls` 数组中严格注入 `index: 0, 1, 2...`，彻底解决 Cline / Roo Code 等客户端解析器因缺少 `index` 抛出 `TypeError: Cannot read properties of undefined` 并报错 *“I encountered an error doing what you asked”* 的兼容性缺陷；
  - **规范的两阶段流式状态转移**：第一阶段下发工具参数与 `finish_reason: null`，第二阶段下发带 `finish_reason: "tool_calls"` 与 `usage` 的终态分块，确保 Agent 状态机平稳切换；
  - **真实 Token 估算统计**：中英文多模态精确估算 `Usage`（`prompt_tokens`, `completion_tokens`, `total_tokens`），告别客户端 UI 显示 `用量 0 tok` 的困扰；
  - **云端全量毛刺智能拦截重试 (Glitch Auto-Recovery)**：
    - 全面捕获 Gemini Web 各类瞬态模板报错（如 *"I encountered an error doing what you asked"*、*"I seem to be encountering an error. Can I try something else for you?"*、*"Sorry, something went wrong"*、*"I'm having a hard time fulfilling your request"* 等）；
    - 在非流式与流式下发前实施静默拦截并启动退避自动重试（最多 3 次，第 2 次起刷新 `bl`/`at` 会话参数），杜绝报错信息污染客户端 Agent 对话流；仅匹配 200 字符以内的短回复，避免误伤正文中恰好包含这些句子的代码。
    - 重试耗尽后返回**真实 HTTP 错误**（502，OpenAI 错误体），而不是空回复或把报错文案当作答案——客户端据此重试/提示，且不会把错误写入对话历史。
    - 回放历史时自动剔除历史中的 Gemini 报错回复，并合并因报错而被客户端重复发送的相同用户消息，避免模型被历史污染而反复报错/答非所问。
  - **限流识别（`google.com/sorry`）**：短时间内请求过密时 Google 会 302 到反滥用验证页。网关识别后立即返回 `429 rate_limited`（不重试、不刷新会话以免加重限流）。出现时请暂停请求，并在同一网络的浏览器中打开 gemini.google.com 完成验证。
  - **无工具（文本协议）模式不再谎称已完成**：不再向模型断言“所有工具均已可用并由客户端代为执行”，该措辞会让模型在没有任何工具通道时编造“已成功修改文件”。现在仅引导模型遵循客户端系统提示词中定义的工具语法，且要求只有工具结果确认后才可声明已修改。
  - **请求体宽容解析**：`stop` 支持字符串/数组，`tool_calls[].function.arguments` 支持字符串/对象，`model` 为空回退默认模型，`developer`/`function` 角色可识别；无法解析的请求返回带说明的 JSON 400，而不是空响应。

---

## 🔀 支持模型列表

支持通过标准 OpenAI 请求体中的 `"model"` 参数直接指定（同时支持带 `gemini-` 前缀或直接使用简称/空格命名，如 `3.8 Flash`、`3.5 Flash-Lite`、`3.1 Pro`、`4 Argon`），网关自动自适应映射至 Gemini Web 内部引擎：

| 模型标识 (Model ID) | 别名/简称 | 架构体系 | 思考能力 | 适用推荐场景 |
| :--- | :--- | :--- | :---: | :--- |
| **`gemini-4-argon`** | `4 Argon`, `4-argon`, `argon` | Google Gemini 4.x | 🔮 前沿突破 | **下一代旗舰探索**：最新代号 Argon 前沿大模型 |
| **`gemini-3.8-flash`** | `3.8 Flash`, `3.8-flash`, `gemini-3.8` | Google Gemini 3.x | ✅ 深度思考 | **强推旗舰**：极高推理能力、顶尖代码编程与深度思维链 |
| **`gemini-3.5-flash-lite`** | `3.5 Flash-Lite`, `3.5-flash-lite` | Google Gemini 3.x | ⚡ 极致轻量 | **极速推荐**：轻量级补全、极低首字延迟、高并发吞吐 |
| **`gemini-3.1-pro`** | `3.1 Pro`, `3.1-pro`, `gemini-3.1` | Google Gemini 3.x | 🎯 强力推理 | **高阶推导**：复杂数学、长逻辑架构设计与高精度分析 |
| **`gemini-2.5-pro`** | `gemini-2.5-pro` | Google Gemini 2.5 | 🎯 强推理 | 复杂逻辑推导、长文本理解 |
| **`gemini-2.5-flash`** *(默认)* | `gemini-2.5-flash` | Google Gemini 2.5 | ⚡ 极速 | 通用日常对话、快速总结 |
| **`gemini-2.5-flash-thinking`** | `gemini-2.5-flash-thinking` | Google Gemini 2.5 | ✅ 思维链 | 深度思考分析任务 |
| **`gemini-2.0-flash`** | `gemini-2.0-flash` | Google Gemini 2.0 | ⚡ 快速响应 | 轻量对话与高吞吐调用 |
| **`gemini-1.5-pro`** | `gemini-1.5-pro` | Google Gemini 1.5 | 🎯 稳定生产 | 传统长文推理 |
| **`gemini-1.5-flash`** | `gemini-1.5-flash` | Google Gemini 1.5 | ⚡ 极速 | 极低延迟响应 |

---

## 🚀 快速启动指南

### 运行环境
- **操作系统**：Windows 10/11、Linux、macOS
- **SDK**：[.NET 10.0 SDK](https://dotnet.microsoft.com/download)

---

### 1. 准备 Cookies
在浏览器中打开并登录 [https://gemini.google.com](https://gemini.google.com)，按 `F12` 打开开发者工具：
- 切换至 **Application** (应用) -> **Cookies** -> `https://gemini.google.com`
- 复制 `__Secure-1PSID` 与 `__Secure-1PSIDTS` 的值，拼接成形如：
  ```
  __Secure-1PSID=xxxx...; __Secure-1PSIDTS=yyyy...;
  ```

---

### 2. 启动服务

#### 方式 A：前台实时运行（开发与调试）
```powershell
.\scripts\start_gateway.ps1 -Port 5000
```

#### 方式 B：后台静默守护运行（日常推荐）
```powershell
# 1. 后台无窗口启动
.\scripts\start_background.ps1 -Port 5000

# 2. 检查健康状态
curl http://127.0.0.1:5000/v1/health

# 3. 停止服务
.\scripts\stop_gateway.ps1 -Port 5000
```
*(运行日志将自动沉淀在 `logs/gateway_stdout.log` 和 `logs/gateway_stderr.log`)*

#### 方式 C：Docker / Docker Compose 部署
```bash
# 复制环境变量配置
cp .env.example .env
# 编辑 .env 填入 GEMINI_WEB_COOKIES

# 启动容器
docker-compose up -d
```

---

## 🧪 接口调用示例

### 1. 健康检查 (`GET /v1/health`)
无需鉴权，直接调用：
```bash
curl http://127.0.0.1:5000/v1/health
```
响应：
```json
{
  "status": "ok"
}
```

### 2. 获取可用模型列表 (`GET /v1/models`)
```bash
curl http://127.0.0.1:5000/v1/models \
  -H "Authorization: Bearer YOUR_COOKIES"
```
响应：
```json
{
  "object": "list",
  "data": [
    {
      "id": "gemini-2.5-pro",
      "object": "model",
      "created": 1729567890,
      "owned_by": "google"
    },
    {
      "id": "gemini-2.5-flash",
      "object": "model",
      "created": 1729567890,
      "owned_by": "google"
    }
  ]
}
```

### 3. 非流式对话 (`POST /v1/chat/completions`)
```bash
curl http://127.0.0.1:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_COOKIES" \
  -d '{
    "model": "gemini-2.5-flash",
    "messages": [
      {
        "role": "user",
        "content": "请用一句话介绍你自己"
      }
    ]
  }'
```

### 4. 实时流式 SSE 对话 (`stream: true`)
```bash
curl http://127.0.0.1:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_COOKIES" \
  -d '{
    "model": "gemini-2.5-flash",
    "messages": [
      {
        "role": "user",
        "content": "写一首关于秋天的四句小诗"
      }
    ],
    "stream": true
  }'
```

### 5. Python 官方 OpenAI SDK 调用
```python
from openai import OpenAI

client = OpenAI(
    base_url="http://127.0.0.1:5000/v1",
    api_key="YOUR_COOKIES"  # 传入你的 Gemini Web Cookies
)

# 1. 非流式测试
response = client.chat.completions.create(
    model="gemini-2.5-flash",
    messages=[{"role": "user", "content": "你好，请推荐 3 本计算机科学经典名著。"}]
)
print(response.choices[0].message.content)

# 2. 流式测试
stream = client.chat.completions.create(
    model="gemini-2.5-flash",
    messages=[{"role": "user", "content": "讲一个简短的程序员笑话"}],
    stream=True
)
for chunk in stream:
    if chunk.choices[0].delta.content:
        print(chunk.choices[0].delta.content, end="", flush=True)
```

### 6. 工具调用 / Function Calling 示例 (Agent 读写本地文件场景)

```bash
curl http://127.0.0.1:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_COOKIES" \
  -d '{
    "model": "gemini-4-argon",
    "messages": [
      {
        "role": "user",
        "content": "请查看本地文件 E:/Personal/LiveConversation/test.txt 并告诉我它的内容"
      }
    ],
    "tools": [
      {
        "type": "function",
        "function": {
          "name": "read_file",
          "description": "读取本地工作区中的文件内容",
          "parameters": {
            "type": "object",
            "properties": {
              "path": { "type": "string", "description": "文件的绝对或相对路径" }
            },
            "required": ["path"]
          }
        }
      }
    ]
  }'
```

响应体（完全符合 OpenAI 官方规范）：
```json
{
  "id": "chatcmpl-bc4eeb6faed9418dbd67895f05214574",
  "object": "chat.completion",
  "created": 1792855666,
  "model": "gemini-4-argon",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": null,
        "tool_calls": [
          {
            "id": "call_12345678",
            "type": "function",
            "function": {
              "name": "read_file",
              "arguments": "{\"path\": \"E:/Personal/LiveConversation/test.txt\"}"
            }
          }
        ]
      },
      "finish_reason": "tool_calls"
    }
  ],
  "usage": {
    "prompt_tokens": 0,
    "completion_tokens": 30,
    "total_tokens": 30
  }
}
```

---

## 📁 项目工程目录

```
E:\Personal\GeminiWebAgent\
├── .env.example                                  # 环境变量模版
├── .gitignore                                    # 针对 .NET / VS 的标准忽略配置
├── docker-compose.yml                            # 容器编排部署
├── Dockerfile                                    # 多阶段 .NET 10 轻量镜像构建
├── GeminiWebAgent.slnx                           # .NET 10 现代格式解决方案
├── README.md                                     # 项目完整技术文档
├── scripts/                                      # 自动化管理脚本
│   ├── start_background.ps1                      # 后台静默守护启动
│   ├── start_gateway.ps1                         # 前台实时启动
│   ├── stop_gateway.ps1                          # 一键停止服务
│   └── verify_all.ps1                            # 全量自动化测试验证
├── src/GeminiWebAgent/                           # 主服务项目
│   ├── appsettings.json                          # 主配置文件
│   ├── appsettings.Development.json              # 开发环境配置
│   ├── GeminiWebAgent.csproj                     # .NET 10 Web SDK 配置
│   ├── GeminiWebAgent.http                       # 接口调试测试用例
│   ├── Program.cs                                # 启动装配与管道配置
│   ├── Configurations/
│   │   └── GeminiWebOptions.cs                   # 配置选项 POCO
│   ├── Endpoints/
│   │   ├── ChatCompletionEndpoint.cs             # /v1/chat/completions 路由端点
│   │   ├── HealthEndpoint.cs                     # /v1/health 路由端点
│   │   └── ModelEndpoint.cs                      # /v1/models 路由端点
│   ├── Middlewares/
│   │   └── ApiKeyAuthMiddleware.cs               # 鉴权与 Cookie 透传中间件
│   ├── Models/
│   │   ├── Common/                               # 通用数据结构
│   │   ├── GeminiWeb/
│   │   │   └── GeminiWebModels.cs                # 会话与模型映射字典
│   │   └── OpenAI/
│   │       ├── OpenAIChatCompletionRequest.cs    # OpenAI 请求契约 DTO (含 Tools/Functions)
│   │       ├── OpenAIChatCompletionResponse.cs   # OpenAI 响应契约 DTO (含 ToolCalls)
│   │       └── OpenAIModelResponse.cs            # OpenAI 模型列表契约 DTO
│   ├── Properties/
│   │   └── launchSettings.json                   # 调试启动配置
│   └── Services/
│       ├── CookieKeepAliveBackgroundService.cs   # Cookie 后台保活巡检后台服务
│       ├── GeminiWebChatService.cs               # batchexecute 对话、流式与 Tool Calling 转译服务
│       ├── GeminiWebCookieManager.cs             # 滚动 Cookie 拦截与持久化管理器
│       ├── GeminiWebModelService.cs              # 模型查询服务
│       └── GeminiWebSessionService.cs            # 动态会话参数与构建标识提取服务
└── tests/GeminiWebAgent.Tests/                   # 自动化测试项目
    ├── GeminiWebAgent.Tests.csproj               # xUnit 2.9.3 + .NET 10 测试项目
    ├── IntegrationTests/
    │   └── GatewayLiveTests.cs                   # 端到端存活与鉴权测试
    └── UnitTests/
        ├── BatchExecutePayloadTests.cs           # 报文编解码与前缀剥离单元测试
        ├── GeminiWebCookieManagerTests.cs        # Cookie 管理与滚动更新单元测试
        ├── GeminiWebModelMapTests.cs             # 模型映射与别名单元测试
        ├── GeminiWebSessionExtractionTests.cs    # 正则提取与参数匹配单元测试
        ├── OpenAIContractsTests.cs               # OpenAI 契约序列化单元测试
        └── ToolCallingTests.cs                   # Tool Calling 双向解析与提示词单元测试
```

---

## 🎯 自动化测试结果

项目配备完整的 xUnit 测试套件，覆盖核心功能、契约转换与网络协议编解码：

```powershell
.\scripts\verify_all.ps1
```

执行结果：
```
已通过! - 失败: 0，通过: 55，已跳过: 0，总计: 55，持续时间: 25 s - GeminiWebAgent.Tests.dll (net10.0)
==========================================================
   ALL .NET 10 TESTS PASSED SUCCESSFULLY! (100% PASS)    
==========================================================
```
