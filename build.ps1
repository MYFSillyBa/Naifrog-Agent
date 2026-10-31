param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'src\Naifrog\Naifrog.csproj'
$publishPath = Join-Path $PSScriptRoot 'output\Naifrog'
$contained = if ($SelfContained) { 'true' } else { 'false' }
dotnet restore $projectPath -r win-x64 -p:SelfContained=$contained
if ($LASTEXITCODE -ne 0) { throw '还原运行依赖失败，请检查上方输出。' }
dotnet clean $projectPath -c Release -r win-x64 --nologo
if ($LASTEXITCODE -ne 0) { throw '清理旧构建失败，请检查上方输出。' }
dotnet publish $projectPath -c Release -r win-x64 --self-contained $contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publishPath
if ($LASTEXITCODE -ne 0) { throw '构建失败，请检查上方输出。' }
$previewPath = Join-Path $PSScriptRoot 'output\publish-preview.png'
$smoke = Start-Process -FilePath (Join-Path $publishPath 'Naifrog.exe') -ArgumentList @('--render',('"'+$previewPath+'"')) -WindowStyle Hidden -PassThru -Wait
if ($smoke.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $previewPath)) { throw '发布程序资源检查失败。' }
Write-Host "程序已生成：$publishPath\Naifrog.exe"
