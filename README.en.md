# ADOFAIMacro

[![License](https://img.shields.io/github/license/adofaiex/ADOFAIMacro?color=blue)](LICENSE.txt)
[![Downloads](https://img.shields.io/github/downloads/adofaiex/ADOFAIMacro/total)](https://github.com/adofaiex/ADOFAIMacro/releases)
[![Build](https://github.com/adofaiex/ADOFAIMacro/actions/workflows/build.yml/badge.svg)](https://github.com/adofaiex/ADOFAIMacro/actions/workflows/build.yml)
[![C# 14.0](https://img.shields.io/badge/C%23-14.0-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![.NET Framework 4.8.1](https://img.shields.io/badge/.NET%20Framework-4.8.1-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet-framework)
[![Rust](https://img.shields.io/badge/Rust-2021-000000?logo=rust&logoColor=white)](https://www.rust-lang.org/)

[中文说明](README.md)

**ADOFAIMacro** is a UnityModManager mod for **A Dance of Fire and Ice (ADOFAI)**: it parses level floor timestamps and fires keys with microsecond-precision timing from a dedicated high-priority thread, with direct judge triggering, system-level key simulation, and configurable two-hand key-organization policies.

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
  - [Trace Player & Trace Cache](#trace-player--trace-cache)
- [In-Game Hotkeys](#in-game-hotkeys)
- [Troubleshooting](#troubleshooting)
- [Building (Developers)](#building-developers)
- [Project Structure](#project-structure)
- [License & Related Projects](#license--related-projects)

---

## Feature Overview

- **Auto triggering**: level timestamps → precision-timed key events; double-buffered time anchors sync the main and worker threads, lock-free with zero waiting.
- **Four input paths**: direct judge / SendInput / SkyHook low-level injection (NtInject / NtSendInput) / virtual async keyboard direct-feed (bypasses system injection, sub-microsecond jitter).
- **Technique simulation**: selectable half-beat grid, fixed single-finger KPS runs, or tempo-window grouping; configurable hand assignment, keys / orders / press durations (per-slice accent "strong–weak" patterns), speed segments, per-level configs; one C# core generates and caches technique traces. The optional Rust trigger kernel handles key timing only (embedded in the main DLL — no extra files to ship).
- **Trace player**: the whole chart’s trace is generated and cached during level loading; export / import `.adofaitrace` files for sharing, with time-driven / angle-driven playback; angle-driven mode moves waiting and key-firing onto the worker thread, eliminating the 0–1 frame firing latency.
- **Timing fine-tuning**: ±100 ms offset, adjustable in-game with arrow keys; automatic judge-error calibration (closed-loop, cancels speed-section offsets).
- **Key filtering**: blacklist / whitelist, independent sync (KeyCode) and async (VK code) filters; passes through the 4 special keys the game hard-blocks (PrintScreen / F12 / LAlt / Super).
- **Extras**: auto keypress on death, GC pause suppression during play, out-of-focus guard, macro status badge (HIT / VIRT / NT / SI) with on-screen virtual keys, English/Chinese UI.

---

## Quick Start

**Prerequisites**: ADOFAI installed, [UnityModManager](https://www.nexusmods.com/site/mods/21) (≥ 0.27.0).

1. Download the archive from [Releases](https://github.com/adofaiex/ADOFAIMacro/releases) and install via UMM, or manually extract it into the game's `Mods/ADOFAIMacro/` folder.
   - The folder should contain: `ADOFAIMacro.dll`, `Info.json`, `InputSystem.dll`, `System.Memory.dll`, and `Localization/` (zh-CN / en-US).
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
| Enable logging (nothing written to Player.log when off) | `EnableLogging` | on | When off the mod writes no logs; technique-table / diagnostic dump files are unaffected. |

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
| [Experimental] Enable High Precision Async (re-enable the mod to apply) | `HighPrecisionAsync` | off | Experimental; leave off unless investigating issues. |
| Auto judge-error calibration | `AutoCalibrateJudgement` | on | Closed loop: compensates the offset from actual judge errors (including speed sections); re-converges every run. |
| Suppress GC pauses during play | `SuppressGcPauses` | off | Removes GC-induced error spikes on dense charts (GC happens during loading instead). |

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
| Auto-press key on death | `EnableDeathKey` | off | **Requires both Key simulation and Use advanced input (SkyHook).** |
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

When enabled (requires **Key simulation**), the macro stops simply rotating the key list. The default half-beat grouping policy uses per-floor speed and the **Speed Threshold (BPM)** to build a base grid that alternates hands. One hand can play multiple notes in a slice; slices are subdivided when they exceed that hand’s capacity.

The panel offers three policies: **Half-beat groups** (the default, preserving the current grid rule), **KPS-threshold groups** (fixed single-finger window), and **Tempo Window** (estimates a one-hand run window from per-floor speed and the BPM threshold). The latter two also cap run length at the hand's available keys. They are rule prototypes for gameplay comparison, not measured human limits or claims that one is easier for every chart or player.

### Basic parameters

| Panel item | Default | Description |
|---|---|---|
| Enable Technique Simulation | off | Master switch (requires Key simulation). |
| Starting Hand | Right | Which hand plays the first slice. |
| Attack grouping | Half-beat groups | Switch between the half-beat grid, fixed KPS runs, or Tempo Window. |
| Run hand choice | Alternate groups | KPS/Tempo Window only; optionally reuse the starting hand after one current grouping window since its last attack, otherwise alternate. |
| Single-finger threshold (KPS) | 6.5 | Shown in KPS mode; attacks join while first-to-current span is less than `1/(2×KPS)` seconds. |
| Global · Speed Threshold (BPM) | 500 | Used by half-beat grid and Tempo Window (range 50–2000); unused by fixed KPS. |
| L/R Keys | `D,F` / `J,K` | Keys available to each hand; presets DF/JK, DS/JK, ASDF/JKL. |
| L/R Order | empty | See format below; empty = default rotation. |
| L/R Ratio | `0.8,0.8` | Scales the duration of ordinary key presses; hold notes follow the chart automatically. |

**Order format**: pipe separates key-count groups, commas separate 1-based indices. Example `1,2 | 1,2 | 1,2,1`: one-key slices alternate keys 1 and 2, three-key slices play 1→2→1. Empty = default order.

**The "strong–weak" accent mechanism**: the order row is picked **by the number of events in the slice** — a slice containing N events uses row N of the order table (clamped to the last row), and every key in that row carries its own press-duration ratio. Dense runs therefore produce a natural strong→weak accent cycle (e.g. a 3-event slice plays `50→1.2 / 51→1 / 82→0.8`). This is why the order table has multiple rows: with only row 1 filled in, multi-event slices have no accent layer.

**Boundary adjustments**: at the same speed and with the same settings, the planner can merge or move closely spaced notes across a slice boundary when hand capacity permits. This avoids some unintended two-hand hits. Slice length uses per-floor speed; there is no “Speed Change Tolerance” setting.

### Profiles

Save multiple complete technique parameter sets (grouping policy, KPS threshold, keys, orders, durations, starting hand, segments); create / delete / switch from the panel.

The run hand policy is saved with the profile. Missing fields in older profiles default to the half-beat strategy; run policies default to alternation. See the [technique design and reference comparison](docs/technique-design.md) for the algorithm boundaries and offline comparison procedure (Chinese with English summary).

### Speed Segments

Override global settings within a floor range: each segment can set its own **BPM limit** (used by half-beat grouping and Tempo Window) and **L/R keys / orders / ratios** (empty fields inherit global). A settings change reanchors the grid without resetting to the starting hand.

### Level-specific Configs

Each level can have its own config:

- Stored next to the level file, named `LevelName.adofaimacro.json`;
- Auto-loaded on entry ("Auto-load from level folder", on by default);
- Load / Save / Delete from the bottom of the panel, with current status display.

Useful for smaller key sets on high-BPM sections, custom orders for specific patterns, or per-level fine-tuning.

### Trace Player & Trace Cache

The bottom of the technique panel is the **Trace Player**. With technique simulation enabled, the whole chart’s trace is **generated and cached during level loading**. On re-entry, a cache is reused only if the chart, technique settings, and trace version match.

- **Driver**: time-driven (default, uses the original time-anchor chain) / angle-driven (triggers by the angle the planet has swept across the current tile, immune to clock jitter; quantized by frame rate so it can be one frame late — compensate with "Angle lead (ms)", range −200 ~ 400).
- **Generate trace offline**: manually prepare a trace for the loaded level, or reuse a matching cache.
- **Export / import trace**: exports the current chart's `.adofaitrace` file for sharing; importing validates that it is the same chart and technique settings, and that the trace version is compatible, then applies after re-entering the level.
- **Clear trace cache**: caches live under the game's `persistentDataPath/ADOFAIMacro/trace/`, clearable with one click.

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
Make sure auto judge-error calibration is on (default); under technique simulation, give the speed section its own BPM limit via segments. Slice length follows per-floor speed automatically — there is no (and no longer any) speed-change tolerance to tune.

**Q4: Death key doesn't work**
Requires both Key simulation and Use advanced input (SkyHook) — confirm both are on; confirm the death key is enabled and the key name / code is valid; increase the delay if needed.

**Q5: Key-viewer tools can't see macro keys**
The virtual async keyboard bypasses the system input stream, so third-party tools can't read it — enable "Mirror virtual keys to system input" (on by default). The green badge and on-screen virtual keys (top-left corner) are rendered by the mod itself and are unaffected.

**Q6: Key filter "does nothing"**
Confirm the filter is enabled; confirm the mode (black/whitelist) matches your intent; under advanced input, don't forget the **async** key list. PrintScreen / F12 / LAlt / Super (Win key) work as macro keys with no extra setup — the mod passes through these four keys that the game hard-blocks.

**Q7: The game quits right after enabling the mod**
`Info.json` was modified (tamper detection) — restore it; or BaseMacro is installed (mutually exclusive) — remove it.

**Q8: Technique simulation produces no key events**
Make sure Enable Macro, Key simulation, and Enable Technique Simulation are on. After loading a level, choose Generate trace offline and check the trace status and logs.

**Q9: Judgement drifts when first entering the game**
Auto judge-error calibration keeps converging during play; usually no manual action is needed. For a persistent offset, compensate manually with "Offset (ms)". Larger error on the very first run is normal (calibration samples come from the current run and are not carried across runs).

When filing an issue, please include: game version, mod version, key settings screenshots, whether SkyHook is used and the current input mode, reproduction steps and logs.

---

## Building (Developers)

- Environment: .NET SDK 10 (bundles a C# 14 Roslyn), .NET Framework 4.8.1, C# preview; Rust stable (Release only, builds the `TriggerRs` trigger kernel).
- C# side: `ADOFAIMacro/ADOFAIMacro-Dev.csproj` is an SDK-style project (NuGet via `PackageReference`, restored automatically); game assemblies are referenced through the `$(GameManaged)` property pointing at your local ADOFAI install — you need the game's `Assembly-CSharp.dll`, `UnityEngine*.dll`, `SkyHook.Unity.dll`, `UnityModManager.dll`, plus `Newtonsoft.Json.dll` (used by the localization system). Either `dotnet build -c Release` or MSBuild works.
- Native input: run `cmd /c build_cpp.bat` at the repo root to build `InputSystem` (x64 Debug + Release). **After changing native code you must fully rebuild (`/t:Rebuild`) — incremental builds may pick up stale artifacts.** CI uses `native/prebuilt/InputSystem.dll`.
- Rust trigger kernel: Release builds run `cargo build --release` automatically (path overridable with `-p:CargoExe=`); the resulting `macro_trigger.dll` is embedded into the main DLL, falling back to an existing artifact when cargo is unavailable.
- Deploy to `Mods/ADOFAIMacro/`: `ADOFAIMacro.dll` + `Info.json` + `Localization/` + `InputSystem.dll` + `System.Memory.dll`. `out/Release/` is the single source of truth for the package layout.

---

## Project Structure

```text
ADOFAIMacro/                    # C# mod (namespaces mirror directories)
├─ Core/
│  ├─ Main.cs                   # Entry point: mod lifecycle, input initialization, tamper detection
│  ├─ Patches.cs                # All Harmony patches (input chain, judge-error feedback, special-key passthrough…)
│  ├─ MacroPatch.cs / MacroPatchHub.cs  # Multi-version Harmony adaptation (patch guards + mounting)
│  ├─ GuardFactory.cs           # IL guard code generation
│  ├─ GameVersion.cs            # Game version detection
│  └─ GameNotifier.cs           # In-game messages via the game's native notification bar
├─ Settings/Settings.cs         # Settings + UMM panel UI (all tabs)
├─ UI/
│  ├─ UIUtils.cs                # Panel drawing helpers
│  └─ ShowText.cs               # Macro status badge (HIT/VIRT/NT/SI) + on-screen virtual keys
├─ Input/
│  ├─ InputSystem.cs            # InputSystem.dll P/Invoke wrapper
│  ├─ VirtualAsyncInput.cs      # Virtual async keyboard: feeds the game input queue directly
│  ├─ AsyncInputManager.cs      # Injection path management (SkyHook / SendInput dispatch)
│  ├─ SkyHookSystem.cs          # SkyHook struct definitions
│  └─ KeyMap.cs                 # Key name → virtual-key code mapping
├─ Technique/
│  ├─ MacroEngine.cs            # Technique main flow (partial: judging / calibration / event generation)
│  ├─ TechniqueIntegration.cs   # Connects game floors and settings to the managed core
│  ├─ TechniquePlanner.cs       # Managed technique planning
│  ├─ TechniqueTimeline.cs      # Maps floors to the judgment timeline
│  ├─ TechniquePlayback.cs      # Pure plan / trace / playback event adapters
│  ├─ EventBuilder.cs           # Key event assembly
│  ├─ ConfigParser.cs           # Order / duration / key string parsing
│  ├─ KeyWorker.cs              # High-priority key-firing thread (time anchors)
│  ├─ AngleAsyncDriver.cs       # Angle-driven: waiting and key-firing moved to the worker thread
│  ├─ TriggerRsHost.cs          # Rust trigger kernel host (FFI + C# fallback)
│  ├─ EmbeddedNative.cs         # Runtime extraction + LoadLibrary of embedded native DLLs
│  ├─ Clock.cs / Lifecycle.cs   # Clock & lifecycle
│  ├─ TraceFile.cs / TracePlayer.cs  # Trace cache (.adotr) and trace player (.adofaitrace import/export)
│  └─ LevelTechniqueManager.cs  # Level-specific technique configs (*.adofaimacro.json)
├─ Calibration/Calibration.cs   # Closed-loop judge-error auto calibration
├─ Timing/
│  ├─ PreciseNow.cs             # Unified high-precision clock (same domain as the game judge)
│  └─ DSPTimeSimulater.cs       # Audio DSP time simulation
├─ Localization/
│  ├─ LocalizationManager.cs    # JSON localization (Newtonsoft.Json, with hardcoded fallback)
│  └─ zh-CN.json / en-US.json
└─ Platform/
   ├─ Windows.cs / Linux.cs     # Platform high-resolution timing
   └─ BaseSelect.cs             # Platform selection
InputSystem/                    # C++ low-level input injection (NtInject / NtSendInput)
TriggerRs/                      # Rust trigger kernel (cdylib, embedded into the main DLL)
native/prebuilt/                # CI uses the prebuilt InputSystem.dll
build_cpp.bat                   # Builds InputSystem
tools/                          # Offline evaluation / regression tooling
.github/workflows/build.yml     # CI: build → package → tag-triggered release
```

---

## License & Related Projects

- Project license: [LICENSE.txt](LICENSE.txt)
- Async input optimization license: [AsyncInputOptimize-LICENSE.txt](AsyncInputOptimize-LICENSE.txt) (GPL-3.0)
- [InputSystem](https://github.com/2228293026/InputSystem) — low-level input injection library (`InputSystem.dll`, loaded at runtime)
