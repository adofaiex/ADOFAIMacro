# ADOFAIMacro

[![License](https://img.shields.io/github/license/adofaiex/ADOFAIMacro?color=blue)](LICENSE.txt)
[![Downloads](https://img.shields.io/github/downloads/adofaiex/ADOFAIMacro/total)](https://github.com/adofaiex/ADOFAIMacro/releases)
[![C# 12.0](https://img.shields.io/badge/C%23-12.0-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![.NET Framework 4.8.1](https://img.shields.io/badge/.NET%20Framework-4.8.1-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet-framework)

[中文说明](README.md)

**ADOFAIMacro** is a UnityModManager mod for **A Dance of Fire and Ice (ADOFAI)**: it parses level floor timestamps and fires keys with microsecond-precision timing from a dedicated high-priority thread. From "direct judge triggering" to "system-level key simulation" to "two-hand technique simulation", it covers everything from fully automated clears to realistic hand-play styles.

> ⚠️ **Two hard rules, up front:**
> - **Do not modify `Info.json`** — the mod ships with tamper detection; modifying it makes the game quit on enable.
> - **Incompatible with BaseMacro** — detection causes the game to quit.

---

## Table of Contents

- [Feature Overview](#feature-overview)
- [Quick Start](#quick-start)
- [Trigger Modes & Input Paths](#trigger-modes--input-paths)
- [Settings Reference](#settings-reference)
- [Technique Simulation Guide](#technique-simulation-guide)
- [In-Game Hotkeys](#in-game-hotkeys)
- [Troubleshooting](#troubleshooting)
- [Building (Developers)](#building-developers)
- [Project Structure](#project-structure)
- [License & Related Projects](#license--related-projects)

---

## Feature Overview

- **Auto triggering**: level timestamps → precision-timed key events; double-buffered time anchors sync the main and worker threads, lock-free with zero waiting.
- **Four input paths**: direct judge / SendInput / SkyHook low-level injection (NtInject / NtSendInput) / virtual async keyboard direct-feed (bypasses system injection, sub-microsecond jitter).
- **Technique simulation**: left/right hand alternation, automatic time-slice subdivision above a BPM threshold (multi-finger single-hand streams), configurable keys / orders / press durations, speed segments, per-level configs; the core algorithm runs in a native C++ DLL.
- **Timing fine-tuning**: ±100 ms offset, adjustable in-game with arrow keys; automatic judge-error calibration (closed-loop, cancels speed-section offsets).
- **Key filtering**: blacklist / whitelist, independent sync (KeyCode) and async (VK code) filters.
- **Extras**: auto keypress on death, GC pause suppression during play, out-of-focus guard, English/Chinese UI.

---

## Quick Start

**Prerequisites**: ADOFAI installed, [UnityModManager](https://www.nexusmods.com/site/mods/21) (≥ 0.27.0).

1. Download the archive from [Releases](https://github.com/adofaiex/ADOFAIMacro/releases) and install via UMM, or manually extract it into the game's `Mods/ADOFAIMacro/` folder.
   - The folder should contain: `ADOFAIMacro.dll`, `InputSystem.dll`, `TechniqueSimulator.dll`, and `Localization/` (zh-CN / en-US).
2. Launch the game, open the UMM mod window, and enable **ADOFAIMacro**.
3. In the **Macro** tab, check **Enable Macro**.
4. The defaults (key simulation off = direct judge) will auto-play the level. If hits feel consistently early/late, adjust **Offset (ms)** in the **Offset Settings** tab, or tap <kbd>←</kbd>/<kbd>→</kbd> in-game.

That's it. For more, read on.

---

## Trigger Modes & Input Paths

Three toggles in **Key Settings** combine into four paths:

| What you want | Configuration |
|---|---|
| Just clear the level, shortest path | Key simulation **off** |
| Real system keys (visuals, key sounds, third-party tools reading the keyboard) | Key simulation **on** + Use advanced input **off** (SendInput) |
| High-frequency charts / multi-software setups / SendInput blocked | Key simulation **on** + Use advanced input **on** + Input Mode **Auto** |
| Maximum sync precision | The row above + **Virtual async keyboard** on (default) |

Path details:

1. **Direct judge** (`SimulateKeyPress = false`): the worker thread counts hits → the main thread calls the game's `Hit()`. Zero system side effects and the most predictable latency, but the game treats it as an automatic hit — no real keypresses are produced.
2. **SendInput**: standard Win32 injection, best compatibility.
3. **SkyHook advanced input**: goes through `InputSystem.dll` to lower layers. Input Mode options:
   - **Auto**: picks the lowest available layer automatically (start here);
   - **NtInject**: deepest layer, injects into the raw input stream;
   - **NtSendInput ★**: kernel-boundary injection;
   - **SendInput**: same as standard Win32.
4. **Virtual async keyboard**: synthesizes key events and feeds them straight into the game's own input queue (the same entry point as the real keyboard hook), skipping the entire system-injection chain. Timestamps are generated locally with sub-microsecond jitter and no window-focus dependency. When unavailable (unmapped key, layout check failure) it **automatically falls back** to injection — no manual action needed.

---

## Settings Reference

Organized by UMM panel tab. "Internal name" is the field used in the config file.

### Macro

| Panel item | Internal name | Default | Description |
|---|---|---|---|
| Enable Macro | `Macro` | off | Master switch. |

### Key Settings

| Panel item | Internal name | Default | Description |
|---|---|---|---|
| Keys (comma separated) | `MacroKeys` | `D,F,J,K` | Key sequence for simple rotation mode. |
| Key simulation | `SimulateKeyPress` | off | Off = direct judge; on = system key simulation. |
| Use advanced input | `SkyHookMode` | off | On = SkyHook path; off = SendInput. |
| Virtual async keyboard | `UseVirtualAsyncInput` | on | See path 4 above; auto-fallback when unavailable. |
| Mirror virtual keys to system input | `MirrorVirtualKeys` | on | After a successful direct-feed, also injects one real keypress so key-viewer tools can see virtual keys; injection echoes are dropped automatically — no double hits. |
| Win API Input Mode | `InputMode` | Auto | Auto / NtInject / NtSendInput ★ / SendInput. |

### Offset Settings

| Panel item | Internal name | Default | Description |
|---|---|---|---|
| Offset (ms) | `TimeOffset` | 0 | Trigger time offset, range −100 ~ 100. |
| Adjust Step | `AdjustStep` | 1 | Offset change per in-game adjustment, 0.1 ~ 10. |
| Allow adjustment of delay using left and right keys (in-game) | `EnableArrowTimeAdjust` | on | <kbd>←</kbd>/<kbd>→</kbd> adjust the offset directly. |
| Allow adjusting step offset using Ctrl and arrow keys (in-game) | `EnableKeyAdjust` | on | <kbd>Ctrl</kbd>+<kbd>←</kbd>/<kbd>→</kbd> adjust the step. |
| Enable High Precision Time | `HighPrecisionTime` | off | Switches to a more precise clock source. |
| [Experimental] Enable High Precision Async | `HighPrecisionAsync` | off | Experimental; leave off unless investigating issues. |
| Auto judge-error calibration | `AutoCalibrateJudgement` | on | Closed loop: compensates the offset from actual judge errors (including speed sections); re-converges every run. |

### Key Filter

| Panel item | Internal name | Default | Description |
|---|---|---|---|
| Enable Key Filter | `EnableKeyFilter` | off | Filter master switch. |
| Filter Mode | `FilterMode` | Blacklist | Blacklist = block listed keys; whitelist = allow only listed keys. |
| Keys (comma separated) | `FilteredKeys` | `F1,F2,F3,F4` | Sync input filter list. |
| Async Keys (comma separated) | `FilteredAsyncKeys` | empty | Async input filter (requires advanced input / SkyHook mode). |

### Other Settings

| Panel item | Internal name | Default | Description |
|---|---|---|---|
| Suppress GC pauses during play | `SuppressGcPauses` | off | Removes GC-induced error spikes on dense charts (GC happens during loading instead). |
| Auto-press key on death | `EnableDeathKey` | off | **Advanced input (SkyHook) mode only.** |
| Delay (seconds) | `DeathKeyDelay` | 5 | Seconds to wait after death, 0.1 ~ 30. |
| Key | `DeathKeyInput` | `R` | Key pressed on death; accepts names (SPACE, ENTER…) or virtual-key codes (0x52). |
| The game allows switching to failure mode | `ChangeNoFaillInPlay` | off | Unlock NoFail switching during play. |
| Switching Judgement is allowed in the game | `ChangeJudementInPlay` | off | Unlock judgement switching during play. |
| Lock Level Editor | `LockLevelEditor` | off | Prevents accidental edits. |
| Block key input when window is unfocused | `BlockInputWhenUnfocused` | on | Skip key sending while unfocused (the worker thread keeps running and resumes on focus). |

### Key name format (applies everywhere)

- Names: `A`–`Z`, `0`–`9`, `F1`–`F12`, `SPACE`, `ENTER`, `ESC`, `TAB`, `SHIFT`, `CTRL`, `ALT`, arrows (`UP`/`DOWN`/`LEFT`/`RIGHT`), etc.;
- Hex virtual-key codes: e.g. `0x41`;
- Separate multiple keys with commas, e.g. `J,K,L`.

---

## Technique Simulation Guide

When enabled (requires **Key simulation**), the macro no longer simply rotates the key sequence; instead, it simulates two human hands. Time is divided into “slices”: at or below the **Speed Threshold**, each note is one slice and the **starting (main) hand** keeps tapping; above the threshold, slices are subdivided and handled alternately by the left and right hands. After a fast section, entering a slow section automatically returns to the main hand.

> The actual rate is derived from note intervals (notes/minute), not tile BPM: straight tiles ×1, 90° tiles (half-beat) ×2, 45° tiles ×4. SetSpeed changes are reflected by the actual intervals as well.

### Basic parameters

| Panel item | Default | Description |
|---|---|---|
| Enable Technique Simulation (L/R alternation) | off | Master switch; requires **Key simulation**. |
| Starting Hand | Right | Main hand for slow sections / each slice. |
| Global · Speed Threshold (BPM) | 500 | Subdivide slices when the actual note rate exceeds this value (50 ~ 2000). |
| L/R Keys | `D,F` / `J,K` | Keys available to each hand; presets DF/JK, DS/JK, ASDF/JKL. |
| L/R Order | empty | See format below; empty = default rotation. |
| L/R Ratio | `0.8,0.8` | Press-duration ratio (0 ~ 1). |
| Speed Change Tolerance | 0 | Auto-adjusts BPM to align slices with event timing. 0 = off, 0.2 = moderate, 0.5 = aggressive. |
| Balance multi-press across hands | off | Multi-press defaults to the main hand filling all its keys, with the rest to the other hand; on = split evenly. |

**Order format**: use `|` to separate groups with different key counts, and commas to separate 1-based key indices. Example `1,2 | 1,2 | 1,2,1`: one-key slices rotate keys 1 and 2; three-key slices play 1→2→1. Empty = default order.

### Slice rules

- Slice length is based on the **actual note interval**: the time gap to the next note after skipping same-time doubles/chords; cuts snap to event gaps.
- At or below the threshold: one note per slice, single-finger tapping by that hand.
- Above the threshold: the finger count is `n = ceil(note rate / threshold)`, where the threshold is the single-finger tap limit. Each slice is handled by one hand taking half of those `n` notes: when `n` is even, both hands take `n/2` notes alternately; when `n` is odd, the main hand takes the extra finger. If a slice has more events than that hand has keys, all its keys are used.
- Multi-press: when one moment has more notes than a hand has keys, the main hand fills all its keys by default and the rest goes to the other hand; with **Balance multi-press across hands** enabled, notes are split evenly across both hands, with an odd extra note to the main hand.
- “One moment” is detected adaptively: consecutive events with internal gaps ≤12 ms and a total span ≤35 ms count as one multi-press.

### Hold time

Hold time = `max(the note's actual interval × L/R duration ratio, 50 ms)`, capped by “this slice's note span + smallest internal gap (skipping ≤12 ms gaps inside a cluster)”. Inside tight clusters it falls back to the gap outside the cluster; same-time doubles/chords share one base, and a hold is never shorter than one frame.

### Profiles

Save multiple complete technique parameter sets (keys, orders, durations, starting hand, speed-change tolerance, segments); create / delete / switch from the panel.

### Speed Segments

Override global settings within a floor range: each segment can set its own **BPM threshold** and **L/R keys / orders / durations** (empty fields inherit global). At segment boundaries, hand order resets and cross-segment holds are released.

### Level-specific Configs

Each level can have its own config:

- Stored next to the level file, named `LevelName.adofaimacro.json`;
- Auto-loaded on entry (the “Auto-load from level folder” toggle, on by default);
- Load / Save to level folder / Delete from the bottom of the panel, with current status display.

Useful for smaller key sets on high-BPM sections, custom key orders for specific patterns, or per-level fine-tuning.

### Notes

- **The first time you enter the game, you need to die once to calibrate timing** (same note as in the panel).
- The core algorithm runs in the native `TechniqueSimulator.dll`; make sure it is in `Mods/ADOFAIMacro/`. **Release builds produce no technique simulation output without the DLL** (debug builds can fall back to the C# implementation).

---

## In-Game Hotkeys

| Keys | Action | Condition |
|---|---|---|
| <kbd>←</kbd> / <kbd>→</kbd> | Offset ± step | Arrow adjust enabled |
| <kbd>Ctrl</kbd> + <kbd>←</kbd> / <kbd>→</kbd> | Step ±0.1 | Ctrl adjust enabled |

Test on short levels first, then move to long / dense charts.

---

## Troubleshooting

**Q1: Macro enabled but nothing happens**
Check in order: mod enabled in UMM → Enable Macro checked → key sequence valid (comma-separated) → if "Block key input when window is unfocused" is on, keys are skipped while the game is unfocused (resume by focusing it).

**Q2: Timing inconsistent, occasional drops**
Fine-tune the offset with arrow keys (1 ms steps); for high-frequency charts enable advanced input + Auto mode; for dense charts enable GC pause suppression; if still conflicting, enable the key filter to isolate the source.

**Q3: Large offsets on speed-change charts**
Make sure auto judge-error calibration is on (default); under technique simulation, raise Speed Change Tolerance or give the speed section its own BPM limit via segments.

**Q4: Death key doesn't work**
Advanced input (SkyHook) mode only — confirm it's on; confirm the death key is enabled and the key name / code is valid; increase the delay if needed.

**Q5: Key-viewer tools can't see macro keys**
The virtual async keyboard bypasses the system input stream, so third-party tools can't read it — enable "Mirror virtual keys to system input" (on by default).

**Q6: Key filter "does nothing"**
Confirm the filter is enabled; confirm the mode (black/whitelist) matches your intent; under advanced input, don't forget the **async** key list.

**Q7: The game quits right after enabling the mod**
`Info.json` was modified (tamper detection) — restore it; or BaseMacro is installed (mutually exclusive) — remove it.

**Q8: Technique simulation produces nothing / DLL unavailable**
Place `TechniqueSimulator.dll` in `Mods/ADOFAIMacro/`; Release builds generate no technique events without the DLL.

**Q9: Judgement drifts when first entering the game / need to die once?**
Yes. The first entry requires one death to complete time calibration; after that it's normal.

When filing an issue, please include: game version, mod version, key settings screenshots, whether SkyHook is used and the current input mode, reproduction steps and logs.

---

## Building (Developers)

- Environment: Visual Studio 2022 (or MSBuild), .NET Framework 4.8.1, C# 12.
- Open `ADOFAIMacro-Dev.csproj` (or `ADOFAIMacro-Dev.slnx`) and point the `HintPath`s at your local ADOFAI install — you need the game's `Assembly-CSharp.dll`, `UnityEngine*.dll`, `SkyHook.Unity.dll`, `UnityModManager.dll`, plus `Newtonsoft.Json.dll` (used by the localization system; get it from the game folder or NuGet).
- Restore NuGet packages (packages.config style) and build Release.
- Deploy to `Mods/ADOFAIMacro/`: `ADOFAIMacro.dll` + `Localization/` + `InputSystem.dll` + `TechniqueSimulator.dll`.

---

## Project Structure

```text
ADOFAIMacro/
├─ Main.cs                      # Entry point: mod lifecycle, DLL loading, tamper detection
├─ Settings.cs                  # Settings + UMM panel UI
├─ UIUtils.cs                   # Panel drawing helpers
├─ ShowText.cs                  # In-game key display overlay
├─ Patches.cs                   # Harmony patches (input chain, judge-error feedback, etc.)
├─ Localization/
│  ├─ LocalizationManager.cs    # JSON localization (Newtonsoft.Json)
│  ├─ zh-CN.json / en-US.json
├─ Macro/
│  ├─ Macro.cs                  # Core: time anchors, worker scheduling, event generation
│  ├─ VirtualAsyncInput.cs      # Virtual async keyboard: feeds the game input queue directly
│  ├─ AsyncInputManager.cs      # SkyHook input management
│  ├─ InputSystem.cs            # InputSystem.dll P/Invoke wrapper
│  ├─ TechniqueSimulator.cs     # Technique simulator P/Invoke wrapper
│  ├─ LevelTechniqueManager.cs  # Level-specific technique configs
│  ├─ PreciseNow.cs             # Precise local time (same clock domain as the game judge)
│  ├─ DSPTimeSimulater.cs       # Audio DSP time simulation
│  ├─ SkyHookSystem.cs          # SkyHook struct definitions
│  └─ KeyMap.cs                 # Key name → virtual-key code mapping
└─ Platform/
   ├─ Windows.cs / Linux.cs     # Platform high-resolution timing
   └─ BaseSelect.cs             # Platform selection
```

---

## License & Related Projects

- Project license: [LICENSE.txt](LICENSE.txt)
- Async input optimization license: [AsyncInputOptimize-LICENSE.txt](AsyncInputOptimize-LICENSE.txt) (GPL-3.0)
- [InputSystem](https://github.com/2228293026/InputSystem) — low-level input injection library (`InputSystem.dll`, loaded at runtime)
