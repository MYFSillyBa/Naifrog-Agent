# 非游戏功能版验证记录

日期：2026-10-29，Asia/Shanghai。实现规格及过程记录见 docs/work/。

## 最终构建

通过 build.ps1 完成 win-x64 Release 发布，0 个警告、0 个错误。构建脚本先还原目标运行依赖、清理旧构建，再发布并启动 --render 检查内置角色资源，避免旧增量资源遗漏。

发布位置：output/Naifrog/Naifrog.exe；动画与许可证分别保存在同级 animations / licenses。资源包含 52 份清单、1299 张透明 PNG；所有帧已核对哈希并实际解码。

## 自动检查

| 模块 | 最终结果 |
|---|---|
| ChatChecks | 19 组通过 |
| AnimationChecks 完整资源包 | 208 项通过 |
| FileChecks | 18 项通过 |
| StoreChecks | 13 项通过 |
| IntegrationChecks | 22 项通过 |
| 整理窗口 WPF 检查 | 通过，包含 540px 高度布局、只读扫描、AI 取消 |

前五项合计 280 项/组行为检查，另有整理窗口检查。数值代表检查场景数量，不代表代码覆盖率。

覆盖的关键整合边界包括：快速回复表情不被等待动作清空、按表情冷却、禁用自动表情保留手动预览、普通配置保存不取消文字聊天、截图撤权取消图片请求、跨实例配置/Key 快照、保存失败回滚、清空/关闭/跨实例清空后的记忆及陪伴缓存、旧设置草稿合并、设置页保存后控件重建、初始位置与免打扰。

文件检查只操作自动创建的唯一临时目录，没有整理真实桌面文件。模型检查使用注入 handler 或本机模拟 HTTP，没有真实模型 Key、外网模型请求或实际桌面截图上传。

## 发布程序的真实 UI / HTTP 验收

在发布目录的 EXE 上，通过 Windows UI Automation 和本机 127.0.0.1 模拟服务完成：

1. 八个顶部分类均可切换。
2. 用户在实际输入框填写 API 地址、模型 ID、Key、系统提示词并保存。
3. 连接测试成功，实际 HTTP 路径为 /v1/chat/completions。
4. 实际请求使用所填模型、Bearer Key 和用户系统提示词。
5. 流式正文完整出现在聊天窗口。
6. 取消等待中的回复后恢复输入；加密记忆文件存在，普通设置不含明文 Key。
7. 真实鼠标右键角色只显示设置与退出两项，点击退出正常结束进程。

最终输出：PASS: eight settings pages, custom API/model/key/prompt saved, localhost connection test, full streaming UI response, cancellation, encrypted-memory file, actual HTTP parameters, right-click settings/exit and graceful exit.

界面截图：output/settings-model.png、output/chat-acceptance.png。

## 审查

聊天、动画、整合审查发现的 P1/P2 均已处理，复审记录见 docs/work/*review.md。额外补充了跨实例记忆清空以及图片请求发出前再次校验授权的回归。

## 实际边界

- 用户需自行配置兼容 chat/completions 的模型服务；未验证具体供应商额度、认证、图像能力或私有扩展参数。
- 动画使用透明序列帧和整图二维变换，不是可旋转三维模型，也没有实现局部四肢骨骼蒙皮。
- 整理为顶层最多 500 项；目录仅支持引用/快捷方式。
- 未声称完成混合 DPI、多显示器热插拔、实际断电恢复及长期负载测试。
- 默认关闭模型情绪分析、自动模型陪伴和屏幕上传；屏幕授权不会在重启后自动恢复。

界面是自行设计的 WPF 布局与样式，采用顶部分类、「奶蛙的小屋」主题及独立聊天版式；没有使用参考项目的设置网页或游戏界面代码。
