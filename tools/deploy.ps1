# 一键部署：仓库 out\Release -> 游戏 Mods\ADOFAIMacro
# 用法：pwsh -File D:\Projects\ADOFAIMacro\tools\deploy.ps1
# 幂等：可反复运行；游戏运行时也能复制（新文件下次启动生效，覆盖 dll 需先关游戏）。
$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot '..\out\Release'
$dst = 'D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\Mods\ADOFAIMacro'

if (-not (Test-Path $src)) { throw "源不存在: $src （先 dotnet build）" }

New-Item -ItemType Directory -Force -Path $dst | Out-Null
New-Item -ItemType Directory -Force -Path "$dst\Localization" | Out-Null

$files = @('ADOFAIMacro.dll','Info.json','InputSystem.dll','System.Memory.dll','TechniqueSimulator.dll')
foreach ($f in $files) {
    Copy-Item (Join-Path $src $f) (Join-Path $dst $f) -Force
}
Copy-Item "$src\Localization\zh-CN.json" "$dst\Localization\zh-CN.json" -Force
Copy-Item "$src\Localization\en-US.json" "$dst\Localization\en-US.json" -Force

Write-Host "=== 部署完成 -> $dst ===" -ForegroundColor Green
Get-ChildItem $dst -Recurse -File |
    Select-Object @{n='RelPath';e={$_.FullName.Substring($dst.Length+1)}}, Length, LastWriteTime |
    Format-Table -AutoSize | Out-String | Write-Host
