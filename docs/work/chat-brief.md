# 聊天任务

项目：Windows C# / .NET 9 / WPF 奶蛙桌宠，工作目录 D:/abcd/naibaby_desk。用户授权接入非游戏功能，明确要求用户自行配置模型 API、模型名称、系统提示词。公共类型已在 src/Naifrog/Core 中建立，请先读 AppSettings.cs 与 SettingsStore.cs。

仅负责 src/Naifrog/Chat/ 和 tests/ChatChecks/，不要改其他代码、csproj 或共享类型。无 Git 仓库，不提交、不创建分支。不要接触真实用户 API、不运行外部项目。自己编写实现，不复制上游源文件。

实现 public sealed class ChatService : IDisposable，namespace Naifrog.Chat。
- ctor ChatService(SettingsStore store, HttpMessageHandler? handler = null)。可注入 HTTP handler 以本机测试。
- bool IsConfigured：BaseUrl 与 ModelId 已填写。
- Task<ChatResult> SendAsync(string text, Action<string>? onDelta, CancellationToken cancellationToken, byte[]? imagePng = null)。返回 Core.ChatResult。onDelta 回调为累计正文文本，调用线程可能非 UI；仅完整成功结果写记忆。支持 Streaming 开关、SSE delta、非流式 JSON、[DONE]、中文、跨 chunk；忽略 reasoning 字段。避免并发问答污染上下文，处理取消、超时、有部分正文后的错误，不自动重放已产生正文的请求。
- Task<string> CompleteRawAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)：不写聊天历史、不分类情绪，供主动陪伴/整理。
- Task<string> TestConnectionAsync(CancellationToken cancellationToken)：不写记忆、不分类情绪，不展示 Key。
- IReadOnlyList<ChatMessage> History { get; }；void ClearMemory()；void ReloadMemory()；void ExportMemory(string path)。记忆文件只包含文本，用 LocalVault + AtomicFile 加密保存，限制 Data.MaxMemoryPairs；发送上下文限制 Model.ContextPairs，整体有字数预算。
- string Diagnostic { get; }，最近状态，不含 Key/正文/截图。

模型地址用户填兼容 OpenAI chat/completions 的 Base URL（例如 https://example.com/v1）；可容错用户直接填完整 /chat/completions，不能重复拼接。仅 HTTP loopback 或 HTTPS（AppSettings 已有校验，服务也检查）。自定义 ModelId/SystemPrompt/Timeout/MaxTokens 生效；不硬编码任何真实模型 ID。有 Key 才加 Bearer，允许本机无 Key 模型。

如 Emotion.Enabled && Emotion.AnalyzeWithModel，在正式正文前独立小请求判断 Core.EmotionIntent，4秒超时、单次尝试、严格JSON校验，低置信/失败降级 neutral 而正文继续；标签需是已知26表情/neutral，引用 Core.EmotionSettings.Instruction。模型分析 prompt与普通聊天文本分离。imagePng 仅在调用方明确传入时转换 data:image/png;base64；不进入记忆或情绪分析。用户自定义系统提示词应用于正式回复；主动陪伴用调用方传入prompt。

验证需有意义的注入 handler / 本机模拟，不使用真实外网服务：检查路径/模型/自定义提示词，SSE累计，取消不保存，坏分类不中断正文，加密恢复和截断。可独立创建控制台测试项目链接 Core + Chat 源文件（WPF enabled），避免主任务UI未完导致构建干扰。先写并运行至少一个失败检查再实现。完成后写 docs/work/chat-report.md，记录文件、接口、测试命令与实际结果、风险。最终只返回状态与摘要，不要输出整份源码。
