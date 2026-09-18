# AGENTS.md — Kinect Home OS (operational guide for Codex and other coding agents)

For the detailed architecture narrative see `CLAUDE.md`, which is the same project seen in
more depth. This file is the checklist. If code and docs disagree, the code wins. Update the
docs in the same change.

## 1. Mission

Turn an Xbox One Kinect (Kinect v2) into a Windows spatial gesture-control system,
**Kinect Home OS**. The project forked from TangoChen's `KinectV2MouseControl` (MIT, 2018) and
has been substantially rebuilt:

```
Kinect body input → body-relative tracking → gesture recognition → semantic actions → Windows control
```

Implemented today: smoothed body-relative pointer, grip click/drag, lasso right click,
second-hand scroll, horizontal swipe window switching, and double-clap control toggle, plus an
engineering settings/diagnostics window.
Future (NOT implemented): control-center UI, HUD/overlay, voice, AI/agent commands, more
gestures, deeper app control.

## 2. Toolchain

- Windows 11, Kinect for Windows SDK 2.0 (`KINECTSDK20_DIR=C:\Program Files\Microsoft SDKs\Kinect\v2.0_1409\`)
- C# / WPF / **.NET Framework 4.8**, **legacy non-SDK csproj**, AnyCPU, Debug + Release
- Reference: `Microsoft.Kinect` 2.0.0.0 via `$(KINECTSDK20_DIR)Assemblies\Microsoft.Kinect.dll`, `Private=False`
- Build tool: VS 2022 **Preview** MSBuild
- **Not** a Node/npm/web or `dotnet`-SDK project. No `npm`, no `dotnet build`, no test projects.

### Build (both must pass)

PowerShell:
```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Preview\MSBuild\Current\Bin\MSBuild.exe" "C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl.sln" /t:Build /p:Configuration=Debug /v:minimal /nologo
& "C:\Program Files\Microsoft Visual Studio\2022\Preview\MSBuild\Current\Bin\MSBuild.exe" "C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl.sln" /t:Build /p:Configuration=Release /v:minimal /nologo
```

Git Bash (dash-style switches, because MSYS mangles `/p:`):
```bash
"/c/Program Files/Microsoft Visual Studio/2022/Preview/MSBuild/Current/Bin/MSBuild.exe" src/KinectV2MouseControl.sln -t:Build -p:Configuration=Debug -v:minimal -nologo
"/c/Program Files/Microsoft Visual Studio/2022/Preview/MSBuild/Current/Bin/MSBuild.exe" src/KinectV2MouseControl.sln -t:Build -p:Configuration=Release -v:minimal -nologo
```

- `-t:Rebuild` for a guaranteed fresh output.
- MSB3027/MSB3021 "file locked" means the exe is running. Ask the user to close it. Don't kill it.

### Executables

- Debug: `C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl\bin\Debug\KinectV2MouseControl.exe`
- Release: `C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl\bin\Release\KinectV2MouseControl.exe`
- `executables/KinectV2MouseControl_EXE.zip` = upstream's 2018 binary. It is historical, not current.
- `bin/` and `obj/` are git-ignored. **Never commit exes or build output.**

## 3. Code map (`src/KinectV2MouseControl/`)

| File | Responsibility |
|---|---|
| `Models/KinectCursor.cs` | Orchestrator. Body-frame handler, activation zone, controlling-hand latch, pointer path, grip/click/drag + click anchoring, hover timer, stall watchdog, control gate (`IControlGate`), calibration glue, display-change handling, diagnostics fill, all reset paths |
| `Models/CursorControlInput/KinectReader.cs` | Opens/closes sensor. Locks onto the first tracked body. Raises `OnTrackedBody` (with sensor timestamp) / `OnLostTracking` after >5 missing frames |
| `Models/CursorControlInput/KinectBodyHelper.cs` | SpineBase-relative hand position, forward distance, joint weight (1 / 0.35 inferred / 0), hand state + confidence, pointer-frame offsets (±0.185 m X, −PointerCenterHeight Y) |
| `Models/CursorControlInput/HandStateFilter.cs` | Grip debounce: press 0.08 s (confident only), release 0.12 s, pending timeout 0.5 s, `IsPressPending` for pre-press anchoring |
| `Models/CursorMapper/CursorMapper.cs` | Input rect → virtual desktop (`MoveScale × AlignScale`), One Euro filter, jitter dead zone |
| `Models/CursorMapper/OneEuroFilter.cs` | `LowPassFilter`, `OneEuroVectorFilter` |
| `Models/CursorMapper/StationaryLock.cs` | Absolute lock: dwell/radius to lock, breakout radius/speed to unlock, decaying release |
| `Models/CursorMapper/PointerCalibration.cs` | Opt-in per-axis hand range (`ScaleAlignment.Both`) + sweep capture |
| `Models/CursorMapper/MapperStructs.cs` | `MRect`, `MVector2` |
| `Models/CursorControlOutput/CursorOutputLoop.cs` | Background ~125 Hz thread. **Sole `SetCursorPos` caller.** τ 15 ms ease, write-on-change, snap after `ClearTarget` |
| `Models/CursorControlOutput/MouseControl.cs` | SendInput buttons/wheel, `SetCursorPos` wrapper |
| `Models/CursorControlOutput/KeyboardControl.cs` | Alt+Tab / Shift+Alt+Tab as single SendInput batches |
| `Models/CursorControlOutput/Win32Input.cs` | SendInput INPUT union/constants |
| `Models/CursorControlOutput/VirtualScreen.cs` | Virtual desktop bounds in physical px (needs DPI-aware manifest) |
| `Models/Actions/ControlAction.cs` | `ControlActionType` (LeftMouseDown/Up, LeftClick, RightClick, Scroll, NextWindow, PreviousWindow, ToggleControl), `ControlAction` struct, `IActionSink`, `IControlGate` |
| `Models/Actions/ActionRouter.cs` | Discrete action → Win32. Scroll remainder accumulation. ToggleControl → gate |
| `Models/Gestures/GestureContext.cs` | `HandSnapshot` + reused per-frame `GestureContext` |
| `Models/Gestures/GestureEngine.cs` | Priority: Clap (always) → gate/idle → vocabulary check (GripToPress only) → Lasso → Swipe → Scroll |
| `Models/Gestures/GestureTuning.cs` | All gesture/activation thresholds |
| `Models/Gestures/Recognizers/*.cs` | `ClapRecognizer`, `LassoRecognizer`, `ScrollRecognizer`, `SwipeRecognizer` |
| `Models/Gestures/GestureDiagnostics.cs`, `IGestureRecognizer.cs` | Readout model; recognizer contract + `GestureState` |
| `ViewModels/KinectCursorViewModel.cs` | Bindings, Load/Save settings, `DEFAULT_*` + ResetToDefault, 200 ms diagnostics timer, `Quit` |
| `Views/MainWindow.xaml(.cs)` | 18 parameter sliders, 6 mode radios, lock/calibrated checkboxes, Calibrate/Finish, diagnostics panel, Default |
| `Properties/Settings.settings` + `Settings.Designer.cs`, `App.config` | Persisted user settings + defaults |
| `app.manifest` | PerMonitorV2 DPI awareness |
| `KinectV2MouseControl.csproj` | **Every new `.cs` file must be added to `<Compile Include>`** |

Control modes (`KinectCursor.ControlMode`): Disabled, MoveOnly, GripToPress (default and the
only mode with lasso/scroll/swipe), HoverToClick, MoveGripPressing, MoveLiftClicking. Double
clap works in every mode except Disabled.

Threads: body frames and DispatcherTimers run on the WPF UI thread. Only `CursorOutputLoop` runs
on its own thread.

## 4. Engineering invariants (non-negotiable)

1. Single cursor-position writer: only `CursorOutputLoop` moves the cursor.
2. Never write the cursor from the Kinect body-frame handler. Publish a target instead.
3. Keep the high-rate output architecture (thread, time-based ease, write-on-change,
   `ClearTarget` hands back the mouse, `UpdateOutputLoopState` is the one start/stop decision).
4. Don't casually rewrite the working filter/click pipeline:
   One Euro → jitter dead zone → stationary lock → click freeze/anchor → clamp → output loop,
   plus `HandStateFilter`. It is hardware-tuned.
5. No injected mouse-down may survive tracking loss, control disable, sensor stall, app
   shutdown, display reconfiguration, hand handoff, mode change, calibration, or any
   equivalent teardown. `ReleaseAllGrips()` goes before `ResetControlState()`.
6. Recognizers emit `ControlAction`s via `IActionSink`. No direct Win32/cursor calls.
7. `ActionRouter` is the reusable discrete-action boundary for gesture, voice and agent inputs.
   Pointer movement stays out of it.
8. Double-clap disable must keep the sensor and body tracking running so another clap can
   re-enable control. Only `Mode = Disabled` closes the sensor. Clap stays above the gate in
   engine priority.
9. Preserve body-relative (SpineBase) tracking.
10. Recognizers write state before emitting. Never retain `GestureContext`. No per-frame allocations.
11. Keep DPI awareness + physical-pixel virtual-desktop mapping. `SetCursorPos`, not SendInput absolute.
12. Use real sensor `deltaTime`. Never assume 30 Hz.
13. Uncalibrated mapping stays identical to the original. Calibration is opt-in.
14. No broad refactors for cleanliness. No framework migration. No UI redesign unless asked.
15. Don't rename the exe/assembly or bump AssemblyVersion without telling the user, because
    it orphans their `user.config`.
16. Keep the upstream MIT license notice.
17. Never describe hardware behaviour as verified because it compiles.

## 5. Change protocol (every task)

1. Inspect the relevant implementation before editing.
2. Inspect `git status` / `git diff`. Don't clobber uncommitted user work.
3. Preserve working functionality unless the task explicitly changes it.
4. No unrelated refactors.
5. Make the smallest coherent implementation that matches the surrounding style: C# 7,
   explicit braces, long-form properties, and explanatory `///` comments that give the *why*.
6. Build **Debug**.
7. Build **Release**.
8. Report errors **and** warnings.
9. List every source file changed.
10. Give the exact executable paths (§2). The fresh `bin\Debug` / `bin\Release` outputs are the
    current test builds. Don't accumulate copied test exes unless a comparison build is useful.
11. Spell out what specifically needs physical Kinect testing.
12. Label status **COMPILE VERIFIED** vs **HARDWARE VERIFIED**. Only the user's physical test
    makes something hardware verified.

Loop: `code → build Debug+Release → new exe → user physical test → feedback → next change`.

Adding a setting touches: `Settings.settings`, `Settings.Designer.cs`, `App.config`, ViewModel
(property, LoadSettings, SaveSettings, `DEFAULT_*`, ResetToDefault), `MainWindow.xaml`.
Settings save only on normal window close. `user.config` is stored **per exe path**, so Debug
and Release keep separate tuned values.

## 6. Regression checklist (input/pointer/gesture changes)

Reason through each of these, and list the relevant ones for the user to test physically:

- [ ] Pointer smoothness (slow aim, fast sweep, no 30 Hz stepping)
- [ ] Stationary lock engages/breaks cleanly, no snap on release
- [ ] Click anchoring (cursor pinned while fist closes, no jump after freeze)
- [ ] Grip click (no spurious clicks from flicker/low confidence)
- [ ] Drag (holds through noisy frames, anchored to clicked spot)
- [ ] Lasso right click (one per gesture, not during drag)
- [ ] Secondary-hand scroll (arming, neutral, direction, rate, stop)
- [ ] Swipe (both directions, no reverse on return stroke, not during drag, not from clapping)
- [ ] Double clap (toggles both ways, no false positives, works while dragging and releases the drag)
- [ ] Control enable/disable (tracking continues while disabled, UI mode selection re-enables)
- [ ] Tracking loss (button released, cursor handed back)
- [ ] Frame stall / sensor unplug (≥ 1 s watchdog releases)
- [ ] Hand / control-session changes (handoff releases, no carried-over gestures)
- [ ] Stuck mouse button prevention on every teardown path
- [ ] Calibration (sweep, finish, rejected tiny sweep, calibrated vs uncalibrated mapping, MoveScale interaction)
- [ ] Virtual desktop / dual-monitor mapping (edges reachable, negative origins, display change)

## 7. Hardware-tested reference configuration (user-reported; preferences, not defaults)

```
Movement Scale:           ~0.50 in latest testing
Cursor Smoothing:          0.80
Speed Responsiveness:     40
Jitter Dead Zone:           8
Click Freeze:              0.35 s
Pointer Height:             0.40 m
Activation Height:          0.16 m
Forward Activation:         0.15 m
Scroll Speed:              60
Swipe Distance:             0.25 m
Hover-to-click Range:      25.61
Hover-to-click Duration:    2.0 s
Stationary Lock Dwell:     ~0.23 s
```

Code defaults differ (e.g. MoveScale 1, Smoothing 0.2 in settings / 0.7 via Default button,
JitterDeadzone 3, ClickFreeze 0.15, PointerHeight 0.5, ActivationHeight 0.25, LockDwell 0.35).
Don't change defaults to these values unless asked. See `CLAUDE.md` §7 for the full list and
hardware findings. Hardware tip: on Windows 11, Kinect microphone audio enhancements caused
repeated sensor disconnect/reconnect. Disabling them fixed it.

## 8. Current backlog (not implemented)

1. **Dedicated hand roles.** Right hand = pointer only. Left hand = secondary/modifier only.
   The left hand must never become the pointer when the right is lowered. (Today: first
   activated hand wins, right checked first.)
2. **Left-hand clutch.** Open left hand is passive. A closed left fist engages the clutch.
   Scroll and horizontal swipe only while the fist is held. Releasing ends/resets the gesture.
   (Today: scroll arms on a steady-dwell with no fist requirement.)
3. **Scroll refinement.** Rebuild secondary-hand scroll around the clutch, because the current
   one feels over-sensitive.
4. **Calibration.** Comfortable full dual-monitor reach without exaggerated arm movement.

Known code-audit gaps are listed in `CLAUDE.md` §10 (e.g. active-to-active mode change
releases grips only on the next frame; no crash-time mouse-up handler).
