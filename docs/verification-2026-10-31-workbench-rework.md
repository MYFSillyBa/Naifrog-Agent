# 工程工作台重做验收

日期：2026-10-31（Asia/Shanghai）。用户六项修正及追加图标修改均已完成。

## 本次变更

- 工作台不置顶，不再以置顶桌宠为 Owner；设置、聊天、整理工具窗口也与桌宠置顶独立。保存外观配置不会重新置顶这些窗口。
- 桌宠右键和托盘增加「工程工作台」直接入口。
- 原生 WPF 会话式工作台：左侧任务、中间消息、可展开的命令/文件工具卡片、输出和退出码、侧边改动、底部输入。没有 WebView 或网页运行时。
- 左上角 N 替换为用户三视图提取的奶蛙正面立绘，资源为现有内嵌 Assets/naifrog.png。
- 不展示当前/总轮数，不因旧 MaxRounds/MaxContextChars 停止；思考只显示状态，不展示私有推理。
- 按 token 窗口估算，结合 usage 校准，接近窗口前自动整理。服务明确提示上下文过长时，整理后最多重试两次当前模型请求，不重放工具。
- 完整加密历史与模型当前上下文分离；摘要包括真实参数/结果和用户约束，重要工程约定、失败和修改文件另有有界记录。保留的工具调用仍保留服务需要的私有 reasoning 元数据。
- 大量中文输出保持合法 JSON 及真实失败状态；显示记录裁剪后移除对应旧卡片，流式正文与最终回复共用稳定 ID。

## 根因

上一版失败源于本地 MaxContextChars=160000 的序列化字符硬阈值，而非 API Key 余额或接口配额错误。旧历史和工具结果累积后直接退出，未进行压缩。该逻辑已移除。新设置中的 ContextWindowTokens 默认 128000，是可配置的本地模型窗口预算；它不能改变服务实际窗口。

## 本次执行的验证

| 检查 | 结果 |
|---|---:|
| AgentChecks | 16 组通过 |
| AgentTransportChecks | 11 组通过 |
| AgentUiChecks | 11 项通过 |
| IntegrationChecks | 22 项通过 |
| StoreChecks | 13 组通过 |
| ChatChecks | 19 组通过 |
| 发布版模拟接口 + 实际 WPF | 通过 |
| 发布版聊天/连接/取消/右键菜单/退出 | 通过 |
| 发布版内嵌奶蛙资源渲染 | 通过 |

长任务回归覆盖超过旧40次停止阈值、重复大输出压缩、多次摘要失败、用户目标与后续指令保留、服务拒绝后逐次减小请求、命令不重复执行、多段 thinking 工具元数据、9000字中文 stdout+stderr 的失败码7。传输测试覆盖逐字节 UTF-8 SSE、增量工具参数、usage、JSON兼容返回、取消、不完整回复和上下文错误分类。

所有联网测试使用本机模拟服务或注入的 HTTP handler、假 Key；文件与命令使用临时目录。没有请求用户的真实模型服务或修改用户工程。

截图 `output/agent-workbench.png` 已实际检查，设置窗口关闭后截取，含奶蛙图标和展开命令输出。UI 测试先验证普通窗口/右键入口/旧控件移除，再验证流式替换和旧事件移除。

## 研究与实现边界

参考官方公开机制，自行实现 C# 原生界面及协议，未移植其 Web UI：

- https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/compaction/README.md
- https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/architecture.md
- https://developers.openai.com/codex/app-server#items
- https://github.com/openai/codex/blob/main/codex-rs/core/src/compact.rs

## 发布

发布到 `output/Naifrog-Agent` 完成真实 UI 验证后，同步到常用 `output/Naifrog`。EXE SHA256：

`D3E982255CBC3A681A13625B0F9E6E0B56B083EDA8A93DA53A05D9D0B7A2F27D`

保留同级 animations 和 licenses。默认用户配置未被测试覆盖。

模型本身仍有上下文窗口；用户指令或必要协议自身过长、服务持续拒绝、接口失败时明确停止。API Key 认证/计费、上下文窗口、最大输出 token 是不同概念。终端不是沙箱；文件工具撤销不包含终端改动；继续任务仍限当前窗口会话。
