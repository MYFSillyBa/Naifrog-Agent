using System;
using System.Collections.Generic;

namespace Naifrog.Agent;

public sealed record AgentToolCall(string Id, string Name, string Arguments);
public sealed record AgentTurn(string Text, IReadOnlyList<AgentToolCall> ToolCalls, string? ReasoningContent = null,
    int? PromptTokens = null, int? CompletionTokens = null);
public sealed record AgentEvent(DateTimeOffset At, string Kind, string Text, string? Id = null, string? ToolName = null);
public sealed class AgentContextLengthException : InvalidOperationException
{
    public int? ProviderContextTokens { get; }
    public AgentContextLengthException(int? providerContextTokens = null)
        : base("模型服务提示输入上下文超过窗口；需要整理上下文后继续。") => ProviderContextTokens = providerContextTokens;
}
public sealed record AgentFileChange(string Path, string? Before, string? After, string BeforeHash, string AfterHash);
