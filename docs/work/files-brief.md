# 文件接收与整理任务

项目 Windows C# / .NET 9 / WPF 奶蛙桌宠，工作目录 D:/abcd/naibaby_desk。用户授权接入非游戏功能，配置放集中设置。先读 src/Naifrog/Core/AppSettings.cs 和 SettingsStore.cs。

仅负责 src/Naifrog/Files/ 和 tests/FileChecks/，不改主窗口/设置页/共享Core/csproj。无 Git仓库，无提交。不得操作用户真实桌面文件、运行外部项目；验证只使用自己创建的临时目录。

提供 public sealed class FileOrganizerService(SettingsStore store)，namespace Naifrog.Files：
- Task<string> ReceiveAsync(string[] paths, CancellationToken token)：按 store.Current.Files.ReceiveFolder（为空则 DataDirectory/ReceivedFiles）和 ReceiveMode Copy/Move；默认复制；Move也安全验证，拒绝symlink/reparse，避免同名覆盖。返回用户可读结果摘要。
- public sealed class OrganizerWindow : Window，ctor OrganizerWindow(SettingsStore store, Func<string,string,CancellationToken,Task<string>> completeText, Action<string>? notify = null)。root 负责单窗口复用。窗口自带预览UI：选择源/目标文件夹、扫描、可勾选/可编辑分类表格、可选AI细化、引用/快捷方式/复制/移动模式、执行与撤销、状态/错误。窗口不要命名为设置，配置默认值取store.Current.Files；保存默认设置可调用store.Save拷贝，避免清掉其他配置。

整理：本地规则按扩展/名称分类；AI仅在用户主动点细化且模型可用时调用 completeText(system,user,ct)，发送文件名而非内容/凭据，校验返回只允许已有item ID与安全分类名；结果给用户复核。扫描顶层普通文件（可显示文件夹仅用于引用/快捷方式，复制移动先拒绝目录并说明）。限制扫描数量避免卡UI。通过引用索引JSON（保留原位）、Windows快捷方式COM、复制或移动到分类目录。实际写入均须预览后用户点击执行，移动再明确确认对话框。

实现可撤销journal，任何同名不覆盖，操作前记录意图与完整路径，完成后记完成状态；逐项失败可继续，取消保留已完成journal可撤销。复制/移动验证长度/SHA256并flush；Move仅在验证后删除原文件，不删除源目录。Undo需校验当前目标哈希/类型未变化，源不存在才还原，冲突或变化时保留并报告。引用/快捷方式Undo也不误删被用户更改的文件。不能用任意用户路径拼接成命令，不调用shell删除。

整理窗口应有完整操作反馈与取消，关闭时取消未完成扫描/AI任务；不在关闭后继续触发文件执行。文件接收同时借鉴事务式保存，默认复制保留原文件。

编写有意义的临时目录检查：同名保护、copy/move及undo、修改后拒绝undo覆盖、取消、目录/链接拒绝、journal恢复边界。独立测试csproj链接Core + 服务源（不需要UI），先写失败检查再实现。可自行设计内部plan/journal API供测试。

完成写 docs/work/files-report.md，含API、功能、检查命令和结果、边界限制；只简短返回。完整DeskBox不是本任务依赖，自己实现适用子集，不要引入外部exe。
