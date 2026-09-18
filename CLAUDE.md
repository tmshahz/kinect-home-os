# CLAUDE.md — Kinect Home OS

Standing context for Claude Code. Read this first; it is meant to save you from re-auditing
the repo every session. `AGENTS.md` holds the same rules in a more operational, checklist form
for Codex and other agents. If code and this file disagree, **the code wins**. Fix this file
in the same change.

---

## 1. Project identity and mission

This repo started as a fork of TangoChen's **KinectV2MouseControl**
(`https://github.com/TangoChen/KinectV2MouseControl`, MIT, © Jingzhou Chen, last upstream
release v1.2.1 in 2018). It has been heavily reworked and is growing into **Kinect Home OS**,
a Windows spatial gesture-control system built on an Xbox One Kinect (Kinect v2).

Conceptual pipeline (this matches the current code):

```
Kinect/body input            KinectReader, KinectBodyHelper
      ↓
body-relative tracking       HandSnapshot / GestureContext (metres from SpineBase)
      ↓
gesture recognition          GestureEngine + recognizers, HandStateFilter (grip)
      ↓
semantic actions             ControlAction  →  ActionRouter
      ↓
Windows control              MouseControl / KeyboardControl (SendInput), CursorOutputLoop (SetCursorPos)
```

**Longer-term directions. None of these exist in code yet:** custom control-center UI,
HUD/overlay, voice commands, AI/agent commands, more gestures, deeper Windows/app control.
The `ActionRouter`/`ControlAction` boundary is where voice and agent commands are meant to
plug in later. `ControlAction.Parameter` (string) exists for future actions like `LaunchApp`
but nothing uses it yet.

Names still inherited from upstream: assembly/exe `KinectV2MouseControl`, namespace
`KinectV2MouseControl`, window title "Kinect v2 Mouse Control", AssemblyVersion `1.2.1.0`,
the upstream credit label in the UI, and `README.md` (it still describes upstream v1.2.1).
**Do not rename any of these unless asked.** Renaming the exe or bumping the version moves
where Windows stores `user.config` (see §8), so the user's tuned settings would appear to
vanish.

## 2. Technical stack

| Item | Actual value |
|---|---|
| OS | Windows (developed on Windows 11) |
| Sensor | Xbox One Kinect / Kinect v2, via the Kinect for Windows adapter |
| SDK | Kinect for Windows SDK 2.0 (`KINECTSDK20_DIR` = `C:\Program Files\Microsoft SDKs\Kinect\v2.0_1409\`) |
| UI | WPF (MVVM-ish: one ViewModel, one Window) |
| Language | C# (C# 7-level features, e.g. `out double x` inline declarations) |
| Framework | .NET Framework **4.8** (`TargetFrameworkVersion v4.8`, `App.config` supportedRuntime 4.8) |
| Project | Legacy, **non-SDK-style** `.csproj` (ToolsVersion 12.0), explicit `<Compile Include>` list |
| Build | MSBuild from Visual Studio 2022 **Preview** |
| Platform | AnyCPU, Debug and Release only |
| Dependency | `Microsoft.Kinect` 2.0.0.0, HintPath `$(KINECTSDK20_DIR)Assemblies\Microsoft.Kinect.dll`, `Private=False` (binds to the GAC copy at runtime, so it is not copied to `bin\`) |
| Manifest | `app.manifest`: PerMonitorV2 DPI awareness, asInvoker |

**This is not a Node/npm/web project.** Do not run `npm`, do not add `package.json`, and do
not try `dotnet build`/`dotnet new`. The legacy WPF csproj is built with Visual Studio MSBuild.
There are no unit-test projects.

### Build commands (verified on this machine)

PowerShell:
```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Preview\MSBuild\Current\Bin\MSBuild.exe" "C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl.sln" /t:Build /p:Configuration=Debug /v:minimal /nologo
& "C:\Program Files\Microsoft Visual Studio\2022\Preview\MSBuild\Current\Bin\MSBuild.exe" "C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl.sln" /t:Build /p:Configuration=Release /v:minimal /nologo
```

Git Bash (use `-p:` not `/p:`, because MSYS rewrites `/p:` as if it were a path):
```bash
"/c/Program Files/Microsoft Visual Studio/2022/Preview/MSBuild/Current/Bin/MSBuild.exe" src/KinectV2MouseControl.sln -t:Build -p:Configuration=Debug -v:minimal -nologo
"/c/Program Files/Microsoft Visual Studio/2022/Preview/MSBuild/Current/Bin/MSBuild.exe" src/KinectV2MouseControl.sln -t:Build -p:Configuration=Release -v:minimal -nologo
```

Use `-t:Rebuild` when you want a guaranteed fresh output. If the build fails with
MSB3027/MSB3021 ("file is locked"), the app is still running from that `bin` folder. Ask the
user to close it; do not kill it yourself while they may be testing.

### Executable paths

- Debug: `C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl\bin\Debug\KinectV2MouseControl.exe`
- Release: `C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl\bin\Release\KinectV2MouseControl.exe`

`bin/` and `obj/` are git-ignored. `executables/KinectV2MouseControl_EXE.zip` is **upstream's
2018 v1.2.1 binary**, kept for history. It is not the current build.

## 3. Repository map

```
CLAUDE.md, AGENTS.md          agent context (this file + operational twin)
README.md, LICENSE.md         upstream README (outdated) and MIT license (keep the notice)
executables/                  upstream 2018 release zip (historical)
src/KinectV2MouseControl.sln
src/KinectV2MouseControl/
  KinectV2MouseControl.csproj legacy csproj: new .cs files MUST be added to <Compile Include>
  App.config                  user-settings defaults + DPI AppContext switch
  app.manifest                DPI awareness (required for correct cursor coordinates)
  Properties/Settings.*       persisted user settings (designer file is generated, keep in sync)
  Models/
    KinectCursor.cs           THE orchestrator: frame handler, pointer path, grip/click, safety, calibration glue
    CursorControlInput/
      KinectReader.cs         sensor open/close, body selection, OnTrackedBody / OnLostTracking
      KinectBodyHelper.cs     body-relative geometry, joint weights, hand state/confidence
      HandStateFilter.cs      debounced open/closed decision (grip)
    CursorMapper/
      CursorMapper.cs         input rect → virtual-desktop mapping, One Euro filter, jitter dead zone
      OneEuroFilter.cs        LowPassFilter + OneEuroVectorFilter
      StationaryLock.cs       absolute pointer lock with hysteresis + smooth release
      PointerCalibration.cs   optional per-axis hand range + calibration sweep capture
      MapperStructs.cs        MRect / MVector2
    CursorControlOutput/
      CursorOutputLoop.cs     ~125 Hz background thread, sole SetCursorPos caller
      MouseControl.cs         SendInput buttons/wheel; SetCursorPos wrapper
      KeyboardControl.cs      Alt+Tab / Shift+Alt+Tab
      Win32Input.cs           SendInput structs/constants
      VirtualScreen.cs        virtual desktop bounds in physical pixels
    Actions/
      ControlAction.cs        ControlActionType enum, ControlAction struct, IActionSink, IControlGate
      ActionRouter.cs         semantic action → Win32 (the discrete-action boundary)
    Gestures/
      GestureContext.cs       HandSnapshot + per-frame context (reused, no per-frame allocation)
      GestureEngine.cs        runs recognizers, explicit priority arbitration
      GestureTuning.cs        every gesture/activation threshold in one place
      GestureDiagnostics.cs   live readout model
      IGestureRecognizer.cs   recognizer contract + GestureState enum
      Recognizers/            Clap, Lasso, Scroll, Swipe
  ViewModels/KinectCursorViewModel.cs   bindings, load/save settings, defaults, diagnostics timer, Quit
  Views/MainWindow.xaml(.cs), ParameterControl.xaml(.cs), RadioCheckedToBoolConverter.cs
```

## 4. Architecture

### 4.1 Threads and rates

| Path | Thread | Rate |
|---|---|---|
| `KinectReader` → `KinectCursor.Kinect_OnTrackedBody` | WPF UI thread (the SDK raises `FrameArrived` on the thread that created the reader, which is the UI thread via the XAML DataContext) | ~30 Hz |
| `CursorOutputLoop.Run` | dedicated background thread `KinectCursorOutput`, AboveNormal priority | 8 ms tick (~125 Hz nominal, ~64 Hz at default timer resolution) |
| `safetyTimer` (frame-stall watchdog) | DispatcherTimer | 250 ms |
| `hoverTimer` (hover-to-click) | DispatcherTimer | HoverDuration |
| `diagnosticsTimer` (UI readout) | DispatcherTimer in the ViewModel | 200 ms |
| `SystemEvents.DisplaySettingsChanged` | SystemEvents thread, marshalled to the Dispatcher | on change |

The output loop is a thread, not a DispatcherTimer or `CompositionTarget.Rendering`, because
the app normally runs minimized and WPF stops rendering minimized windows.

### 4.2 Per body frame (`Kinect_OnTrackedBody`, ~30 Hz)

1. Ignore when `Mode == Disabled`. Record frame arrival for the watchdog. Compute `deltaTime`
   from the sensor's `RelativeTime`, clamped to [0.002, 0.2] s, default 1/30.
2. `BuildHandSnapshots`: for each hand, body-relative `Position` (hand − SpineBase, X right,
   Y up), `ForwardDistance` (SpineBase.Z − Hand.Z), `State`, `IsConfident` (High confidence),
   `PositionWeight` (1 tracked / 0.35 inferred / 0 not tracked, from hand and SpineBase),
   and `IsActivated` (activation zone with hysteresis, see 4.6).
3. If control is off (double clap) or a calibration sweep is running: record the calibration
   sample if capturing, run the gesture layer (so the clap can still be seen), update
   diagnostics, **return**. Nothing touches the machine.
4. Hand loop, **right hand first** (`i = 1` then `0`). The first activated hand becomes the
   controlling hand (`usedHandIndex`) and stays latched until it deactivates. For the controlling hand:
   - GripToPress: grip is resolved **before** the pointer target, so a fist that is only
     starting to close pins the cursor on the same frame.
   - Pointer target: `GetHandRelativePosition` → `CursorMapper.GetSmoothedOutputPosition`
     → `StationaryLock.Apply` (lock allowed only while no grip is held) →
     `PublishCursorTarget` (click freeze/anchor) → `ClampToOutputRect` → `outputLoop.SetTarget`.
   - For the non-controlling hand: in MoveGripPressing it drives the button; in
     MoveLiftClicking a raised hand clicks; otherwise its grip is released.
   - When the controlling hand deactivates: `EndControlSession` (release, reset filters, clear cursor state).
5. Hover timer on/off (HoverToClick only).
6. `RunGestureLayer`: fill `GestureContext` (controlling hand, second hand, `IsDragActive`,
   vocabulary flag, control gate) and call `GestureEngine.Update`.
7. `UpdateDiagnostics`.

### 4.3 High-rate output path (`CursorOutputLoop`)

- The frame handler publishes a target via `SetTarget` under a small lock. The loop reads it
  every tick and eases `current` toward it exponentially (τ = 15 ms, tick delta capped at 0.25 s).
- It calls `MouseControl.MoveTo` → `SetCursorPos` **only when the rounded pixel changes**, so
  a converged/locked pointer does not fight the physical mouse.
- `ClearTarget()` stops driving the cursor entirely (physical mouse is free) and arms a
  snap, so reacquisition jumps to the new target instead of gliding from a stale spot.
- `Start`/`Stop` are decided only in `KinectCursor.UpdateOutputLoopState()`: running iff
  `Mode != Disabled && controlEnabled && !calibration.IsCapturing`.
- **It is the only code that moves the cursor.**

### 4.4 Pointer movement vs discrete actions

Pointer position is a continuous stream: frame handler → `CursorOutputLoop` → `SetCursorPos`.
It deliberately does **not** go through `ActionRouter`; the `ActionRouter` header comment
explains why. Everything discrete (button down/up, clicks, right click, wheel, window switch,
control toggle) is a `ControlAction` executed by `ActionRouter`. Grip/click logic in
`KinectCursor` also emits `ControlAction`s (`LeftMouseDown/Up`, `LeftClick`) through the
router rather than calling `MouseControl` directly.

### 4.5 Gesture layer

- `IGestureRecognizer`: `Update(GestureContext, IActionSink)` + `Reset()`. Recognizers never
  touch Win32 or the cursor, never keep the context instance (it is reused every frame), and
  must write their own state **before** emitting (emitting `ToggleControl` re-enters and
  resets every recognizer).
- `GestureEngine.Update` priority (explicit in code):
  0. **Clap**: runs whenever a body is tracked, even with control off. It owns the on/off switch.
  1. If the control gate is closed (read live from `router.ControlGate`, because the clap may
     have just flipped it): reset session recognizers → Idle. Switching on takes effect on the
     *next* frame, so the re-enabling clap cannot also drive a gesture.
  2. No body / no controlling hand → reset, clear scroll remainder, Idle.
  3. State = Pointer. If the vocabulary is disabled (any mode other than **GripToPress**) →
     reset session recognizers and stop.
  4. **Lasso** (controlling hand) → `RightClick`. Stands down during a drag.
  5. **Swipe** (second hand) → `NextWindow`/`PreviousWindow`. Suppressed during a drag or while
     the clap owns the hands.
  6. **Scroll** (second hand) → `Scroll(notches)`. Suppressed during swipe cooldown or while the
     clap owns the hands. `State = Scroll` while armed.
- **Grip/drag is not a recognizer.** It lives in `KinectCursor` + `HandStateFilter` because it
  is tied to click anchoring. The engine only sees it as `IsDragActive`.
- "Second hand" = the other hand, **only if it is also activated**. The controlling hand is
  whichever activated first (right checked first each frame). **There are no fixed hand roles
  yet** (see backlog §9).
- `ResetControlSession()` resets lasso/swipe/scroll but **spares the clap**, because bringing
  the hands together can change the controlling hand. `Reset()` resets everything, clap included.

### 4.6 Body-relative coordinates and virtual-desktop mapping

- All gesture geometry is metres relative to **SpineBase**, so the control region moves with
  the user (leaning, reclining, sliding on a couch). No assumption about sensor mounting.
- Activation (`UpdateHandActivation`): engage when `Height ≥ ActivationMinHeight` **and**
  `ForwardDistance ≥ ForwardActivationDistance`. Disengage when either drops more than
  `ActivationReleaseMargin` (0.08 m) below its threshold. The height test stops a hand resting
  in the lap from engaging while reclined.
- Pointer frame (`GetHandRelativePosition`): body-relative position, X shifted by ±0.185 m
  (`GESTURE_X_OFFSET`) so each hand's rest position is centred, Y minus `PointerCenterHeight`.
- `CursorMapper.GetOutputPosition` = `OutputRect.Center + (input − InputRect.Center) × MoveScale × AlignScale`.
  - Uncalibrated: `InputRect = (-0.18, 1.65, 0.18, -1.65)`, `ScaleAlignment.LongerRange` gives
    one uniform scale taken from the desktop's longer axis (X on a wide desktop, i.e.
    desktop width / 0.36 m).
  - The vertical sign flip comes from the rect's top > bottom, so raising the hand raises the cursor.
- `OutputRect = VirtualScreen.GetBounds()` = `SM_[XY]VIRTUALSCREEN` / `SM_C[XY]VIRTUALSCREEN`
  in **physical pixels**, which can have a negative origin. This only works because
  `app.manifest` declares PerMonitorV2 DPI awareness. `SetCursorPos` is used (not SendInput
  absolute) because it takes signed physical pixels exactly.
- The target is clamped to the desktop before publishing (right/bottom exclusive).
- Display changes re-read the bounds and tear down the session (see §6).

### 4.7 Calibration layering (`PointerCalibration`)

- Opt-in via `UseCalibratedRange`. When off, mapping is identical to the original uniform scale.
- When on: `InputRect = BuildInputRect()` (width `HandRangeX`, height `HandRangeY`, centred at
  `HandCenterX`, vertically 0) with `ScaleAlignment.Both`, so X and Y scale independently. That is
  the fix for dual-monitor desktops.
- **`MoveScale` still multiplies in calibrated mode.** With calibration on, `MoveScale = 1`
  maps the swept rectangle exactly onto the desktop. A leftover `MoveScale` of 0.5 would need
  twice the reach.
- Vertical centre is owned solely by `PointerCenterHeight`. On `EndCalibration`, the sweep's
  vertical centre is **added to `PointerCenterHeight`**, not stored in the rect.
- Sweep: the Calibrate button starts capture (grips released, output loop stopped, actions
  off). Only activated hands with `PositionWeight == 1` contribute. Finish adopts the ranges
  if both are ≥ 0.05 m and turns calibrated mode on. Otherwise nothing changes. Finishing
  needs the physical mouse, because Kinect control is off during the sweep.
- `ApplyInputMapping()` resets smoothing, lock and cursor state whenever the mapping changes.
- `KinectCursor.CancelCalibration()` exists but nothing in the UI calls it. Selecting a
  control mode cancels an in-progress capture.

### 4.8 Pointer filtering chain (in order)

1. **One Euro filter** (`OneEuroVectorFilter`), time-aware using real `deltaTime`, isotropic (one
   cutoff from 2D speed). `MinCutoff = 15 Hz × (0.4/15)^Smoothing`, so Smoothing 0 → 15 Hz,
   0.5 → 2.4 Hz, 0.75 → 1 Hz, 0.8 → ~0.8 Hz, 1 → 0.4 Hz. The slider caps at 0.95.
   `Beta = SpeedResponsiveness × 0.0001`. `DerivativeCutoff = 1 Hz`. `alpha` is multiplied by
   `PositionWeight`, so inferred joints lean on history.
2. **Jitter dead zone** (`CursorMapper`, relative): output only moves once the filtered
   position leaves `JitterDeadzone` px from the last output.
3. **Stationary lock** (`StationaryLock`, absolute): speed smoothed with τ 0.12 s. Locks after
   `LockDwell` s within `LockRadius` px at ≤ 45 px/s (`StillSpeed`). Breaks beyond
   `BreakoutRadius` px or above 220 px/s (`BreakoutSpeed`). Release is a decaying offset
   (τ 0.10 s), never a snap. Disabled (smoothly released) while any grip is held.
4. **Click freeze / anchor** (`PublishCursorTarget`): see §5.
5. Clamp → output loop ease (τ 15 ms).

`BreakoutSpeed`, `StillSpeed` and the time constants are code constants, not settings.

## 5. Implemented functionality (COMPILE VERIFIED; hardware status in §7)

**Pointer / control**
- Body-relative pointer control with activation zone + hysteresis and a latched controlling hand.
- Adaptive One Euro filtering with Smoothing and Speed Responsiveness dials.
- Jitter dead zone, stationary lock (toggle + radius/dwell/breakout sliders).
- High-rate interpolated cursor output (separate thread, snap on reacquire, no redundant writes).
- DPI-correct virtual-desktop mapping across multiple monitors, including negative origins.
  WPF window rescales per-monitor (`Switch.System.Windows.DoNotScaleForDpiChanges=false`).
- Configurable pointer height, activation height, forward activation.
- Optional calibrated per-axis range (manual sliders or Calibrate sweep).
- Confidence weighting: inferred joints get 0.35 weight in the filter. Swipe ignores
  non-fully-tracked samples. Clap arming requires fully tracked joints.

**Clicking / dragging** (`HandStateFilter` + `KinectCursor`)
- Grip to press: Closed must hold 0.08 s **with High confidence** to press. Open must hold
  0.12 s to release, and release never waits on confidence. Unknown/NotTracked/Lasso frames
  are neutral. Unconfirmed candidates are dropped after 0.5 s.
- Click anchoring: the cursor pins as soon as a close is *pending*. After the press it stays
  frozen `ClickFreezeDuration`. Afterwards the offset between the pinned point and the hand is
  held constant for the whole drag, so the drag is anchored to the clicked spot. After release
  it decays (τ 0.12 s).
- Dragging = held `LeftMouseDown`. Stationary lock stands down during it.
- Other modes still present: Move only, Hover to click (`HoverRange` px / `HoverDuration` s
  → LeftClick), Move + grip pressing (other hand's fist holds the button), Move + lift
  clicking (other hand's height > 0.02 m relative to pointer frame → LeftClick).

**Gestures** (all thresholds in `GestureTuning`)
- **Lasso → right click** (GripToPress only): confident Lasso held 0.15 s. Re-arms after Open/Closed held 0.12 s. 0.6 s cooldown.
- **Second-hand scroll** (GripToPress only): second hand activated and steady (±0.05 m) for
  0.15 s arms scrolling and captures a **live neutral height** (frozen until disengaged).
  Rate control: `(|offset| − 0.03 m dead zone) × ScrollSpeed` notches/s, capped at 25/s,
  vertical only. Fractional notches accumulate in `ActionRouter`, and whole notches go out as `WHEEL_DELTA` multiples.
- **Horizontal swipe → window switch** (GripToPress only, second hand): ≥ `SwipeMinDisplacement`
  (0.25 m) within 0.40 s, average ≥ 0.9 m/s, vertical wander ≤ 0.12 m, ≥ 3 samples over
  ≥ 0.08 s. Toward the user's right (+X) → `NextWindow` (Alt+Tab), left → `PreviousWindow`
  (Shift+Alt+Tab), sent as one SendInput batch. 1.0 s cooldown that also suppresses scroll.
- **Double clap → toggle control** (every mode except Disabled): skeleton-based, not audio.
  Arm at ≥ 0.35 m 3D separation with both hands fully tracked. Contact ≤ 0.14 m within 0.40 s
  of arming. Both hands ≥ 0.10 m above SpineBase. 0.18 s refractory. Second clap within 1.2 s.
  While hands are close, or one clap is pending, swipe/scroll are suppressed.

**Diagnostics / UI** (`MainWindow.xaml`, 404×740, minimize-only)
- Scrollable list of 18 slider+textbox parameters: Movement scale, Cursor smoothing, Speed
  responsiveness, Jitter dead zone, Click freeze, Lock radius, Lock dwell, Breakout radius, Hand
  range X/Y, Hand centre X, Pointer height, Activation height, Forward activation, Scroll speed,
  Swipe distance, Hover-to-click range, Hover-to-click duration.
- Six control-mode radio buttons, Stationary lock and Calibrated range checkboxes, and a
  Calibrate/Finish button.
- Diagnostics panel (Consolas, refreshed 5×/s): tracking, control enabled/DISABLED, gesture
  state, control hand, both hand states, active gesture, clap progress, pointer
  Moving/Stationary/Locked + lock displacement, scroll neutral/offset, controlling-hand height
  and forward distance, desktop bounds, calibration status, last action.
- Default button (resets parameters, not the mode) and the upstream credit label.
- No tray icon, overlay, HUD or hotkeys.

**Settings persistence**: loaded on `Window_Loaded`, saved **only on `Window_Closed`**
(`Quit`). Defaults come from `Settings.settings`/`App.config`/`Settings.Designer.cs`, and the
Default button uses separate constants in the ViewModel.

## 6. Safety / reset behaviour

`ReleaseAllGrips()` sends `LeftMouseUp` for any held grip and resets the hand filters.
`ResetControlState()` clears session state (controlling hand, filters, gesture engine, context,
activation latches, cursor state + `outputLoop.ClearTarget()`) but **does not release
buttons**. Callers release first. `ClearCursorState()` is the choke point that also resets the
stationary lock.

| Trigger | What happens |
|---|---|
| Tracking lost (`OnLostTracking`, > 5 consecutive frames without the locked body) | hover off, ReleaseAllGrips, ResetControlState, diagnostics idle |
| Frame stall (no frames ≥ 1.0 s while a mode is active; watchdog every 0.25 s) | ReleaseAllGrips, ResetControlState, diagnostics idle |
| Controlling hand leaves activation zone | ReleaseGrip + EndControlSession (ReleaseAllGrips, reset smoothing/filters/session gestures, clear cursor) |
| New controlling hand acquired | BeginControlSession (ReleaseAllGrips first, then resets; clap is spared) |
| Non-controlling hand deactivates / loses position | its grip released |
| Double clap → control off | hover off, ReleaseAllGrips, ResetControlState, output loop stopped; **sensor and tracking keep running** |
| Double clap → control on | same reset, output loop restarted |
| Mode → Disabled | hover off, ReleaseAllGrips, ResetControlState, safety timer stop, output loop stop, **sensor closed** |
| Mode → another active mode | forces control on, cancels calibration, ResetControlState, loop/safety/sensor started. *No explicit ReleaseAllGrips*: a held grip is released on the next body frame (see §10) |
| Calibration start | ReleaseAllGrips, ResetControlState, output loop stopped |
| Calibration end / mapping change | ResetControlState / ApplyInputMapping resets |
| Display settings changed | re-read bounds, ReleaseAllGrips, ResetControlState |
| App exit (window closed) | Quit: save settings, then Mode = Disabled (same path as above) |
| Pointer-height change | smoothing + cursor state reset |

Scroll remainder is reset on disengage and full reset. Swipe cooldown survives the hand
leaving the zone on purpose, but not a full reset.

## 7. Hardware-tested state

**The user has run extensive physical Kinect testing.** Hardware verification only comes from
the user's reports. Compiling does not verify anything about hardware.

User-reported reference settings from the latest testing. These are working preferences,
**not code defaults**:

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

Actual code/persisted defaults (`Settings.settings`, `App.config`, ViewModel `DEFAULT_*`):
MoveScale 1, Smoothing **0.2 in Settings / 0.7 in ViewModel Default button**, SpeedResponsiveness 20,
JitterDeadzone 3, ClickFreeze 0.15, PointerCenterHeight 0.5, ForwardActivation 0.15,
ActivationMinHeight 0.25, ScrollSpeed 60, SwipeMinDisplacement 0.25, StationaryLock on / radius
15 / dwell 0.35 / breakout 35, UseCalibratedRange false, HandRange 0.5 × 0.3, HandCenterX 0,
HoverRange 20, HoverDuration 2, Mode 2 (GripToPress).

Snapshot of the most recently saved `user.config` (2026-09-18, one exe path). It differs from
the reference list, which shows values were still moving between sessions: MoveScale 0.65,
Smoothing 0.8, SpeedResponsiveness ~40, JitterDeadzone ~13.1, ClickFreeze ~0.35, PointerHeight
~0.37, ActivationHeight ~0.16, LockRadius ~9.7, LockDwell ~0.10, Breakout 35, HoverRange 25.61,
calibrated range off (saved ranges X 0.44 / Y 0.33 / centreX −0.02).

User-reported hardware findings:
- Pointer movement is dramatically smoother than the original application.
- Stationary lock works. Shortening lock dwell to ~0.23 s felt better.
- Grip click works, and drag works reliably.
- Lasso right-click works.
- Horizontal swipe / window switching works.
- Dynamic scroll works but still needs interaction refinement. It feels overly sensitive and
  demands care.
- Double clap toggles control. Double clap while dragging correctly releases the drag.
- Dual-monitor mapping works. Ergonomics/calibration can still improve.
- Windows 11: Kinect audio enhancements / microphone processing caused repeated Kinect
  disconnect/reconnect. Disabling the problematic audio enhancement stabilised the sensor. If
  the sensor starts cycling again, check this before debugging code.

## 8. Development / build-test loop

This is how the user works. Follow it for every meaningful change:

```
inspect relevant code
       ↓
make smallest coherent change
       ↓
build Debug   → must succeed
       ↓
build Release → must succeed
       ↓
the fresh bin\Debug and bin\Release exes are now the current test builds
       ↓
report the exact EXE paths + what needs physical testing
       ↓
user performs physical Kinect testing → feedback → next iteration
```

`code → build → new executable → physical test → feedback → next code change`

- Always label results as **COMPILE VERIFIED** (built, 0 errors) vs **HARDWARE VERIFIED** (the
  user tested it on the Kinect and said it works). Never promote one to the other on your own.
- Overwrite `bin\Debug` / `bin\Release` in place. Don't pile up manually copied test exes
  unless a comparison build is specifically useful. Say so and name it clearly if you keep one.
- Never commit build output. `bin/` and `obj/` stay ignored. Git tracks the source needed to
  reproduce the exe.
- **Settings are per exe path.** .NET stores `user.config` under
  `%LOCALAPPDATA%\KinectV2MouseControl\KinectV2MouseControl.exe_Url_<hash-of-path>\1.2.1.0\`.
  Debug and Release therefore keep **separate** tuned settings, and copying the exe elsewhere
  starts from defaults. Mention this when handing over a build if tuning matters.
- Settings are saved only when the window closes normally.
- New settings must be added in all of: `Settings.settings`, `Settings.Designer.cs`,
  `App.config`, the ViewModel (property, Load, Save, DEFAULT_ const + ResetToDefault), and
  `MainWindow.xaml` if user-facing.
- New `.cs` files must be added to the csproj `<Compile Include>` list or they won't build.

## 9. Backlog (NOT implemented; documented from hardware testing)

1. **Dedicated hand roles.** Right hand = pointer only. Left hand = secondary gesture/modifier
   only. The left hand must never become the pointer when the right is lowered. Today the
   controlling hand is simply whichever activates first, right checked first, latched per
   session, so the left hand *can* take the pointer.
2. **Left-hand gesture clutch.** Left hand open means passive. A closed left fist engages the
   secondary-gesture clutch. Only while the fist is intentionally held should left-hand
   movement drive scroll and horizontal swipe. Releasing the fist ends/resets the secondary
   gesture. Today scroll arms on a dwell whenever the second hand is activated and steady,
   with no fist requirement. Note that `HandStateFilter` debouncing and swipe/scroll neutral
   capture would need to key off the clutch.
3. **Scroll refinement.** Dynamic neutral helps, but secondary-hand scrolling still feels too
   sensitive. Rebuild it around the clutch.
4. **Calibration.** Comfortable reach across the whole dual-monitor desktop without
   exaggerated arm movement.

Longer-term (not started): control-center UI, HUD/overlay, voice, AI/agent commands, more
gestures, deeper Windows/app control.

## 10. Known gaps / observations from the code audit (not yet addressed)

- Mode change between two *active* modes does not call `ReleaseAllGrips()` directly. A held
  button is released on the next body frame (hand loop / `BeginControlSession`). Because
  `ResetControlState` clears `hasFrameArrived`, the stall watchdog would not catch it if
  frames stopped at that exact moment.
- No `AppDomain.UnhandledException` / `SessionEnding` handler, so a crash or forced kill
  mid-drag cannot send `LeftMouseUp`. Clean exit is covered.
- `ControlEnabledChanged` has no subscribers. `CancelCalibration()` has no caller.
  `CursorOutputLoop.Dispose()` is never called (Stop via Mode = Disabled is what runs).
- Selecting a mode in the UI sets `controlEnabled = true` without raising `ControlEnabledChanged`.
- Smoothing default mismatch: Settings 0.2 vs ViewModel Default button 0.7.
- `GestureDiagnostics` is written on the frame (UI) thread and read by a UI timer. It is
  lock-free on purpose.

## 11. Architectural invariants — DO NOT BREAK

1. **Single cursor-position writer.** Only `CursorOutputLoop` calls `MouseControl.MoveTo`/`SetCursorPos`.
2. **Never move cursor writes back into the Kinect body-frame handler.** The frame handler
   publishes targets; the loop writes.
3. **Preserve the high-rate output architecture**: background thread, time-based ease, write
   only on change, `ClearTarget` releases the mouse, and `UpdateOutputLoopState` is the one
   place that starts/stops it.
4. **Don't casually rewrite the working filter/click pipeline** (One Euro → dead zone →
   stationary lock → click freeze/anchor → clamp → loop, plus `HandStateFilter`). It is
   hardware-tuned. Change one stage at a time, and keep the order.
5. **No injected mouse-down may survive** tracking loss, control disable, sensor stall, app
   shutdown, display reconfiguration, controlling-hand handoff, mode change, calibration, or any
   equivalent teardown. Call `ReleaseAllGrips()` **before** `ResetControlState()`. Every
   `LeftMouseDown` must have a matching `handGrips[]` flag so it can be released.
6. **Recognizers emit semantic `ControlAction`s through `IActionSink`.** They never call Win32,
   `MouseControl`, `KeyboardControl` or the cursor.
7. **`ActionRouter` is the discrete-action boundary** for gestures now and voice/agent inputs
   later. Add capabilities as a `ControlActionType` + a router case. Keep pointer movement out of it.
8. **Double-clap disable keeps the sensor and body tracking running**, and the clap recognizer
   runs while control is off. Only `Mode = Disabled` closes the sensor. The clap must stay
   above the control gate in engine priority.
9. **Preserve body-relative tracking** (SpineBase-relative metres). Don't switch gestures or
   activation to raw camera space.
10. Recognizers must finish writing state before emitting (toggle re-enters and resets them).
    `GestureContext` is reused each frame and must not be retained. Avoid per-frame allocations
    on the 30 Hz path (`ControlAction` is a struct for this reason).
11. Keep the process DPI-aware (`app.manifest`) and map to `VirtualScreen.GetBounds()` physical
    pixels. Don't switch cursor output to SendInput absolute coordinates.
12. Time-based maths uses real `deltaTime` from sensor timestamps, not an assumed 30 Hz.
13. Clamp targets to the virtual desktop. Recompute bounds on display change.
14. Uncalibrated mapping must remain bit-for-bit the original uniform mapping; calibration is opt-in.
15. Avoid broad refactors for architectural cleanliness. Don't migrate frameworks
    (.NET Framework 4.8, legacy csproj, WPF) or redesign the UI unless explicitly asked.
16. Don't rename the assembly/exe or change AssemblyVersion without warning the user, because
    it orphans their saved settings.
17. **Never claim hardware behaviour is verified because the project compiles.**
18. Keep the upstream MIT license notice.
