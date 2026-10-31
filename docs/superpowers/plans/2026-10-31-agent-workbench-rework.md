# 工程工作台重做计划

原生 WPF、不新增运行时依赖；用户六项要求优先于旧的双项右键菜单、原视觉样式和轮数阈值。无 Git 仓库，不创建 worktree 或提交。

- [x] 上下文/session：AgentSession + 新 Context helper + Core AgentSettings/AppSettings 对应验证 + tests/AgentChecks。Token 窗口、自动摘要/裁剪、服务上下文错误重试、去除默认轮数停止。事件继续兼容 AgentEvent Kind/Text，新增稳定 Id/ToolName 可选字段由 transport worker 提供；request 文本正在思考，compaction 整理上下文；tool_call/tool_result 对应 Id。保留完整执行结果与取消保护。
- [x] 模型 transport：AgentModelClient + AgentContracts + tests/AgentTransportChecks。CompleteAsync 原参数不变，末尾可选 Action<string>? onDelta=null（累积正文，不含推理）；AgentTurn 增加可选 ReasoningContent、PromptTokens、CompletionTokens。SSE/JSON 同时兼容，usage，安全上下文专用异常 AgentContextLengthException。无工具摘要请求不发送 tool_choice。接口 Key/endpoints 不切换，不复制 Key 日志。
- [x] 原生UI/root：AgentWindow 重做、Settings AgentPage token 设置、PetWindow/App 右键入口和独立普通窗口、原生会话可展开工具卡片、改动视图和输入区。移除轮数设置/显示。tests/AgentUiChecks 与真实 HTTP UI 测试覆盖。
- [x] 综合审查、回归、发布和新截图。持续执行至完成，不为已授权常规修复询问许可。

## 并行接口

AgentTurn(string Text,IReadOnlyList<AgentToolCall> ToolCalls,string? ReasoningContent=null,int? PromptTokens=null,int? CompletionTokens=null)。
AgentEvent(DateTimeOffset At,string Kind,string Text,string? Id=null,string? ToolName=null)。
AgentModelClient.CompleteAsync(AppSettings settings,string apiKey,JsonArray messages,JsonArray tools,CancellationToken token,Action<string>? onDelta=null)。
AgentContextLengthException : InvalidOperationException，消息为本地安全文本，可选 ProviderContextTokens，不含未经处理的响应体。HTTP 400 context_length_exceeded/context_window_exceeded/prompt_too_long 及明确上下文错误才可识别，非上下文错误不重试。

AgentSettings 增加 ContextWindowTokens=128000（8192..2000000）、AutoCompact=true。旧 MaxContextChars/MaxRounds 为兼容保留但不作为新运行默认停止条件；UI 不再显示轮数或字符设置。

Session公开API保持原样；UI依赖 Events 快照和 Changed。tool_call Text=name+'\n'+args；tool_result Text=name+'\n'+JSON；Id用于配对，旧记录可按先后兼容。assistant_stream 用同一个 Id 替换累计正文；最终 assistant 使用相同 Id 取代流式卡片，request只保留状态文字不显示行日志。
