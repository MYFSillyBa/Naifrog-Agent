param([string]$Executable)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AgentPickerClick {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
}
'@
$project = Split-Path $PSScriptRoot -Parent
$profileDir = Join-Path $project ('output\agent-ui-' + [guid]::NewGuid().ToString('N'))
$fixtureDir = Join-Path $profileDir 'workspace'
New-Item -ItemType Directory -Path $fixtureDir -Force | Out-Null
[System.IO.File]::WriteAllText((Join-Path $fixtureDir 'sample.txt'),'before',[System.Text.UTF8Encoding]::new($false))
if (-not $Executable) { $Executable = Join-Path $project 'src\Naifrog\bin\Release\net9.0-windows\Naifrog.exe' }
$port = [int](Get-Content -LiteralPath (Join-Path $project 'output\agent-ui-port.txt'))
$process = Start-Process -FilePath $Executable -ArgumentList @('--data-dir',('"'+$profileDir+'"'),'--settings') -WindowStyle Hidden -PassThru
$process.WaitForInputIdle(10000) | Out-Null
$script:condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
function Elements {
    for($attempt=0;$attempt -lt 4;$attempt++) {
        try { return [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,$script:condition) }
        catch { if($process.HasExited){throw 'Fixture application exited unexpectedly'}; if($attempt -eq 3){throw}; Start-Sleep -Milliseconds 100 }
    }
}
function Find([string]$name,[System.Windows.Automation.ControlType]$type) { @(Elements | Where-Object { $_.Current.Name -eq $name -and $_.Current.ControlType -eq $type }) | Select-Object -First 1 }
function Wait-For([scriptblock]$check,[string]$message) {
    $until=[datetime]::UtcNow.AddSeconds(25)
    do { if (& $check) { return }; Start-Sleep -Milliseconds 150 } while ([datetime]::UtcNow -lt $until)
    throw $message
}
function Button([string]$name) { $c=Find $name ([System.Windows.Automation.ControlType]::Button); if(-not $c){throw "Missing button $name"}; $c.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Text([string]$name,[string]$value) { $c=Find $name ([System.Windows.Automation.ControlType]::Edit); if(-not $c){throw "Missing field $name"}; $c.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Page([string]$name) { (Find $name ([System.Windows.Automation.ControlType]::ListItem)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Choose-Model([string]$name) {
    $currentPicker = Find '工作台模型' ([System.Windows.Automation.ControlType]::ComboBox)
    $currentPicker.SetFocus()
    $currentPicker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Wait-For { $candidate=Find $name ([System.Windows.Automation.ControlType]::ListItem); $candidate -and $candidate.Current.BoundingRectangle.Width -gt 0 -and $candidate.Current.BoundingRectangle.Height -gt 0 } 'Model entry not visible'
    $entry = Find $name ([System.Windows.Automation.ControlType]::ListItem)
    $bounds = $entry.Current.BoundingRectangle
    if($bounds.Width -le 0 -or $bounds.Height -le 0){throw 'Model entry not visible'}
    [void][AgentPickerClick]::SetCursorPos([int]($bounds.X+$bounds.Width/2),[int]($bounds.Y+$bounds.Height/2))
    [AgentPickerClick]::mouse_event(0x2,0,0,0,[UIntPtr]::Zero); [AgentPickerClick]::mouse_event(0x4,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 150
}
function Log {
    $values = @(Elements | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit } | ForEach-Object {
        try { $_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { '' }
    })
    return ($values -join "`n")
}
try {
    Wait-For { Find '奶蛙设置' ([System.Windows.Automation.ControlType]::Window) } 'Settings not shown'
    Page '模型与聊天'
    Text 'API 地址' "http://127.0.0.1:$port/v1"
    Text '模型名称 / Model ID' 'agent-ui-fixture'
    Text 'API Key' 'FAKE-AGENT-UI'
    Button '获取模型'
    Wait-For { @(Elements | Where-Object { $_.Current.Name.StartsWith('已获取 3 个模型') }).Count -gt 0 } 'Settings catalog did not load'
    Button '保存并应用'
    Page '工程 Agent'
    Button '保存并打开工程工作台'
    Wait-For { Find '工作区目录' ([System.Windows.Automation.ControlType]::Edit) } 'Workbench missing'
    (Find '奶蛙设置' ([System.Windows.Automation.ControlType]::Window)).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Button '刷新模型列表'
    Wait-For { @(Elements | Where-Object { $_.Current.Name.StartsWith('已获取 3 个模型') }).Count -gt 0 } 'Workbench catalog did not load'
    $picker = Find '工作台模型' ([System.Windows.Automation.ControlType]::ComboBox)
    $picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Wait-For { Find 'agent-ui-alternate' ([System.Windows.Automation.ControlType]::ListItem) } 'All returned models not selectable'
    Choose-Model 'agent-ui-alternate'
    $picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    if((Get-Content -LiteralPath (Join-Path $profileDir 'settings.json') -Raw | ConvertFrom-Json).Model.ModelId -ne 'agent-ui-alternate'){throw 'Selected model not saved'}
    $picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Choose-Model 'agent-ui-fixture'
    $picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    Text '工作区目录' $fixtureDir
    Text '工程任务输入' '读取 sample.txt，将 before 精确替换为 after，然后运行命令验证。'
    Button '发送'
    Wait-For { Find '允许此命令？' ([System.Windows.Automation.ControlType]::Window) } 'Command approval missing'
    if ((Get-Content -LiteralPath (Join-Path $fixtureDir 'sample.txt') -Raw) -ne 'after') { throw 'File tool did not modify fixture' }
    Text '工程任务输入' 'USER_REFERENCE_UI 请把这个补充作为引用，继续原任务。'
    Button '补充引用'
    Wait-For { @(Elements | Where-Object { $_.Current.Name -eq '补充引用 · 待引用' }).Count -gt 0 } 'Pending reference not visible'
    Button '允许执行'
    Wait-For { (Log).Contains('补充引用已纳入，原任务继续完成。') } 'Reference not delivered to ongoing task'
    $commandCard = @(Elements | Where-Object { $_.Current.Name.StartsWith('执行命令') -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Group }) | Select-Object -First 1
    if(-not $commandCard){throw 'Readable command card missing'}
    $commandCard.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $changesButton = @(Elements | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Current.Name.Contains('1 个文件') }) | Select-Object -First 1
    if (-not $changesButton) { throw 'Change count summary missing' }
    $changesButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { Find 'sample.txt' ([System.Windows.Automation.ControlType]::ListItem) } 'Selectable changed file missing'
    (Find 'sample.txt' ([System.Windows.Automation.ControlType]::ListItem)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $conversation = @(Elements | Where-Object { $_.Current.Name -eq 'Agent 对话' }) | Select-Object -First 1
    if($conversation){try {$conversation.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1,100)}catch{}}
    $window=Find '奶蛙 · 工程工作台' ([System.Windows.Automation.ControlType]::Window)
    $fixtureChat=Find '和奶蛙聊天' ([System.Windows.Automation.ControlType]::Window)
    if($fixtureChat){$fixtureChat.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()}
    $window.SetFocus()
    Start-Sleep -Milliseconds 600
    $bounds=$window.Current.BoundingRectangle
    $bitmap=[System.Drawing.Bitmap]::new([int]$bounds.Width,[int]$bounds.Height)
    $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
    try { $graphics.CopyFromScreen([int]$bounds.X,[int]$bounds.Y,0,0,$bitmap.Size); $bitmap.Save((Join-Path $project 'output\agent-workbench.png')) } finally { $graphics.Dispose(); $bitmap.Dispose() }
    Text '工程任务输入' 'CANCEL_AGENT_HTTP'
    Button '发送'
    Wait-For { (Find '停止' ([System.Windows.Automation.ControlType]::Button)).Current.IsEnabled } 'Stop not enabled'
    Start-Sleep -Milliseconds 350
    Button '停止'
    Wait-For { (Find '发送' ([System.Windows.Automation.ControlType]::Button)).Current.IsEnabled } 'Cancel did not release UI'
    $records=@(Get-ChildItem -LiteralPath (Join-Path $profileDir 'AgentTasks') -Filter '*.dpapi' -Recurse)
    if($records.Count -lt 1){ throw 'Encrypted task journal missing' }
    $requests=@(Get-Content -LiteralPath (Join-Path $project 'output\agent-ui-requests.jsonl') | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object {$_.model -eq 'agent-ui-fixture'})
    if($requests.Count -lt 5 -or @($requests | Where-Object {-not $_.fake_key_ok -or $_.tools -lt 9}).Count -gt 0){throw 'Custom model key or tools missing'}
    if (@($requests | Where-Object {$_.references.Count -gt 0}).Count -lt 1) { throw 'Supplemental reference missing in HTTP' }
    if((Get-Content -LiteralPath (Join-Path $profileDir 'settings.json') -Raw).Contains('FAKE-AGENT-UI')){throw 'Plaintext key in settings'}
    Write-Output "PASS: native UI model catalogs -> list/read/edit -> pending reference during command approval -> ongoing task receives reference -> green/red file changes -> continuation cancel -> encrypted journals. EXE=$Executable PROFILE=$profileDir"
}
finally {
    if(-not $process.HasExited){$process.CloseMainWindow() | Out-Null; if(-not $process.WaitForExit(5000)){$process.Kill()}}
}
