param(
    [ValidatePattern('^\d{1,5}\.\d{1,5}\.\d{1,5}(\.\d{1,5})?$')]
    [string]$Version = '1.0.0',
    [string]$IsccPath
)
$ErrorActionPreference = 'Stop'

if (-not $IsccPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $candidates = @(
        $(if ($compiler) { $compiler.Source }),
        (Join-Path $env:LOCALAPPDATA 'NaifrogBuildTools\InnoSetup6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw '请先安装 Inno Setup 6，或通过 -IsccPath 指定 ISCC.exe。https://jrsoftware.org/isdl.php'
}

$projectPath = Join-Path $PSScriptRoot 'src\Naifrog\Naifrog.csproj'
$payloadPath = Join-Path $PSScriptRoot 'output\installer-payload'
$setupOutputPath = Join-Path $PSScriptRoot 'output\installer'

# A clean payload prevents deleted resources from leaking into a later installer.
if (Test-Path -LiteralPath $payloadPath) {
    $resolvedPayload = (Resolve-Path -LiteralPath $payloadPath).ProviderPath
    $expectedPayload = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'output\installer-payload'))
    $payloadItem = Get-Item -LiteralPath $payloadPath -Force
    if ($resolvedPayload -ne $expectedPayload -or
        ($payloadItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '安装负载路径不安全，已停止清理。'
    }
    Remove-Item -LiteralPath $resolvedPayload -Recurse -Force
}

dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    "-p:Version=$Version" -o $payloadPath --nologo
if ($LASTEXITCODE -ne 0) { throw '生成自包含程序失败。' }

& $IsccPath '/Qp' "/DAppVersion=$Version" "/DPayloadDir=$payloadPath" `
    "/DInstallerOutputDir=$setupOutputPath" (Join-Path $PSScriptRoot 'installer\Naifrog.iss')
if ($LASTEXITCODE -ne 0) { throw '编译 Setup 安装程序失败。' }

$setupPath = Join-Path $setupOutputPath "Naifrog-Agent-Setup-$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw '未找到生成的 Setup。' }
$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $setupOutputPath 'SHA256SUMS.txt'),
    "$hash  $([IO.Path]::GetFileName($setupPath))`n", [Text.UTF8Encoding]::new($false))
Write-Host "安装程序：$setupPath"
Write-Host "SHA256：$hash"
