# 动画与情绪实现报告

日期：2026-10-28。只修改 `src/Naifrog/Animation/`、`tests/AnimationChecks/` 和本报告。没有改动 Core、UI、聊天、文件整理或资源，也没有运行上游源码。

## 交付接口

命名空间 `Naifrog.Animation`：

```csharp
public record ExpressionDefinition(string Name, string Label, string Gesture,
    int Priority, bool Automatic = true);
public static class EmotionCatalog
{
    public static IReadOnlyList<ExpressionDefinition> Entries { get; }
}
public sealed class PetAnimator : IDisposable
{
    public PetAnimator(Image target, Func<AppearanceSettings> appearance,
        Func<EmotionSettings> options, string assetsRoot);
    public void SetEmotion(EmotionIntent intent, bool manual = false);
    public void SetBlocked(bool blocked);
    public void ApplySettings();
    public string CurrentEmotion { get; }
    public string Status { get; }
    public event Action? StateChanged;
    public void Dispose();
}
```

构造器须在 Image 所属 UI 线程运行。普通更新入口可由其他线程调用，内部投递到 Dispatcher；事件在 UI 线程发出，只有状态文字变化才触发。建议由窗口在 UI 线程 Dispose。卸载 Image 时停止定时器，重新加载时恢复按需调度；Dispose 停止定时器、解除 Loaded/Unloaded/Tick 和 StateChanged 订阅、释放缓存并恢复原 Image 变换。

`assetsRoot` 是实际 `animations` 文件夹路径。建议主程序传入 `Path.Combine(AppContext.BaseDirectory, "animations")`。窗口位置、拖动和召唤全部由主任务管理；本模块只改变 Image 的 Source 与 RenderTransform。

目录共 36 项：26 个自动表情、7 个自用姿势、organize，再加 eat/walk 两个本地业务动作。自动 26 项为 happy、surprised、confused、shy、sad、angry、love、proud、cheer、celebrate、greet、grateful、agree、refuse、ponder、curious、scared、worried、crying、wronged、disappointed、embarrassed、sleepy、annoyed、relieved、hug。自用 7 项为 smile、excited、focused、bored、smug、dizzy、shocked。neutral 是额外待机标签，播放 idle 片段。

## 调度行为

- `EmotionScheduler` 无 UI 依赖，构造器接受毫秒时钟、配置提供器及片段时长提供器。实际 Animator 使用 Stopwatch 单调时钟。
- 入场、主片段、退场按绝对经过时间推进，UI 延迟跨越多个阶段时不会逐帧补播。非循环主片段播放一次后退场；循环主片段到意图期限后退场。
- 同一情绪合并，强度使用旧值 65% + 新值 35%，不会重启。已进入退场的重复情绪也不会在退场后重播。
- 使用目录优先级与请求优先级的较高值，organize 为 45。普通低优先级意图不打断较高优先级动作，保存单个等待槽；手动动作允许经过安全退场后打断，并绕过冷却。
- 冷却读取 `EmotionSettings.CooldownSeconds`，按情绪名称分别记录上次开始时间；不同情绪不会相互增加冷却等待。等待意图在收到时开始计算期限，阻塞或同情绪冷却结束后已过期的意图不会播放。
- 阻塞期间不启动或抢占动作，已有动作仍可自然结束；整图微动作暂停。解除阻塞时重新检查期限。
- 强度小于 0.18、低于最低置信度、未知标签或非有限数值转 neutral；happy 强度小于 0.6 时转 smile。neutral 请求引导当前动作退场。
- `Enabled=false` 忽略后续自动请求并引导现有自动情绪退场；不会结束或更改手动动作，也不会清空等待中的手动动作。MicroMotion 和动画包开关独立工作。

## 素材与安全限制

每个片段使用 `animations/<name>/manifest.json`，字段包括 name、emotion、fps、frameCount、loop、priority、可选 transitionIn/transitionOut、canvas.width/height、groundY、frames[{file,sha256}]。字段名称大小写兼容。mouthAnchor、reviewed 等额外美术元数据不会影响播放。

限制如下：清单 256 KiB、嵌套深度 16；名称为最多 64 位小写字母/数字/下划线/短横线；帧率 1–60；每片段 1–240 帧；画布每边 1–2048；每张 PNG 最多 4 MiB，单片段编码图片合计最多 64 MiB；单片段解码预算 128 MiB。frameCount 必须与清单数组一致，图片像素尺寸必须与画布一致；PNG 只接受不超过 8 位位深。

帧必须在所属片段目录内，拒绝绝对路径、父级路径、空路径片段、ADS、重复路径及非 PNG 文件。清单、帧和全部祖先目录检查 ReparsePoint，拒绝符号链接/目录联接，不递归扫描目录。读取时另设硬字节上限，并核对 SHA-256；PNG 头中的尺寸在交给 WPF 解码前检查，解码后再次核对。任何缺失或无效片段降级到内置原始立绘，不信任受损帧继续播放。

清单按需读取并缓存，最多 128 项；不在每次意图时扫描整个资源包。每片段一次性解码、Freeze，逐帧仅切换已冻结 BitmapSource。解码序列使用最多 4 项且总预算 192 MiB 的 LRU 缓存，加载前淘汰旧项；内存预算针对持有的解码像素，WPF/解码器对象和 GC 延迟回收另有开销。丢失过渡片段时可跳过该阶段；丢失主素材时保持原始立绘与可用整图微动作。

`UseAnimationPack=false` 始终显示 `pack://application:,,,/Assets/naifrog.png`。素材建议保持统一 420×560 画布、groundY=540 和角色比例；播放器使用脚底比例作为变换原点，并对基线修正限幅。

## 计时与动作表现

呼吸、摆动、弹跳、颤抖、轻微压缩/回弹、整体倾斜通过 WPF Scale/Rotate/Translate 组合实现，强度读取配置并限幅。它们是整图二维微动作，不是 3D、局部骨骼或独立四肢动画。

需要微动作或多帧播放时使用约 40 ms 定时器。关闭 MicroMotion 且只有静态动作时使用 250 ms 自然到期检查；完全静态待机没有等待工作时停止定时器。关闭 MicroMotion 后，多帧动画包仍按需播放。

## 实际检查

先为调度器建立占位实现并运行检查，观察到 `FAIL: entry begins immediately`，然后实现调度器；资源层先观察到 `FAIL: valid manifest timing` 再实现加载；WPF 层先观察到 `FAIL: public animator routes intent` 再实现。自审补充了退场期间重复情绪回归用例，先复现失败，再修复重复重播。

无可见窗口、无网络、无第三方测试包的控制台检查：

```powershell
dotnet run --project tests/AnimationChecks/AnimationChecks.csproj
dotnet run --project tests/AnimationChecks/AnimationChecks.csproj -- --pack assets/animations
```

最新完整素材检查退出码为 0，输出 `Pack verified: 52 clips, 1299 frames.` 和 `Animation checks: 198 passed.`。其中基础检查 38 项，实际素材每片段检查清单、完整哈希与冻结解码、缓存预算，共 156 项，真实 Animator 素材接入再检查 4 项。涵盖冷却、合并、优先级、阻塞、过期、自然退场、跨阶段时间跳跃、低置信和非有限数值、路径越界、错误哈希、超大画布、原始立绘降级、计时器节流、变换恢复。实际动画包的所有 1299 帧已成功解码，不仅验证文件是否存在。

## 已知限制

- 第一次使用一个片段时在 UI 线程同步验证及解码，会有短暂开销；后续使用命中有界缓存。缓存淘汰后再次使用会重新解码该片段。
- 读取失败或缺失的资源在该 Animator 生命周期内保持负缓存；替换/安装新资源包后应重新创建 Animator 或重启应用。
- 不实现局部骨骼、嘴部口型、面部部件拆分、窗口移动或长期心情数值。本模块不使用 mouthAnchor。
- 自用 walk 当前没有专用主片段时使用原始立绘与摆动微动作；实际窗口散步由主窗口实现。
- 路径检查针对正常资源安装及符号链接防护，不构成对同权限进程并发篡改资源目录的操作系统沙箱。
- 本子任务没有执行主窗口的完整交互或发布验证；由主任务负责整合检查。

## 2026-10-29 复审修复与重验

修复两处调度行为：关闭自动情绪后直接忽略非手动提交，保留活动手动预览与等待手动动作；冷却改为按情绪分别记录最近启动时间，与设置中的“相同动作冷却秒数”一致。重复的活动情绪仍合并、不重启；手动动作仍可绕过冷却；关闭开关仍由 Tick 引导活动自动动作退场。

先加入回归检查，分别观察到预期失败 `FAIL: disabled automatic request preserves manual preview` 和 `FAIL: different emotion bypasses unrelated cooldown using safe exit`，再修改产品代码。新增十项断言覆盖不同情绪从 ponder 安全退场后直接接续、相同情绪的冷却前后边界、禁用输入对活动/等待手动动作的保护，以及禁用后现有自动动作仍自然退场；原全局冷却断言已改为优先级等待及不同情绪在退场后接续。

实际重验命令及结果：

```powershell
dotnet run --project tests/AnimationChecks/AnimationChecks.csproj
# 退出码 0；Animation checks: 48 passed.
dotnet run --project tests/AnimationChecks/AnimationChecks.csproj -- --pack assets/animations
# 退出码 0；Pack verified: 52 clips, 1299 frames.
# Animation checks: 208 passed.
```

本次产品修改仅 `EmotionScheduler.cs`，测试修改仅 `tests/AnimationChecks/Program.cs`。PetRuntime 的聊天结果与思考清理接入问题由主任务另行修复与验证。
