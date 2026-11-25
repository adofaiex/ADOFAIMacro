# 一键部署：仓库 ADOFAIMacro\bin\Release -> 游戏 Mods\ADOFAIMacro
# 用法：pwsh -File tools\deploy.ps1 [-Destination <目录>]
# 源是 Release 编译的直接输出（bin\Release）。不用 out\Release：本工程没有设置 PackageOutputPath，
# SDK 默认它等于 OutputPath（bin\Release），CopyNativeDlls 的打包复制因此写入 bin\Release，
# out\Release 不随普通 Release 构建更新；从那里部署会静默带入旧 DLL。
# 以下检查失败时，在改动游戏目录之前退出：
#   1. 六个源文件齐全；
#   2. ADOFAIMacro 下的 .cs/.csproj/.json、TriggerRs 下的 .rs/.toml，以及嵌入主 DLL 的
#      macro_trigger.dll，都不比编译产物 ADOFAIMacro.dll 新（否则先 dotnet build -c Release）；
#   3. 已存在的六个目标文件都能独占写入（游戏运行时会占用，先关游戏）。
# 不检查 C++ 原生源 InputSystem：改动原生库后需重新 dotnet build，让新 DLL 复制进 bin\Release。
# 复制后逐个核对 SHA256。检查 3 通过后若复制中途失败，目标可能只部分更新，核对时会报错；
# 重新运行即可补齐（幂等）。只覆盖上述六个文件，不删除目标目录中的其它文件（如 Settings.xml）。
param(
    [string]$Destination = 'D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\Mods\ADOFAIMacro'
)
$ErrorActionPreference = 'Stop'

$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$src = Join-Path $repo 'ADOFAIMacro\bin\Release'
$dst = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\')
$files = @(
    'ADOFAIMacro.dll',
    'Info.json',
    'InputSystem.dll',
    'System.Memory.dll',
    'Localization\zh-CN.json',
    'Localization\en-US.json'
)

foreach ($f in $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $src $f))) {
        throw "源缺少 $($f)：$($src)（先 dotnet build -c Release）"
    }
}

# 检查 2：任一输入比编译产物新，说明改动尚未编译。
$dll = Get-Item -LiteralPath (Join-Path $src 'ADOFAIMacro.dll')
$sources = @()
$sources += Get-ChildItem -LiteralPath (Join-Path $repo 'ADOFAIMacro') -Recurse -File -Include '*.cs', '*.csproj', '*.json' |
    Where-Object { $_.FullName -notlike '*\bin\*' -and $_.FullName -notlike '*\obj\*' }
$sources += Get-ChildItem -LiteralPath (Join-Path $repo 'TriggerRs') -Recurse -File -Include '*.rs', '*.toml' |
    Where-Object { $_.FullName -notlike '*\target\*' }
$rustDll = Join-Path $repo 'TriggerRs\target\release\macro_trigger.dll'
if (Test-Path -LiteralPath $rustDll) { $sources += Get-Item -LiteralPath $rustDll }
$newest = $sources | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newest -and $newest.LastWriteTime -gt $dll.LastWriteTime) {
    throw "$($newest.FullName) 比编译产物新（$($newest.LastWriteTime) > $($dll.LastWriteTime)）。请先 dotnet build -c Release 再部署。"
}

# 检查 3：已存在的目标文件都要能独占写入；任一不能，则整体不动。
foreach ($f in $files) {
    $p = Join-Path $dst $f
    if (Test-Path -LiteralPath $p) {
        try {
            $fs = [System.IO.File]::Open($p, 'Open', 'ReadWrite', 'None')
            $fs.Close()
        }
        catch {
            throw "目标无法独占写入，未做任何改动：$($p)（游戏运行中会占用文件，请先关闭游戏）。$($_.Exception.Message)"
        }
    }
}

New-Item -ItemType Directory -Force -Path (Join-Path $dst 'Localization') | Out-Null
foreach ($f in $files) {
    try {
        Copy-Item -LiteralPath (Join-Path $src $f) -Destination (Join-Path $dst $f) -Force
    }
    catch {
        throw "复制 $($f) 失败：$($_.Exception.Message)。目标可能部分更新，关闭游戏后重新运行即可补齐。"
    }
}

foreach ($f in $files) {
    $a = (Get-FileHash -LiteralPath (Join-Path $src $f) -Algorithm SHA256).Hash
    $b = (Get-FileHash -LiteralPath (Join-Path $dst $f) -Algorithm SHA256).Hash
    if ($a -ne $b) { throw "部署后哈希不一致：$($f)。关闭游戏后重新运行。" }
}

Write-Host "=== 部署完成 -> $($dst) ===" -ForegroundColor Green
Get-ChildItem -LiteralPath $dst -Recurse -File |
    Select-Object @{ n = 'RelPath'; e = { $_.FullName.Substring($dst.Length + 1) } }, Length, LastWriteTime |
    Format-Table -AutoSize | Out-String | Write-Host
