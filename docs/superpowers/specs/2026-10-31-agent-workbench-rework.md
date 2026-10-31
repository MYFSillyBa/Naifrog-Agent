# 工程工作台重做

用户明确要求六项修正：普通不置顶窗口；上下文不因本机字符阈值过早停止；右键直接进入；采用 Codex / DeepSeek Harness 的会话工作台格式；思考只显示正在思考，命令可展开，不展示总轮数/当前轮数；重新设计视觉。保持 C# 原生 WPF 和用户自配 API Key，不使用 Web UI，不移植 NaiWaChatPet UI。

## 已确认根因

原 AgentSession 按全部历史及工具 schema 的序列化字符数对比 MaxContextChars=160000，超过即退出，无压缩，无按模型窗口计量；并非用户 Key 额度反馈。MaxRounds=40 限制循环并直接显示技术日志。AgentWindow.Topmost 从桌宠继承，且 Owner=置顶 PetWindow，普通 Topmost=false 仍可能受 Owner 影响。

## 新设计

- 独立普通任务栏窗口，Owner 不绑定置顶桌宠，设置保存也不改变其 Topmost。右键新增「工程工作台」，托盘同步入口。
- 原生中性色会话布局：左侧任务列表；顶部工作区/模型；中间用户和助手消息；工具显示带状态的可展开命令/读取/搜索/修改卡片；底部输入区与停止/发送。配置收纳到权限区与设置页；文件改动使用独立侧面板。保留用户滚动和展开状态，更新不重建全部内容。
- 去除轮数进度。默认持续直到模型结束、用户停止、接口失败；兼容旧设置，不再沿用旧默认40次上限。思考显示通用状态，不展示私有推理链。
- 模型客户端兼容流式 SSE 和普通 JSON，自配模型 Key/端点保持快照。解析 usage，保留必要 reasoning_content 用于 DeepSeek 工具续接但不展示或落入主会话卡片。上下文错误与其它 HTTP 错误区分。
- 上下文窗口按可配置 token 预算估算并结合 usage 反馈，在接近窗口前裁剪旧工具内容和总结已完成历史。摘要使用相同模型，失败可退回确定性的操作摘要；保持完整当前目标、最近用户指令和完整 tool-call/result 配对，不重放已经执行的工具。服务拒绝上下文时，整理后仅重试模型请求，不能重复工具。历史记录与当前模型上下文分开。

## 研究依据

- https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/compaction/README.md ：自动摘要、工具结果裁剪、独立 token meter。
- https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/architecture.md ：持久会话事件与流式状态分离。
- https://developers.openai.com/codex/app-server#items ：userMessage、agentMessage、commandExecution、fileChange、contextCompaction。
- https://github.com/openai/codex/blob/main/codex-rs/core/src/compact.rs ：保留用户指令与摘要，历史替换和使用量重新计量。仅参考机制，不复制实现。

## 验证

先复现旧的长工具输出引起停止，新增跨多次压缩仍完成的模型 fixture；超上下文服务错误压缩重试且不重复工具；SSE 字节拆分、工具参数增量、usage 和取消；新窗口非 Topmost、非置顶 Owner、保存后仍普通窗口；右键第三项；实际 UI 截图验证会话卡片与展开的命令输出，滚动不抢用户位置。最终发布并更新旧启动目录。
