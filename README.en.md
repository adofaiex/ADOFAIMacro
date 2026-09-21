# ADOFAIMacro

[![License](https://img.shields.io/github/license/adofaiex/ADOFAIMacro?color=blue)](LICENSE.txt)
[![Downloads](https://img.shields.io/github/downloads/adofaiex/ADOFAIMacro/total)](https://github.com/adofaiex/ADOFAIMacro/releases)
[![C# 12.0](https://img.shields.io/badge/C%23-12.0-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![.NET Framework 4.8.1](https://img.shields.io/badge/.NET%20Framework-4.8.1-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet-framework)

[中文说明](README.md)

> **Fork notice**: This repository is a fork of [adofaiex/ADOFAIMacro](https://github.com/adofaiex/ADOFAIMacro), maintained by [Daoguan-king](https://github.com/Daoguan-king). The upstream project is licensed under **AGPL-3.0** (plus a GPL-3.0 portion from AsyncInputOptimize); this fork keeps the same licenses, preserves all original copyright/license notices, and only contains compatibility and algorithm fixes. `Info.json` is left untouched.
>
> **Changes in this fork (2026-09, for game r150 / Unity 6.3)**
> 1. **Game API compatibility**: `scrPlayer.Hit(bool)` → `Hit(long?, bool)`; `scrMisc.GetAdjustedAngleBoundaryInDeg` now takes `Difficulty` and returns a struct (probe uses `.Counted` and a Prefix captures the raw angle); `scrHitErrorMeter.AddHit` gained a `HitMargin` parameter and rewrites `angleDiff` in place; key filtering on `CountValidKeysPressed` became a transpiler (keeping the game's new touch/coop/key-limiter logic); replaced the unsupported `[with(n)]` syntax
> 2. **Technique simulator rewrite**: piece length is quantized to multiples of the **actual note interval** (piece = interval × notes for this hand); boundary cutting scores by “distance (under-length penalized 2x) − gap” so doubles/chords stay on one hand and mergeable bursts are not split; per-floor `floor.speed` uses the *next* event's floor (fixes SetSpeed causing piece-length mismatch and 3-1-1 fragmentation); hands alternate only when the **actual note rate** (derived from event intervals, notes/minute; a 90° tile is 2× the tile BPM) exceeds the threshold, and below it the hand resets to the starting/main hand (e.g. a single finger at low KPS, slow passages after a fast one return to main); above the threshold the total finger count is `n = ceil(note rate/threshold)` (120→1, 240→2, 360→3, 480→4 fingers…), with the main hand taking the extra finger when n is odd (three fingers = main hand 2 notes + off hand 1, alternating), and a slice takes exactly maxK events when it would need more than the hand has keys (extreme BPM no longer under-uses keys); empty pieces no longer flip hands; segment reset only when the key config changes (fixes the starting hand repeating at the beginning); press duration = **actual note interval × duration ratio** (smooth and monotonic), capped by the piece's note span + smallest internal gap (no long hold across a Pause), plus a “Legacy press duration (1.3.0.30)” toggle
> 3. **Native layer**: new `BuildTechniqueHitEventsEx` export (legacy export kept); `InputSystem` / `TechniqueSimulator` rebuilt as Release|x64 (toolset v143)
> 4. **Key overlay**: numpad `N0–N9 / N* N+ N- N. N/`, function/arrow keys, adaptive box width, `0xXX` fallback for unknown keys
> 5. **Settings persistence**: speed-change tolerance is written back to the selected profile (fixes reset-to-0 after restart)
> 6. **Slice-base fix**: the base slice length now follows the **actual note interval** (time gap to the next note, skipping same-time doubles/chords) × `round(note rate/(2·threshold))`, replacing the “local half-beat quantization”. On non-90° tiles / SetSpeed mixes (e.g. a snowflake chart with doubles), when the actual note interval is not an integer multiple of the local half-beat, the quantized length overshoots the next note and merges a single with the following double/chord onto one hand, drifting the alternation phase (`R2 L1 R1 L3…`); after the fix it is the stable `R2 L1 R1 L1`. For 90° tiles (interval = local half-beat) it is identical to the old formula
> 7. **Press-duration base fix**: the human floor is now derived from the **actual note interval** (`GetNoteFoldedPressLength`) instead of the local floor BPM (`bpm×floor.speed`), plus a same-moment tolerance. On uniform charts (e.g. a snowflake chart) the old code varied the hold between 39–73 ms because SetSpeed gives each tile a different `speed`, and the two keys of one double could differ by >10 ms; now every note (and both keys of a double/chord) in a uniform passage holds the same duration
> 8. **Balance multi-press across hands**: new setting, using an **adaptive multi-press cluster** (consecutive events with internal gaps ≤12 ms and a total span ≤35 ms; covers the common “several 1° tiles + 999 midspin” 8-press form, 5–22 ms per cluster, without misclassifying a true uniform stream). When one moment has more notes than a hand has keys, on = split evenly across both hands (6→R3 L3, 7→R4 L3, 8→R4 L4, odd extra to the main hand), off = main hand takes all its keys then the rest goes to the other hand (6→R5 L1)
> 9. **Auto judge-error calibration direction fix**: on r150 the judge probe reports `errMs<0 = late` (it mirrors the game's `AddHit` `*−57.29578` conversion), but the old closed loop treated a late error as early and kept increasing the offset — positive feedback. On dense multi-press charts (e.g. 16-press) the offset ramped to the +60 ms clamp and the macro fired systematically late, failing the chart. Changed to negative feedback (`_autoOffsetMs += step`), which converges to the offset that cancels the lateness
> 10. **Docs**: README and EN/CN localization strings updated
> 11. **Finger-count & press-duration fix (2026-09-21)**: fixes “still two fingers above 240 BPM, only four fingers at 390” and “hold time jumps above 240 BPM”:
>     - The total finger count is now `ceil(rate/threshold)` instead of `round(rate/(2·threshold))`. The old formula could only produce an even finger count (k notes per hand, both hands symmetric), so 240–360 BPM stayed two-fingered (120–180 taps/minute per finger, over the single-finger limit), and the half-integer boundary suffered from floating-point error: at 360 BPM the measured rate is 359.99999, `round(1.49999996)=1` → still two fingers at 360 and only four at 390. The new formula gives 240→2, 360→3, 480→4…; for an odd count the main hand takes the extra finger (three fingers = main hand taps 2 notes, off hand 1, alternating), so no finger exceeds the threshold;
>     - Hold time no longer derives from the slice structure (`CalculateReleaseTime`'s “half way to the next slice's end”: the slice length steps by 2k× the interval, giving 150 ms at 240 BPM then 267 ms just above it, sawtoothing with k). It now directly follows that note's **actual interval × duration ratio**: on the 120→900 BPM test chart it runs 300→240→200→171→150→133→120→109→100→92→86→80→75→…→40 ms, smooth and monotonic, with the same duty cycle as below 240;
>     - The folded human floor only applies at or below the threshold (rate ≤ threshold); above it the base is no longer folded back an octave;
>     - For tight clusters (adjacent gaps ≤12 ms, i.e. multi-presses/offset doubles) the press base falls back to the gap outside the cluster, so hold time never shrinks to 2–4 ms (less than a frame; frame-sampled input paths could drop the key).
> 12. **Fast double/multi-press hold-time fix (2026-09-21, second pass)**: after item 11, doubles/multi-presses could still hold too briefly:
>     - The `cap` (piece span + smallest internal gap) previously skipped only same-moment (≤3 ms) gaps. Inside an 8-press cluster a 3.2 ms gap counted as a “meaningful internal gap”, so `cap = ratio×(cluster span + 3.2 ms)` was only ~15 ms and the first half of the chart was clipped to 15 ms (the second half, with 0.8 ms gaps, was treated as same-moment and stayed normal) — it now skips gaps ≤12 ms **inside the cluster**, so the whole cluster falls back to the normal gap outside it;
>     - Doubles / fast runs with 10s-of-ms gaps (16.7 ms, 33.3 ms) do not match the cluster rule, so hold time was just 0.6×gap = 10–20 ms, too short to see or to survive frame sampling — added a **human minimum press of 50 ms** (a real tap is ~40–60 ms and must span at least one frame): hold = max(interval × ratio, 50 ms). Slow passages (>83 ms gaps) are unaffected; high rates (750 BPM and up) settle at 50 ms.
> 13. **Double/high-density late-timing fix (2026-09-21, third pass)**: on doubles and above, worse at higher BPM, the game's timing reading was off by 5–10 ms (16-press test, multi-press test 2, 20000-BPM stress chart). The cause is **not performance**: the multiple keys at one instant are sent **one by one** on the macro worker thread, and each `SendKey` (virtual direct feed + mirrored `SendInput`) costs 0.1–2 ms, so later keys are actually sent after their ideal time; the virtual event's timestamp was taken at send time, and the game judges by that timestamp (sub-frame) — so later keys were judged late (an 8-press can accumulate 5–10 ms). The fix now **batch-anchors event timestamps**: the first event of a batch (which the macro sends after waiting for its ideal time, so it is only off by scheduling jitter) provides the real local base time, and the rest of the batch are placed by their **ideal song-time offsets** (50 ms batch window, re-anchored beyond that). This removes the per-key serial-send accumulation *without relying on the absolute `audioNow` extrapolation*, so high-density streams (e.g. the 20000-BPM chart) no longer drift. If a small offset remains, turn off **Mirror virtual keys** (the extra `SendInput` per virtual key is the main latency source; the built-in key overlay still works, external `Input.GetKeyDown` viewers will not).

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

When enabled (requires **Key simulation**), the macro stops simply rotating the key list and instead simulates two human hands: time is divided into slices, each slice is assigned to one hand; **hands alternate only when the actual note rate exceeds the Speed Threshold**, and below the threshold the hand resets to and stays on the **starting (main) hand** — so after a fast passage a slow passage returns to the main hand (a single-event slice = single-finger tapping, multi-event slices roll that hand's fingers); above the threshold L/R alternate and each hand takes more events as the rate rises until its whole key set is used. The actual rate is derived from note intervals (notes/minute); note that one 90° tile is a half-beat, so its actual rate is **2×** the tile BPM (45° tiles 4×, straight tiles 1×), and SetSpeed is naturally covered by the intervals. Slice lengths follow the **actual note interval** (the gap to the next note after skipping same-time doubles/chords, naturally covering SetSpeed and any tile angle): at or below the **Speed Threshold** it is one note per slice (single-finger tapping); above it the total finger count is `n = ceil(note rate/threshold)` (threshold 120: 240 BPM→2 fingers, 360→3, 480→4, 600→5…), and each slice is handled by one hand with half of those notes — for even n both hands take n/2 notes alternately, for odd n the main hand takes the extra finger (three fingers = main hand taps 2 notes, off hand 1, alternating), so no finger exceeds the threshold; a slice that would need more notes than the hand has keys takes all its keys. Cuts always land on the note grid — non-90° tiles / SetSpeed mixes (e.g. a snowflake chart with doubles) no longer overshoot the next note and merge a single with a double onto one hand. Key hold time = max(the note's **actual interval × the configured L/R duration ratio**, 50 ms) (smooth and monotonic: 240 BPM→150 ms, 360→100 ms, 720 BPM and up settles at 50 ms; tight clusters fall back to the gap outside the cluster and same-time doubles/chords share one base, so a hold is never shorter than a frame), capped by “this piece's note span + smallest internal gap (skipping ≤12 ms gaps inside a cluster)” (no stretch across a Pause). Multi-presses (one moment with more notes than a hand has keys) default to the main hand filling all its keys with the rest going to the other hand (e.g. a 6-press → R5 L1); enabling **Balance multi-press across hands** splits it evenly across both hands instead (6→R3 L3, 7→R4 L3, 8→R4 L4, an odd extra note to the main hand). “One moment” is detected **adaptively**: consecutive events with internal gaps ≤12 ms and a total span ≤35 ms count as one multi-press (covers the common “several 1° tiles + 999 midspin” 8-press form, ~5–22 ms per cluster; a true uniform stream is not misclassified). Cuts snap to event gaps (natural cluster boundaries) so doubles/chords are not split across hands and mergeable bursts are not switched early.

### Basic parameters

| Panel item | Default | Description |
|---|---|---|
| Enable Technique Simulation (L/R alternation) | off | Master switch (requires Key simulation). |
| Starting Hand | Right | Which hand plays the first slice. |
| Global · Speed Threshold (BPM) | 500 | Single-finger tap limit (range 50 ~ 2000, unit = notes/minute): at or below the actual note rate the hand resets to the starting (main) hand, above it L/R alternate and the total finger count is `n = ceil(rate/threshold)` (240→2 fingers, 360→3, 480→4…; an odd count gives the main hand one extra). 90° tiles count as tile BPM ×2. |
| L/R Keys | `D,F` / `J,K` | Keys available to each hand; presets DF/JK, DS/JK, ASDF/JKL. |
| L/R Order | empty | See format below; empty = default rotation. |
| L/R Ratio | `0.8,0.8` | Press-duration ratio (0 ~ 1). Hold time = max(the note's actual interval × ratio, 50 ms) (smooth and monotonic; same-time doubles/chords hold equally, and a hold is never shorter than a frame), capped by “this piece's note span + smallest internal gap (skipping ≤12 ms gaps inside a cluster)” (no stretch across a Pause); hold notes are handled automatically. |
| Legacy press duration | off | On: hold time uses the 1.3.0.30 octave-folded slice length (one short tap near the threshold; slow charts won't hold until the next note); Off: follows the current slice length (held until near the next note). |
| Balance multi-press across hands | off | When one moment has more notes than a hand has keys: on = split evenly across both hands (6→R3 L3, 7→R4 L3, 8→R4 L4, odd extra to the main hand); off = the main hand takes all its keys first and the rest goes to the other hand (6→R5 L1). |
| Speed Change Tolerance | 0 | Auto-adjusts BPM to align slices with event timing. 0 = off, 0.2 = moderate, 0.5 = aggressive; for charts with continuous speed changes. |

**Order format**: pipe separates key-count groups, commas separate 1-based indices. Example `1,2 | 1,2 | 1,2,1`: one-key slices alternate keys 1 and 2, three-key slices play 1→2→1. Empty = default order.

### Profiles

Save multiple complete technique parameter sets (keys, orders, durations, starting hand, tolerance, segments); create / delete / switch from the panel.

### Speed Segments

Override global settings within a floor range: each segment can set its own **BPM limit** and **L/R keys / orders / ratios** (empty fields inherit global). Hand order resets and cross-segment holds are released at segment boundaries.

### Level-specific Configs

Each level can have its own config:

- Stored next to the level file, named `LevelName.adofaimacro.json`;
- Auto-loaded on entry ("Auto-load from level folder", on by default);
- Load / Save / Delete from the bottom of the panel, with current status display.

Useful for smaller key sets on high-BPM sections, custom orders for specific patterns, or per-level fine-tuning.

### Notes

- **The first time you enter the game you need to die once to calibrate the time** (same note as in the panel).
- The core algorithm runs in the native `TechniqueSimulator.dll` — make sure it is in `Mods/ADOFAIMacro/`. **Release builds produce no technique output without the DLL** (debug builds fall back to the C# implementation).

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
