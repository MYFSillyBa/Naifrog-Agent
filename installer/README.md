# 奶蛙Agent Windows 安装器

`Naifrog.iss` 使用 Inno Setup 6 编译，固定 AppId 用于覆盖升级。安装器包含自包含 Windows x64 程序、动画与来源声明，不需要用户另装 .NET。

在仓库根目录执行：

```powershell
.\build-installer.ps1 -Version 1.0.0
```

需要 .NET 9 SDK 和 Inno Setup 6。编译器不在默认目录时传入 `-IsccPath 'C:\path\ISCC.exe'`。脚本仅发布程序和生成安装器，不运行应用测试、不启动安装器。

产物为 `output/installer/Naifrog-Agent-Setup-1.0.0-win-x64.exe` 和 `SHA256SUMS.txt`。发布时将它们上传到 GitHub Releases；不把生成的 EXE 提交进 Git。

默认安装位置为 `%LOCALAPPDATA%\Programs\Naifrog`，无须管理员权限。提供开始菜单入口和可选桌面快捷方式。安装器图标、程序图标和卸载入口均使用 `src/Naifrog/Assets/naifrog.ico`。

卸载保留 `%APPDATA%\Naifrog` 的用户设置、聊天与加密凭据；仅删除指向本安装目录的开机启动项。其他位置的便携版不受该项清理影响。升级前应结束正在执行的 Agent 任务并退出旧程序。

`Languages/ChineseSimplified.isl` 原样来自 Inno Setup 官方源码仓库的社区翻译目录（6.7.4 标签），保留其中的作者与许可声明：

https://github.com/jrsoftware/issrc/blob/is-6_7_4/Files/Languages/Unofficial/ChineseSimplified.isl

安装器尚未配置代码签名，不应在发布说明中声称有可信发行者签名。
