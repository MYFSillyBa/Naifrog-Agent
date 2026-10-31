# 运行中引用与模型选择计划

使用 subagent-driven-development 分工，原生 WPF、无新运行时依赖、测试不使用真实 Key/工程；无 Git 仓库，不创建提交。

- [x] Session 引用队列：AgentSession.cs、tests/AgentChecks。API `bool TryQueueReference(string text)` 和 `int PendingReferenceCount`。事件 `reference_pending` / `reference` 使用相同稳定 Id，Text 为原始用户文字（脱敏）；纳入模型的引用为 role=user 的带标记字符串。待处理队列加密保存。原 RunAsync API 不变。
- [x] 模型列表服务：Chat/ModelCatalogService.cs、SettingsWindow.cs 仅 Model 区域、tests/ModelCatalogChecks。API `ModelCatalogService(HttpMessageHandler? handler=null)` IDisposable；`Task<IReadOnlyList<string>> ListAsync(AppSettings settings,string apiKey,CancellationToken token)`，快照、HTTPS/loopback、禁重定向、有界响应、错误脱敏。设置下拉可手动输入、获取列表不自动保存。模型名称的 AutomationName 保持不变。
- [x] Root UI：AgentWindow 运行时发送引用、引用队列状态和会话卡片、模型选择/刷新和并发过期保护；测试 AgentUiChecks、实际 WPF 模拟 HTTP。ModelCatalogService由worker负责，root只消费接口。
- [x] 静态复核、最后提示词修改前回归、发布。检测默认用户程序是否有活跃任务/草稿，保留用户会话，提供经过验证的新版。
