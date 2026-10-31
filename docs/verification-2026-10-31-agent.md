# 工程 Agent 验证记录

交付日期：2026-10-31（Asia/Shanghai）。C# / .NET 9 / 原生 WPF，无 Web UI，无新增运行时包。

## 验证结果

| 检查 | 本次结果 |
|---|---:|
| WorkspaceChecks | 46 通过 |
| AgentChecks | 12 组通过 |
| AgentUiChecks | 7 项通过 |
| ChatChecks | 19 组通过 |
| StoreChecks | 13 组通过 |
| IntegrationChecks | 22 项通过（设置分类更新为 9 项） |
| FileChecks | 18 项通过 |
| AnimationChecks | 208 项通过，52 片段、1299 帧 |
| Organizer WPF WindowChecks | 通过 |
| 发布版原生 UI + 本机 HTTP 模拟 | 通过 |
| 发布版内嵌图像资源渲染 | 通过 |

所有文件操作测试使用新建临时目录；模型请求使用注入的 HTTP handler 或仅绑定 127.0.0.1 的模拟服务，没有调用用户真实 API 或修改用户工程。

## 端到端证据

`tests/AgentChecks` 驱动真正的 C# `Add-Type` 编译：模型请求写入错误代码，运行编译获得失败码 7；错误作为工具结果返回后再次写入修复代码，真实编译返回 0，随后模型结束。验证继续对话、独立任务、重叠工作区互斥、三任务上限、每次运行的模型/密钥快照、加密记录、截断和取消。

`tests/agent-ui-server.py` + `tests/agent-ui-flow.ps1` 在发布 EXE 上执行：设置用户自填地址/模型/假 Key，进入工程工作台，列目录、读取文件及哈希、精确替换、原生命令审批、真实 PowerShell 读取 `after`、工具结果回传、模型结束、继续任务后取消、检查加密记录。截图：`output/agent-workbench.png`。配置和文件位于独立测试 profile。

## 审查修复

- 切换任务时恢复该任务的真实编辑和终端授权，避免控件显示与执行权限不一致。
- Git 只读工具限定工作区 pathspec，关闭外部 diff、textconv 和 fsmonitor；处理嵌套仓库工作区。
- 上下文溢出或中断时，已执行工具保留真实标记，避免后续继续任务误以为未执行。
- 最终记录保存完成后才释放任务和工作区锁。
- 中文 stdout/stderr 显式使用 UTF-8；取消通过同步回调终止进程树。
- 文件句柄检查防目录链接、硬链接及路径替换；中断写入保存实际字节快照，冲突撤销保留用户后续修改。

## 发布

先发布 `output/Naifrog-Agent/Naifrog.exe` 并完成真实 UI 验证，再更新常用 `output/Naifrog/Naifrog.exe`。两个 EXE SHA-256 相同：

`C14693D1DC437B44DD399E4883D3D4FD9F407D06313040F42E8EC50F5FDD792E`

保留 EXE 同级的 animations 和 licenses。

## 使用边界

使用用户配置的兼容 chat/completions 工具调用模型，普通聊天成功不表示支持 tools。终端具有当前 Windows 用户权限，不是沙箱；默认逐条审批，明确选择后可本任务自动执行。构建、Git 和测试依赖本机已安装工具。

文件工具处理 UTF-8 文本，单文件最多 1 MiB。撤销仅覆盖文件工具快照，不包含终端修改；冲突不强行还原。并行/重叠目录互斥在同一程序进程内生效。重启后可查看加密记录，暂不恢复任务执行；普通取消不自动回滚。终端非交互、输出有上限，用户命令自行脱离的进程不纳入后台作业托管。未集成 Codex 私有实现或其云端能力，模型最终回复不代替人工检查实际工程行为。
