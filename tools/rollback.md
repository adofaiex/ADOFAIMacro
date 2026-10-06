# 一键回退：手法模拟 DLL

## 背景

2026-10-07 部署的「片长对齐」修复（`a97a910`）消除了一类缺陷（同手同帧连按 1041→0），
但产生了**新的副作用**：每片恒 1 音 → `mult` 永不递增 → 整首只用 R/P 两个键、单键 20 次/秒。

经查用户 2026-08-26 的旧会话（`session-b14057f7`），这三种形态**都曾被用户明确否决**：

| 用户原话 | 对应本修复的副作用 |
|---|---|
| 「怎么还是单键啊 你到底怎么想的啊」 | 只用 R/P 两键 |
| 「一个按键大2021BPM 这么抽象？」 | 单键 20 次/秒 = 1200BPM |
| 「他的手法都没有变化 说明你的代码拟定有问题」 | TechniqueBpmLimit 完全失效 |

因此提供回退路径，由用户决定保留哪一版。

## 两个版本

| 版本 | ADOFAIMacro.dll | TechniqueSimulator.dll | 说明 |
|---|---|---|---|
| **旧版（用户原来玩的那版）** | `43B0925D…` | `6769472E…` | 有同手连击缺陷，但手法为多指分摊 |
| **新版（当前已部署）** | `C94FC02B…` | `FDC62FE4…` | 无同手连击，但只用两键、阈值失效 |

## 回退到旧版（在游戏**关闭**时执行）

```powershell
$mods = 'D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\Mods\ADOFAIMacro'
$bk   = "$env:TEMP\ADOFAIMacro_backup_20261007_040038"

# 1) 确认游戏已关闭（有进程会锁住 DLL，复制会失败）
if (Get-Process '*adofai*' -EA SilentlyContinue) { '★ 游戏正在运行，请先退出' ; exit 1 }

# 2) 备份当前（新版）状态，便于再切回来
$keep = "$env:TEMP\ADOFAIMacro_backup_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
New-Item -ItemType Directory -Path $keep -Force | Out-Null
Get-ChildItem $mods -Filter *.dll | Copy-Item -Destination $keep -Force

# 3) 还原旧版两个 DLL
Copy-Item "$bk\ADOFAIMacro.dll"        $mods -Force
Copy-Item "$bk\TechniqueSimulator.dll" $mods -Force

# 4) 删缓存（★ 必须；否则会命中旧轨迹，看不出变化）
Remove-Item "$mods\*.cache" -Force -EA SilentlyContinue
Remove-Item "$env:USERPROFILE\AppData\LocalLow\7th Beat Games\A Dance of Fire and Ice\ADOFAIMacro\trace\*.adotr" -Force -EA SilentlyContinue
#   ↑ 轨迹缓存也一并清掉：清空后游戏会按新算法重算（不删则可能回放旧解）

# 5) 核验哈希
Get-ChildItem $mods -Filter *.dll |
  ForEach-Object { "{0,-26} {1}" -f $_.Name, (Get-FileHash $_.FullName -Algorithm MD5).Hash.Substring(0,16) }
#   期望 ADOFAIMacro.dll = 43B0925DD95C5CB1
#        TechniqueSimulator.dll = 6769472EE6885C63
```

## 切回新版（当前状态）

新版备份在 `$env:TEMP\ADOFAIMacro_backup_20261007_044053`，
或按需重新构建（源码在 HEAD `7680bc8`）：

```powershell
Copy-Item "$env:TEMP\ADOFAIMacro_backup_20261007_044053\*.dll" $mods -Force
Remove-Item "$mods\*.cache" -Force -EA SilentlyContinue
```

## 备注

- **一定要删 `.cache`**：Mods 目录下的 `ADOFAIMacro.dll.*.cache` 是 UnityModManager 的
  程序集缓存，不删则新 DLL 不生效（这正是 10-06 那次「改了没生效」的原因之一）。
- **也建议清轨迹缓存**：`.adotr` 的指纹在本修复中已并入求解器哈希（FormatVersion 2→3），
  理论上不会误命中旧解；但手动清空更稳妥，代价只是首次进图多算 1~4ms。
- 两版导出表完全一致（`dumpbin /exports` 核对 7 个导出名/序号），
  切换不会导致 `GetProcAddress` 拿不到符号。