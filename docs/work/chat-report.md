# 聊天子系统实现报告

日期：2026-10-28。范围仅为 `src/Naifrog/Chat/`、`tests/ChatChecks/` 与本报告；没有修改共享 Core、主 csproj、UI、动画或文件功能。没有调用真实模型服务、使用真实用户密钥、下载或执行上游项目，也没有提交 Git。

## 文件与公共接口

- `src/Naifrog/Chat/ChatService.cs`：独立实现 `Naifrog.Chat.ChatService : IDisposable`。
- `tests/ChatChecks/ChatChecks.csproj`：.NET 9 Windows/WPF 独立控制台检查，链接 Core 与 Chat 源码，避免依赖未完成的主 UI。
- `tests/ChatChecks/Program.cs`：注入 `HttpMessageHandler` 与逐字节响应流；临时数据目录在检查结束后清理。

公共接口与任务书一致：构造函数接收 `SettingsStore`、可选 handler；提供 `IsConfigured`、`SendAsync`、`CompleteRawAsync`、`TestConnectionAsync`、`History`、`ClearMemory`、`ReloadMemory`、`ExportMemory`、`Diagnostic`、`Dispose`。`ChatResult`、`ChatMessage`、`EmotionIntent` 全部使用 Core 已有类型。

## 行为与整合说明

1. 每次取得请求锁后快照配置与 Key。聊天、独立补全、连接测试共享异步串行锁；排队期间可以取消。后续聊天只看到完整成功的前序问答。没有任何自动重试。
2. 地址仅允许 HTTPS 或 HTTP loopback，禁止 userinfo/query/fragment。Base URL 自动拼接 `/chat/completions`；已填写完整路径及其尾斜杠均不重复拼接。默认 HTTP handler 禁用重定向。有 Key 时才发送 Bearer；允许本地无 Key 服务。
3. 模型名、正式回复系统提示词、stream 开关、max_tokens、超时均取配置。连接测试使用很小的 token 上限；独立补全使用调用方系统提示词，不读写记忆、不分类情绪。没有硬编码真实模型 ID。
4. SSE 由 UTF-8 StreamReader 跨网络 chunk 解析，支持 CRLF/LF、注释、多个 data 行、usage 事件、`[DONE]`、finish_reason 后 EOF。只累加 content，忽略 reasoning/tool-call 字段。若实际返回非流式 JSON，也可读取正文。每次 onDelta 传递累计全文，调用方须自行切回 WPF UI 线程。
5. SSE 缺少完成标志、JSON 无效、HTTP 错误、超时或取消均不记忆。onDelta 已发出的正文由 UI 保留；服务抛出异常，不自动重放。调用方可区分 `OperationCanceledException`、`TimeoutException`，其余失败通常为 `InvalidOperationException`/配置参数异常。HTTP 错误只暴露状态码，网络/JSON 错误不会携带服务端正文。
6. 情绪启用且选择模型分析时，在正式正文前发起一次独立小请求。使用 `Emotion.Instruction`，只发送有限文字语境，不含截图和正式回复系统提示词。4 秒截止；只接受严格的 `{name,intensity,confidence}` JSON 对象及指定 26 标签/neutral、有限 0–1 数值、达标置信度。失败、超时、无效或低置信统一 neutral，继续正文。调用方取消则停止整个流程。
7. 自动记忆仅为 user/assistant 文本对，写入 `SettingsStore.DataDirectory/chat-memory.dpapi`。使用 Core `LocalVault` 的 Windows 当前用户 DPAPI 与 `AtomicFile`，没有明文备份。限制 `MaxMemoryPairs` 及总字符预算；恢复时校验交替角色和大小。清空会写入加密空记录，并提升版本，阻止进行中的旧请求重新填回已清空记录。
8. `Data.RememberChat=false` 时不把旧记忆发给模型，也不保存新问答。旧加密文件保留，用户可显式清空；`ReloadMemory()` 在记忆关闭时清空当前会话列表。`History` 返回只读快照。设置切换后，UI 可调用 `ReloadMemory()` 立即更新展示。
9. `ExportMemory(path)` 是用户明确导出的 UTF-8 明文文本，应由 UI 保存对话框选择位置。自动保存仍然加密。保存记忆失败不丢弃已成功的模型回复：返回正文，并将 Diagnostic 设为记忆保存失败。
10. 截图只有明确传入 `imagePng` 时才作为最后一条用户消息的 `image_url` data URL 发送。会复制传入 buffer 以隔离排队期间的修改，不进入分类、记忆、诊断。

## 有界资源

- 单次请求文本（系统提示词、历史、当前消息）最多 32,000 个 UTF-16 字符；无法容纳时优先减少完整历史问答对，当前消息与提示词本身超额则拒绝。
- 上下文最多 `Model.ContextPairs` 对；持久记忆最多 `Data.MaxMemoryPairs` 对并受 256,000 字符总预算约束。
- 回复正文最多 64,000 字符，响应流最多 4 MiB，恢复记忆文件最多 2 MiB。
- 显式图片输入最多 10 MiB；图片不计入文字语境预算。

## 实际验证

运行环境：Windows、.NET SDK `9.0.310`。

首次实现前先运行 API 存在性检查，实际退出码 1：`FAIL: ChatService public API is missing.`。随后实现服务并逐步扩展检查。加密校验一度因 JSON 默认转义中文导致检查断言不正确，已改为 DPAPI 解密后反序列化记录并验证原文。

新增“关闭记忆不发送旧文本”检查时，实际先失败：`FAIL: disabled memory still sent prior text`；修正上下文快照后通过，形成该隐私边界的回归检查。

最终命令：

```powershell
dotnet run --project tests/ChatChecks/ChatChecks.csproj
```

实际退出码 0，输出 `PASS: 12 chat checks`，无编译警告。12 组覆盖：

1. 完整 endpoint 不重复拼接、自定义 model/system/max_tokens/nonstream、本地无 Key。
2. 逐字节拆分中文和 emoji 的 SSE、累计回调、reasoning 排除、DONE。
3. 首个正文回调后取消，不保存、不重试。
4. 无效情绪不中断正文，分析 prompt 隔离，截图只进入正文请求。
5. DPAPI 加密恢复、记忆轮数截断、上下文轮数、明文导出、清空重载。
6. 独立补全与连接测试不分类/不记忆，Bearer 正确，诊断不含 Key。
7. 截断、畸形、error SSE 的部分文字仅显示，不保存/不重试。
8. 同时调用串行，上下文只含已完成前轮，排队取消不产生 HTTP 请求。
9. 进行中清空后，旧请求完成不能复活记忆。
10. 情绪 4 秒超时、有效 hug、低置信、Markdown 包裹、超范围和额外字段校验。
11. 正式请求按设置 5 秒超时、401 不泄露服务端正文、服务层拒绝不安全地址。
12. 关闭记忆后不发送原上下文、不改写自动记忆，重载后历史为空。

## 风险与边界

- 仅验证本地注入 handler；没有确认任一真实供应商的兼容性、配额、视觉能力或认证。用户需自行填写 Base URL、模型、Key。
- 不是完整 OpenAI API SDK：未实现 Responses API、工具调用、reasoning 展示、供应商专有 schema/参数。目标是兼容 chat/completions 的正文接口。
- DPAPI 文件仅适用于当前 Windows 用户/机器；明文导出由用户自己保管。关闭记忆不会自动销毁旧加密文件，需显式清空。
- SSE 只有完成标志或明确 finish_reason 才认为完整；有些非标准本地服务可能需要调整 SSE 终止格式。
- 没有运行主 UI 构建，避免与其他实现中的文件互相干扰；主任务应在整合后做整个项目的构建与交互验证。

## 复核修正与补充验证（2026-10-29）

- P1：`SerializedAsync` 现在在取得串行锁后仅调用一次共享 Core 的 `SettingsStore.Snapshot()`，同时取得配置克隆与对应 Key，再把这对值用于整个请求（含情绪小请求）。不再分别读取 `Current.Clone()` 和 `ApiKey`。共享 Store 的原子发布/保存实现由主任务负责，本次未修改 Core。
- P2：情绪分析按 `Math.Clamp(Model.ContextPairs, 0, 2)` 选取完整历史问答对，并分别限制每条历史 1000 字、当前文字 4000 字。0 轮没有 recent 历史，1 轮仅含最近一对；正式正文继续使用已存在的上下文与总字符限制。
- 新增并发服务商切换回归：80 次配置/Key 切换与 120 次注入 HTTP 请求并发，逐请求核对 endpoint/model/Bearer 属于同一服务商。修正前实际失败：`provider endpoint/model/key snapshot mismatch count=3`；改用单次原子快照后通过。
- 新增保存失败回归：锁住 settings.json，强制替换文件失败，随后分别用现有 Store 与重新加载的 Store 发送注入请求；两者都保留原服务商的地址、模型和 Key。测试兼容 Windows 对锁定替换返回 IOException 或 UnauthorizedAccessException，未访问外网。
- 新增情绪开启且已有两轮历史、ContextPairs 分别为 0/1 的检查，同时核对分析和正文请求，并验证分析文字截断。
- 复查清空隐私边界：ClearMemory 在记忆锁内清空列表、提升版本并原子保存加密空记录；原有检查确认进行中回复无法重新写回旧历史。新增关闭记忆后显式清空、解密确认磁盘空记录、重新开启、重新加载与模拟重启均无旧文本的检查。仅关闭记忆仍按原设计保留旧加密文件，显式清空才覆盖它；清空遇到存储故障会抛出异常，不承诺磁盘已清空。

最终再次运行 `dotnet run --project tests/ChatChecks/ChatChecks.csproj`，实际退出码 0，输出 `PASS: 16 chat checks`，无编译警告。所有 HTTP 均使用注入 handler；未调用真实供应商、未使用真实密钥。

### 图片排队期间的授权复核（2026-10-29）

共享 Store 的 Snapshot 现会刷新其他实例修改并在服务商、Key 或屏幕作用域变化时撤销会话授权。ChatService 在取得请求锁和该快照后、任何情绪或正文 HTTP 请求前，拒绝携带图片且快照中 ScreenEnabled=false 的请求，避免旧授权下排队的图片发往新服务商。授权图片测试改为显式调用 `Save(settings, authorizeScreenGrant: true)`。

新增本机 handler 回归：用独立补全阻塞请求队列，在服务商 A 已授权时排入三字节模拟图片，再由第二个 Store 切换至 B。修正前实际失败 `FAIL: expected InvalidOperationException`（图片请求被继续执行）；修正后图片请求被拒绝、HTTP 总数仍为阻塞请求的一次、历史为空且授权已撤销。再次运行相同 ChatChecks 命令，退出码 0、`PASS: 17 chat checks`，无编译警告。未采集真实截图、未调用外网服务。

### 多实例清空一致性（2026-10-29）

复现：同一临时 profile 启动 A/B 两个 ChatService，A 保存一轮文本、B 加载并发起被 handler 阻塞的请求，随后 A 清空。旧实现实际失败 `FAIL: another instance still exposes cleared history`，B 的缓存仍可被读取/导出，且完成回复可能写回旧历史。

修正：每个 profile 使用独立命名互斥锁，且只在同步磁盘读写期间持有。清空、提交、History、发送上下文和导出均在锁内刷新共享加密记录。保存格式升级为 DPAPI 加密的 `{ClearRevision, Messages}`；旧数组格式仍可读取并在下次保存时迁移。清空 GUID 与空消息在同一个 AtomicFile 替换中发布，避免独立 revision 文件与消息文件之间的崩溃窗口。完成回复先读取最新共享记录；若清空 revision 已变则放弃记忆提交，否则在最新记录上追加完整问答，防止另一实例的缓存覆盖已清空或较新的记录。取消/部分回复仍不会保存。

新增公开只读 `string MemoryRevision`：每次读取会刷新磁盘状态，用于主任务主动陪伴等派生文本缓存的失效检测。返回值为不透明字符串，调用方只应比较相等性；普通成功聊天不改变清空 revision。History 返回的已交付快照、已经发出的模型请求无法撤回；清空后新的读取、导出和发送不再使用旧记录，清空前发出的请求完成后不能重新保存旧会话。

补充检查覆盖 B 在请求期间遇到 A 清空、独立缓存实例直接导出与下一次发送、两实例 revision 一致、旧请求完成不复活、清空后新聊天共享且重启只恢复新文本。另验证旧 DPAPI 数组迁移，以及锁住记忆文件导致 ClearMemory 抛出存储错误、磁盘字节/revision 不假报成功，再次正常清空才共同更新。读取失败会清空缓存；记忆启用时停止使用不可验证的文件，记忆关闭时仍可进行不带历史的聊天。存储失败不会吞掉已成功的模型回复。

最终完整执行 `dotnet run --project tests/ChatChecks/ChatChecks.csproj`，实际退出码 0，输出 `PASS: 19 chat checks`，无编译警告。无真实截图、网络或用户数据访问。
