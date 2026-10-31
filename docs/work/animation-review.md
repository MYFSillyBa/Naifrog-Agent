# 动画子系统复审

日期：2026-10-29。初次只读审查 `animation-brief.md`、`animation-report.md`、四个 Animation 源文件、现有 AnimationChecks，以及 PetWindow / PetRuntime 接入；随后按主任务授权修复调度器并增加回归检查，结果附于末尾。

## 两项结论

1. **规格符合性：通过本次动画子系统复审。** 公共接口与 36 项目录齐全，纯调度器、单调时钟、入场/主体/退场、重复合并、冷却、阻塞和过期等待槽都已落实。资源读取、Freeze、有界缓存、原始立绘兜底、整图微动作以及快慢计时器符合任务约束。关闭自动情绪对手动预览的影响已修复，冷却亦已与设置语义统一为按情绪计算。
2. **正确性与整合：调度器修复已通过重验，聊天整合待主任务关闭问题 1。** 未发现本次范围内需要阻断交付的资源越界、无限缓存、逐帧解码或正常 UI 退出订阅泄漏问题。窗口拖动与召唤由 PetWindow 维护，Animator 只修改 Image；应用退出经 PetRuntime.Dispose 释放 Animator 的链路存在。

## 可执行问题

### [P2] 快速聊天回复的情绪被 finally 清空

位置：`src/Naifrog/Desktop/PetRuntime.cs:87–91`；关联 `src/Naifrog/Animation/EmotionScheduler.cs:45–48,104–105`。

SendAsync 开始时通过手动请求启动 ponder，并更新调度器的 lastStarted。默认冷却为 6 秒；若回复在此期间返回，87 行提交的 happy 等自动结果会进入等待槽，CurrentEmotion 仍为 ponder。91 行 finally 随即提交 neutral，调度器 47 行清空等待槽，因此结果情绪在冷却结束后也不会播放。此问题影响正常的快速回复，不要求网络或素材失败。

复现：注入时钟，t=0 手动 ponder(20000ms)，t=1000 自动 happy(6000ms)，按 finally 的条件提交 neutral，t=7000 Tick，结果为 neutral/Idle，happy 从未启动。

建议：在提交成功回复情绪前结束思考状态，或让 finally 仅在取消/异常/未提交结果时清理思考；必须避免清掉刚提交的等待情绪。增加一个冷却内返回结果的接入回归检查。

### [P2，已修复] 禁用的自动请求仍会打断手动预览

位置：`src/Naifrog/Animation/EmotionScheduler.cs:40–48`。

`Enabled=false` 的自动请求在 42 行被归一化为 neutral，而 neutral 分支会清空等待槽并 BeginExit。用户关闭自动情绪后，手动预览本应继续可用，但一个应被忽略的自动输入能够提前结束当前手动动作，也能够取消已排队的手动动作。Tick 的 71 行已正确地只结束自动动作；Submit 的行为与之不一致。

复现：Enabled=false，手动 happy(10000ms) 开始于 Main；紧接着提交非手动 sad，立即变为 happy/Exit，201ms 后为 neutral/Idle。

建议：在自动情绪关闭时直接忽略非手动提交；继续由 Tick 结束已经活跃的自动动作。补充“关闭自动情绪时自动请求不影响手动动作/等待槽”的回归检查。

## 验证和边界

- 按任务要求未重复运行已有 198 项检查；其通过结果来自实现报告，本次不重新背书为新执行结果。
- 针对上述两个疑点，在系统临时目录建立独立 net9.0 控制台，直接链接现有 AppSettings / EmotionCatalog / EmotionScheduler，使用注入时钟复现；`dotnet run` 退出码 0，打印结果与上述描述一致。临时程序未加入工作区。
- 静态检查确认：清单/帧路径及祖先拒绝 ReparsePoint，清单/编码/解码/缓存均有限额，哈希及画布核对在冻结图片前完成；异常素材回退。正常 UI 线程 Dispose 停计时器并解绑 Loaded/Unloaded/Tick，恢复 Image 变换，释放缓存与状态订阅。
- 首次片段同步解码和无效资源负缓存是报告已明示的限制，本次不作为新增缺陷。并发同权限文件篡改不在其沙箱承诺内。未另行运行完整主窗口交互；主任务负责该验证。

## 修复后验证

问题 2 已由 Submit 在自动情绪禁用时直接返回修复，Tick 仍负责让已有自动动作退场。另据主任务发现的设置语义，原全局 lastStarted 改为按情绪名称记录时间，避免 ponder 的冷却延迟无关回复动作；相同情绪仍需等待自己的冷却期限。记录表最多为已知目录中的动作名称，不引入无界键来源。

上述两项修复均先观察回归断言失败，再实现。实际运行基础检查退出码 0，`Animation checks: 48 passed.`；完整素材重验退出码 0，`Pack verified: 52 clips, 1299 frames.`、`Animation checks: 208 passed.`。详细命令与新增覆盖见 animation-report.md 的 2026-10-29 修复段。前文“不重复运行”指初次只读审查阶段，本轮授权修复后已重验。
