param([string]$Executable = '', [string]$TaskProfile = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PetUiMouse {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
}
'@
$project = Split-Path $PSScriptRoot -Parent
if (-not $Executable) { $Executable = Join-Path $project 'src\Naifrog\bin\Release\net9.0-windows\Naifrog.exe' }
if (-not $TaskProfile) { $TaskProfile = Join-Path $project ('output\ui-flow-' + [guid]::NewGuid().ToString('N')) }
$port = [int](Get-Content -LiteralPath (Join-Path $project 'output\ui-model-port.txt'))
$process = Start-Process -FilePath $Executable -ArgumentList @('--data-dir',('"'+$TaskProfile+'"'),'--settings') -WindowStyle Hidden -PassThru
$process.WaitForInputIdle(10000) | Out-Null
$script:processCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
function Elements { [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,$script:processCondition) }
function Find-Control([string]$name, [System.Windows.Automation.ControlType]$type, $parent = $null) {
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,$type))
    if ($null -eq $parent) { return @(Elements | Where-Object { $_.Current.Name -eq $name -and $_.Current.ControlType -eq $type }) | Select-Object -First 1 }
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Wait-For([scriptblock]$check,[string]$message) {
    $deadline = [datetime]::UtcNow.AddSeconds(12)
    do { if (& $check) { return }; Start-Sleep -Milliseconds 150 } while ([datetime]::UtcNow -lt $deadline)
    throw $message
}
function Invoke-Button([string]$name,$parent=$null) {
    $item = Find-Control $name ([System.Windows.Automation.ControlType]::Button) $parent
    if (-not $item) { throw "未找到按钮：$name" }
    $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-Text([string]$name,[string]$value,$parent=$null) {
    $item = Find-Control $name ([System.Windows.Automation.ControlType]::Edit) $parent
    if (-not $item) { throw "未找到输入框：$name" }
    $item.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
}
function Select-Page([string]$name) {
    $item = Find-Control $name ([System.Windows.Automation.ControlType]::ListItem)
    if(-not $item){ throw "未找到设置分类：$name" }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 150
}
function Capture-Window($window,[string]$name) {
    $window.SetFocus()
    Start-Sleep -Milliseconds 200
    $rect=$window.Current.BoundingRectangle
    $image=[System.Drawing.Bitmap]::new([int]$rect.Width,[int]$rect.Height)
    $graphics=[System.Drawing.Graphics]::FromImage($image)
    try { $graphics.CopyFromScreen([int]$rect.X,[int]$rect.Y,0,0,$image.Size); $image.Save((Join-Path $project ('output\'+$name))) }
    finally { $graphics.Dispose(); $image.Dispose() }
}
try {
    Wait-For { Find-Control '奶蛙设置' ([System.Windows.Automation.ControlType]::Window) } '设置窗口启动失败'
    $settings = Find-Control '奶蛙设置' ([System.Windows.Automation.ControlType]::Window)
    foreach($page in @('概览','外观与动作','模型与聊天','情绪与性格','陪伴与专注','屏幕与隐私','数据与高级','工程 Agent')) { Select-Page $page }
    Select-Page '模型与聊天'
    Set-Text 'API 地址' "http://127.0.0.1:$port/v1" $settings
    Set-Text '模型名称 / Model ID' 'ui-acceptance-model' $settings
    Set-Text 'API Key' 'FAKE-UI-ONLY' $settings
    Set-Text '聊天系统提示词' 'UI自定义提示词：请用中文简短回答。' $settings
    Invoke-Button '保存并测试连接' $settings
    Wait-For { @(Elements | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and $_.Current.Name.StartsWith('连接成功') }).Count -gt 0 } '真实设置页连接测试未成功'
    $saved = Get-Content -LiteralPath (Join-Path $TaskProfile 'settings.json') -Raw | ConvertFrom-Json
    if ($saved.Model.ModelId -ne 'ui-acceptance-model' -or $saved.Model.SystemPrompt -ne 'UI自定义提示词：请用中文简短回答。') { throw '模型或提示词未保存' }
    if ((Get-Content -LiteralPath (Join-Path $TaskProfile 'settings.json') -Raw).Contains('FAKE-UI-ONLY')) { throw '密钥被保存到明文配置' }
    Select-Page '概览'
    Select-Page '模型与聊天'
    Capture-Window $settings 'settings-model.png'
    Invoke-Button '打开聊天' $settings
    Wait-For { Find-Control '和奶蛙聊天' ([System.Windows.Automation.ControlType]::Window) } '聊天窗口未打开'
    $chat=Find-Control '和奶蛙聊天' ([System.Windows.Automation.ControlType]::Window)
    Set-Text '聊天输入' '你好，验证自定义接口。' $chat
    Invoke-Button '发送' $chat
    Wait-For { @(Elements | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and $_.Current.Name -eq '收到，你的 API 配置与自定义提示词已传到本机模拟服务。' }).Count -gt 0 } '聊天窗口没有显示完整模拟回复'
    Capture-Window $chat 'chat-acceptance.png'
    Wait-For { (Find-Control '发送' ([System.Windows.Automation.ControlType]::Button) $chat).Current.IsEnabled } '发送按钮未恢复'
    Set-Text '聊天输入' 'CANCEL_UI_TEST' $chat
    Invoke-Button '发送' $chat
    Start-Sleep -Milliseconds 450
    Invoke-Button '取消' $chat
    Wait-For { (Find-Control '发送' ([System.Windows.Automation.ControlType]::Button) $chat).Current.IsEnabled } '取消后未恢复输入'
    if(-not (Test-Path -LiteralPath (Join-Path $TaskProfile 'chat-memory.dpapi'))) { throw '聊天记忆未保存' }
    $requests = Get-Content -LiteralPath (Join-Path $project 'output\ui-model-requests.jsonl') | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.model -eq 'ui-acceptance-model' }
    $chatRequests = @($requests | Where-Object { $_.streaming })
    if ($chatRequests.Count -lt 2 -or $chatRequests[-2].system -ne 'UI自定义提示词：请用中文简短回答。' -or $chatRequests[-2].authorization -ne 'Bearer FAKE-UI-ONLY' -or $chatRequests[-2].path -ne '/v1/chat/completions') { throw '真实 HTTP 请求参数不符合用户配置' }
    $chat.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $pet=Find-Control '奶蛙' ([System.Windows.Automation.ControlType]::Window)
    $bounds=$pet.Current.BoundingRectangle
    [void][PetUiMouse]::SetCursorPos([int]($bounds.X+$bounds.Width/2),[int]($bounds.Y+$bounds.Height/2))
    [PetUiMouse]::mouse_event(0x8,0,0,0,[UIntPtr]::Zero)
    [PetUiMouse]::mouse_event(0x10,0,0,0,[UIntPtr]::Zero)
    Wait-For { @(Elements | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem }).Count -eq 3 } '角色右键菜单未出现三项'
    $menu = @(Elements | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem })
    if ($menu[0].Current.Name -ne '工程工作台' -or $menu[1].Current.Name -ne '设置' -or $menu[2].Current.Name -ne '退出') { throw '右键菜单内容不符' }
    $menu[2].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (-not $process.WaitForExit(5000)) { throw '退出未结束进程' }
    Write-Output 'PASS: eight settings pages, custom API/model/key/prompt saved, localhost connection test, full streaming UI response, cancellation, encrypted-memory file, actual HTTP parameters, right-click settings/exit and graceful exit.'
    Write-Output "TEST_PROFILE=$TaskProfile"
}
catch {
    $diagnostics = @(Elements | ForEach-Object { [pscustomobject]@{ Name=$_.Current.Name; Type=$_.Current.ControlType.ProgrammaticName } })
    $diagnostics | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $project 'output\ui-flow-error.json') -Encoding UTF8
    $failedWindow = Find-Control '奶蛙设置' ([System.Windows.Automation.ControlType]::Window)
    if($failedWindow) { Capture-Window $failedWindow 'ui-flow-error.png' }
    Write-Output "FAILED_PROFILE=$TaskProfile"
    throw
}
finally {
    if (-not $process.HasExited) {
        $pet=Find-Control '奶蛙' ([System.Windows.Automation.ControlType]::Window)
        if($pet){ $pet.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() }
    }
    if(-not $process.HasExited){ [void]$process.WaitForExit(5000) }
}
