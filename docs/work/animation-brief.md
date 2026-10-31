# 动画与情绪任务

项目：Windows C# / .NET 9 / WPF 奶蛙桌宠，工作目录 D:/abcd/naibaby_desk。用户授权接入非游戏表情、情绪和动作。先读 src/Naifrog/Core/AppSettings.cs，Core.EmotionIntent 为公共请求类型。

仅负责 src/Naifrog/Animation/ 和 tests/AnimationChecks/，不要改其他代码或共享类型。无 Git，不提交。不要下载/运行上游源码。主任务将参考动画包放在程序根目录 animations/，内含各动作 manifest.json 和 PNG；原始立绘 pack://application:,,,/Assets/naifrog.png 作为兜底。主任务会建立 idle、happy、happy_in/out 等基础片段清单。

提供公共 API，namespace Naifrog.Animation：
- public record ExpressionDefinition(string Name, string Label, string Gesture, int Priority, bool Automatic = true)。
- public static class EmotionCatalog，Entries 为 IReadOnlyList<ExpressionDefinition>，涵盖原6种、20种扩展、7种自用、organize；Name 使用上游ID。可以新增 eat/walk 用于基础业务反馈，注明本地动画。
- public sealed class PetAnimator : IDisposable，ctor PetAnimator(System.Windows.Controls.Image target, Func<AppearanceSettings> appearance, Func<EmotionSettings> options, string assetsRoot)。assetsRoot 是 animations 目录。
- void SetEmotion(EmotionIntent intent, bool manual = false)、void SetBlocked(bool blocked)、void ApplySettings()。
- string CurrentEmotion、string Status、event Action? StateChanged。

内部职责：读取清单（name,emotion,fps,frameCount,loop,priority,transitionIn/out,canvas,groundY,frames[{file,sha256}]），限制路径在片段目录内，不遍历符号链接、限制图片/帧数/字节，不信任无效资源。缓存 Freeze BitmapSource（有界），不每帧解码、不每次触发加载整个文件夹。支持 entry/main/exit、单调时钟按时刻计算帧；优先级、冷却（options.CooldownSeconds）、重复情绪合并不重启、低置信/弱强度回待机、happy弱强转smile、阻塞时不抢占与等待意图过期、手动动作绕过冷却。缺素材保持原始立绘与可用微动作。

WPF Image.RenderTransform 上实现安全的小幅呼吸、摆动、弹跳、颤抖、下蹲回弹等整图微动作（这不是局部骨骼，文档不冒称3D/独立四肢）。Appearance.UseAnimationPack=false 时始终使用原始立绘，可表现微动作。MicroMotion关闭且静态/无活跃动作时停止快定时器；定时器只在需要时40ms左右，用低频计时检查自然到期也可。所有更新在UI线程；Dispose停timer、解除订阅。不要抢占主窗口移动；root管理Window位置与召唤。

做一个可无UI的内部 EmotionScheduler，便于通过可注入时间的控制台检查冷却、合并、阻塞、过期/退场；先失败再实现。独立测试csproj可链接 Core + Animation 文件，避免主任务尚未完成UI。不要复制上游源代码，实现参考机制即可。

完成写 docs/work/animation-report.md，含接口、检查命令/实际结果、素材配置要求、已知限制；最终只简要报告。
