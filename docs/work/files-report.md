# 文件接收与整理完成报告

2026-10-29。范围：`src/Naifrog/Files/`、`tests/FileChecks/`，以及本文；未操作真实桌面文件，未调用外部 API、外部项目或 shell 删除。

## API 与集成

- `FileOrganizerService(SettingsStore)` / `Task<string> ReceiveAsync(string[] paths, CancellationToken)`：使用集中设置 `ReceiveEnabled`、`ReceiveFolder`、`ReceiveMode`；默认复制到应用数据目录的 `ReceivedFiles`，返回中文结果与 journal 路径。
- `OrganizerWindow(SettingsStore, Func<string,string,CancellationToken,Task<string>>, Action<string>?)`：提供源/目标选择、本地扫描、勾选/编辑分类、主动 AI 细化、执行、取消、日志选择及撤销。主窗口负责复用窗口实例。
- `OrganizerEngine`：`ScanAsync`、`ExecuteAsync`、`UndoAsync`、`ListJournals` 和原子校验 AI 分类的 `ApplyAiCategories`，可独立于 WPF 测试。
- 整理方式 ID 为 `Reference`、`Shortcut`、`Copy`、`Move`，与 `FileSettings.OrganizerMode` 一致。默认值保存通过 `Current.Clone()` 后 `Save`，保留其他配置。

## 行为与安全边界

顶层最多扫描 500 项；按扩展名与部分名称规则分类。目录仅支持引用索引/快捷方式。打开窗口或扫描不会写目标目录；执行须明确点击，移动额外弹出确认框。AI 只在主动细化且模型配置可用时调用注入的委托，只发送 ID 与文件名；未知/重复 ID、路径分类、保留名称或无效 JSON 整体拒绝，不部分改变预览。

所有路径检查现存祖先的重解析标志；拒绝链接、目录复制移动和多硬链接普通文件。目标同名自动编号，最终提交使用不覆盖的移动。复制采用唯一暂存文件、磁盘 flush、长度/SHA256 校验；移动仅在目标验证成功后，通过锁定的源文件句柄标记删除，保留源目录。

逐项操作在写入前保存意图，记录完整路径、状态、SHA256、文件身份及撤销暂存路径。日志原子替换并使用 HMAC 签名；本机签名密钥由 DPAPI 保护。取消保留完成项与 journal。撤销验证签名、路径结构、目标类型、文件身份和哈希；原位置冲突、目标修改/替换、未校验的中断暂存文件均保留并报告。移动恢复先验证暂存文件并提交到空缺的源位置，再删除锁定且验证的目标。可恢复已验证的中断恢复暂存文件。

此次复核修复了较矮窗口中预览表格与执行区重叠的问题：窗口高度限制在工作区内，整体支持垂直滚动，540px 高度仍能访问撤销区。关闭窗口取消扫描/AI/执行令牌，关闭后的异步回调不会启动新的文件执行。

## 验证

在 Windows/.NET 9 执行：

```powershell
dotnet run --project tests/FileChecks/FileChecks.csproj
dotnet run --project tests/FileChecks/WindowChecks/WindowChecks.csproj
```

结果：18 项核心检查全部通过；WPF 集成检查通过，退出码均为 0。所有输入、输出文件和设置/journal 均使用测试创建的唯一临时目录；测试结束清理该目录。WPF 渲染预览写入测试项目的生成输出目录。

核心检查覆盖：同名保护；复制/移动/跨引擎重启后的撤销；修改目标与源占用时拒绝覆盖；取消后撤销和预先取消；目录限制与引用修改保护；junction 源项和目标祖先拒绝；AI 非法 ID/分类原子拒绝；接收默认复制与移动日志；损坏、外部与签名被修改的日志；中断移动原件尚存；相同内容但不同身份的替换；未校验暂存保留；已验证撤销暂存续作；未验证撤销暂存保留；Windows 快捷方式创建与撤销。

WPF 检查覆盖：打开不扫描/写入；扫描不调用 AI/写目标；实际 540px 窗口的表格不重叠且撤销区可滚动到；AI 请求字段只含 ID/名称；关闭取消进行中的 AI。尺寸回归曾在修复前以“Preview overlaps execution controls”失败，修复后通过。模型委托完全为本地测试桩。

## 限制

- 仅 Windows；依赖 DPAPI、原生文件句柄及 WScript.Shell 快捷方式 COM。未运行真实远程模型或模拟 COM 不可用环境。
- 非递归整理，最多扫描 500 项；历史列表最多显示最近 100 个日志。撤销不清理创建的空分类目录。
- 不自动删除无法证明身份/完整性的中断暂存文件；需人工检查。签名密钥缺失、损坏或无法解密时拒绝自动恢复。
- 恢复边界通过重新签名的测试沙箱日志模拟，不等同于实际断电、介质故障、网络盘或卷突然移除测试。
- 路径检查及锁定文件身份防止常见链接与替换问题；未声称防御有权限恶意并发替换祖先目录的完整文件系统攻击模型。窗口初始尺寸依据主屏工作区，尚未验证跨显示器实时 DPI/工作区切换。
- 未生成代码覆盖率百分比；以上是通过的行为场景，不表示穷尽所有异常路径。
