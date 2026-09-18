# CLAUDE.md — Kinect Home OS

Standing context for Claude Code. Read this first; it is meant to save you from re-auditing
the repo every session. `AGENTS.md` holds the same rules in a more operational, checklist form
for Codex and other agents. If code and this file disagree, **the code wins**. Fix this file
in the same change. This file describes the *current* state; git history is the history.

---

## 1. Project identity and mission

This repo started as a fork of TangoChen's **KinectV2MouseControl**
(`https://github.com/TangoChen/KinectV2MouseControl`, MIT, © Jingzhou Chen, last upstream
release v1.2.1 in 2018). It has been heavily reworked and is growing into **Kinect Home OS**,
a Windows spatial gesture-control system built on an Xbox One Kinect (Kinect v2). The control
engine went through a stabilization phase; on top of it now sits **KINECT-OS**, the control
center UI (shell window, floating widget, tray icon, help drawer, activity feed, local voice
commands, action catalog, AI placeholder). The UI is COMPILE VERIFIED + offscreen-rendered
only (§8); the engine's hardware status is unchanged (§5, §7).

Conceptual pipeline (this matches the current code):

```
Kinect/body input            KinectReader (body selection), KinectBodyHelper
      ↓
body-relative tracking       HandSnapshot / GestureContext (metres from SpineBase)
      ↓
gesture recognition          PointerStabilizer + grip (right hand), SecondaryClutch + recognizers (left hand), clap
      ↓
semantic actions             ControlAction  →  ActionRouter
      ↓
Windows control              MouseControl / KeyboardControl (SendInput), CursorOutputLoop (SetCursorPos)
```

Inputs other than gestures plug in at the `ActionRouter`/`ControlAction` boundary: the local
voice engine already does (`VoiceViewModel` → `ActionRouter.Execute(action, "voice")`), the
Actions page does ("control center"), and the AI section is a placeholder for the same path.
`ControlAction.Parameter` carries the `LaunchApp` target.

**Longer-term directions, not in code:** custom user-defined voice commands, the assistant
layer itself (provider/model, workflows), HUD beyond the compact widget, more gestures,
"move window to display".

Names still inherited from upstream: assembly/exe `KinectV2MouseControl`, namespace
`KinectV2MouseControl`, AssemblyVersion `1.2.1.0`, the upstream credit line (now in
Settings → About), and `README.md` (it still describes upstream v1.2.1). The window title is
now "KINECT-OS". **Do not rename the assembly/exe or bump the version unless asked.** Renaming
the exe or bumping the version moves where Windows stores `user.config` (§8).

## 2. Technical stack

| Item | Actual value |
|---|---|
| OS | Windows (developed on Windows 11) |
| Sensor | Xbox One Kinect / Kinect v2, via the Kinect for Windows adapter |
| SDK | Kinect for Windows SDK 2.0 (`KINECTSDK20_DIR` = `C:\Program Files\Microsoft SDKs\Kinect\v2.0_1409\`) |
| UI | WPF: shell `MainWindow` (custom chrome + DWM acrylic) with 8 page UserControls, `OverlayWindow` widget, WinForms `NotifyIcon` tray, view models under `ViewModels/` |
| Language | C# (C# 7-level features) |
| Framework | .NET Framework **4.8** |
| Project | Legacy, **non-SDK-style** `.csproj` (ToolsVersion 12.0), explicit `<Compile Include>` list |
| Build | MSBuild from Visual Studio 2022 **Preview** |
| Platform | AnyCPU, Debug and Release only |
| References | `Microsoft.Kinect` 2.0.0.0 (HintPath `$(KINECTSDK20_DIR)Assemblies\Microsoft.Kinect.dll`, `Private=False`), plus framework assemblies: `System.Runtime.Serialization` (profile JSON), `System.Speech` (local voice), `System.Windows.Forms` + `System.Drawing` (tray icon only) |
| Manifest | `app.manifest`: PerMonitorV2 DPI awareness, asInvoker |

**This is not a Node/npm/web project.** Do not run `npm` or `dotnet build`. There are no test
projects.

### Build commands (verified on this machine)

PowerShell:
```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Preview\MSBuild\Current\Bin\MSBuild.exe" "C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl.sln" /t:Build /p:Configuration=Debug /v:minimal /nologo
& "C:\Program Files\Microsoft Visual Studio\2022\Preview\MSBuild\Current\Bin\MSBuild.exe" "C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl.sln" /t:Build /p:Configuration=Release /v:minimal /nologo
```

Git Bash (use `-p:` not `/p:`; MSYS rewrites `/p:` as a path):
```bash
"/c/Program Files/Microsoft Visual Studio/2022/Preview/MSBuild/Current/Bin/MSBuild.exe" src/KinectV2MouseControl.sln -t:Build -p:Configuration=Debug -v:minimal -nologo -clp:Summary
"/c/Program Files/Microsoft Visual Studio/2022/Preview/MSBuild/Current/Bin/MSBuild.exe" src/KinectV2MouseControl.sln -t:Build -p:Configuration=Release -v:minimal -nologo -clp:Summary
```

- Target is **0 errors / 0 warnings**. Use `-t:Rebuild` for a guaranteed fresh output.
- MSB3027/MSB3021 ("file is locked") means the app is running. Ask the user to close it.
- **Don't launch the exe to "test" it without asking.** If the user is in front of the
  sensor, a launch really does take their cursor.
- **UI verification without launching: `KinectV2MouseControl.exe --ui-smoke-test`.** It
  loads the shell, every page and the widget, lays them out, renders PNGs and exits (0 = pass,
  2 = a view failed). It never shows a window, opens the sensor, starts voice, creates the tray
  icon or writes `runtime.log`. Report: `%LOCALAPPDATA%\KinectHomeOS\ui-smoke-test.txt`;
  renders: `%LOCALAPPDATA%\KinectHomeOS\ui-preview\*.png` (open them with the Read tool).
  Run it after every XAML change: a missing `StaticResource` or a bad template only fails at
  load time, which this catches and a build does not.

### Executable paths

- Debug: `C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl\bin\Debug\KinectV2MouseControl.exe`
- Release: `C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl\bin\Release\KinectV2MouseControl.exe`

`bin/` and `obj/` are git-ignored. `executables/KinectV2MouseControl_EXE.zip` is **upstream's
2018 v1.2.1 binary**. It is historical, not the current build.

### Runtime files

| File | Location | Notes |
|---|---|---|
| Last-used settings | `%LOCALAPPDATA%\KinectV2MouseControl\KinectV2MouseControl.exe_Url_<hash>\1.2.1.0\user.config` | .NET user settings, **per exe path** (Debug and Release differ). Saved only on normal close. Tuning + mode + the UI settings (`CompactOnMinimize`, `StartCompact`, `OverlayAlwaysOnTop`, `OverlayLeft/Top`, `VoiceEnabled`, `VoiceWakeWord`) |
| Profiles | `%LOCALAPPDATA%\KinectHomeOS\profiles.json` | 3 slots, shared by all builds. Atomic write. Unreadable file is moved to `profiles.json.bad`. A slot rename is saved immediately |
| Runtime log | `%LOCALAPPDATA%\KinectHomeOS\runtime.log` (+ `runtime.prev.log`) | Human-rate event log, fresh each launch. **Ask the user for it when diagnosing intermittent issues** |
| UI smoke test | `%LOCALAPPDATA%\KinectHomeOS\ui-smoke-test.txt`, `ui-preview\*.png` | Written by `--ui-smoke-test` only |

## 3. Repository map

```
CLAUDE.md, AGENTS.md          agent context
README.md, LICENSE.md         upstream README (outdated) and MIT license (keep the notice)
executables/                  upstream 2018 release zip (historical)
src/KinectV2MouseControl.sln
src/KinectV2MouseControl/
  KinectV2MouseControl.csproj legacy csproj: new .cs files MUST be added to <Compile Include>, new .xaml to <Page Include>
  App.xaml(.cs)               merges Themes/*, creates MainWindow (no StartupUri), --ui-smoke-test, process-level fail-safes
  App.config, app.manifest    settings defaults + DPI switch; DPI awareness
  Properties/Settings.*       persisted user settings (keep .settings, Designer.cs, App.config in sync)
  Themes/
    Palette.xaml              fonts, colours, brushes, glow effects (the visual identity)
    Icons.xaml                stroke icon Geometry resources (IconHome, IconVoice, ...)
    Controls.xaml             converters, text/card/button/toggle/slider/chip/nav/scrollbar/tooltip styles, ActivityItemTemplate
  ViewModels/
    ShellViewModel.cs         navigation, help drawer, compact state, activity feed, UI settings, commands; owns the rest
    KinectCursorViewModel.cs  engine settings + Load/Save/defaults/profiles/calibration + LiveStatus refresh (200 ms)
    LiveStatus.cs             bindable engine snapshot (ControlState Off/Standby/Ready/Active, hands, gestures, signal, calibration)
    VoiceViewModel.cs         voice enable/wake word/mic status/command groups; marshals recognitions to the UI thread
    ActionsViewModel.cs       catalog grouped by category with Run commands
    DisplaysViewModel.cs      monitor rects + live cursor dot; hand-space geometry (reach rect, thresholds, hand dots)
    ProfileSlotViewModel.cs   one slot: name (rename persists), state, summary, Load/Save
    ObservableObject.cs       INotifyPropertyChanged base + RelayCommand
  Views/
    MainWindow.xaml(.cs)      the shell: WindowChrome + acrylic, rail, header pills, page host, help drawer; compact/tray wiring
    OverlayWindow.xaml(.cs)   floating compact widget (status orb, headline, hands, voice, power/expand)
    Pages/*.xaml(.cs)         HomePage, GesturesPage, VoicePage, ActionsPage, DisplaysPage, ProfilesPage, SettingsPage, AiPage
    Controls/TuningSlider     labelled slider + value box + help glyph (HelpKey must match a ControlHelp title)
    Controls/IconView.cs      draws an Icons.xaml geometry in the inherited Foreground
    ControlHelp.cs            single source of control explanations (tooltips, drawer, Settings guide)
    HelpHub.cs                static hover/pin routing from any control to the drawer
    Converters.cs             BoolToValue, Visibility, state/kind→brush, IconKey, Scale, Percent, Format...
    WindowBackdrop.cs         DwmSetWindowAttribute: dark mode, rounded corners, acrylic (with opaque fallback)
    TrayIcon.cs               NotifyIcon with a generated icon; Open / Compact / Control toggle / Quit
    RadioCheckedToBoolConverter.cs  mode chips; ConvertBack returns Binding.DoNothing when unchecked
  Models/
    KinectCursor.cs           THE orchestrator: frame handler, pointer session, grip/click, safety, calibration glue, settings batches
    CursorControlInput/
      KinectReader.cs         sensor open/close/availability, scored body selection + degraded-body switch
      KinectBodyHelper.cs     body-relative geometry, joint weights/states, hand state/confidence
      HandStateFilter.cs      debounced open/closed decision (grip, and inside the clutch)
      PointerStabilizer.cs    Waiting → Stabilizing → Active pointer sessions, glitch rejection
    CursorMapper/
      CursorMapper.cs         mapping, pre-filter monitor clamp, One Euro filter, soft jitter dead zone, noise metric
      OneEuroFilter.cs        LowPassFilter + OneEuroVectorFilter (with Seed)
      StationaryLock.cs       absolute pointer lock with hysteresis + smooth release
      PointerCalibration.cs   calibrated per-axis range + guided 5-point capture
      MapperStructs.cs        MRect / MVector2
    CursorControlOutput/
      CursorOutputLoop.cs     background ~125 Hz thread, sole SetCursorPos caller
      MouseControl.cs         SendInput buttons/wheel, SetCursorPos wrapper, injected-down tracking
      KeyboardControl.cs      Alt+Tab / Shift+Alt+Tab
      Win32Input.cs           SendInput structs/constants
      VirtualScreen.cs        VirtualScreen (bounds, monitor rects) + DesktopLayout (nearest-monitor clamp)
    Actions/
      ControlAction.cs        enum (mouse, scroll, windows, media, control gate, LaunchApp), Describe(), IActionSink, IControlGate
      ActionRouter.cs         Execute(action, source); LastSource; key chords via KeyboardControl; LaunchApp
      ActionCatalog.cs        ActionDescriptor list for the Actions page + voice grammar (implemented vs planned)
    Voice/
      VoiceCommand.cs         VoiceCommand + built-in VoiceCommandCatalog (phrase/aliases → catalog action)
      IVoiceEngine.cs         backend contract
      LocalVoiceEngine.cs     System.Speech closed-grammar recognizer (wake word + phrases), off by default
      AudioInputDevices.cs    waveIn device names for the mic card
    Diagnostics/
      RuntimeLog.cs           event log on disk (Suspend() for the smoke test)
      ActivityLog.cs          in-memory recent-activity feed (coalescing), any thread
    Gestures/
      GestureContext.cs       HandSnapshot + per-frame context, fixed hand-role constants
      GestureEngine.cs        clap → gate → lasso → clutch → swipe → scroll
      SecondaryClutch.cs      left-fist clutch (SecondaryGestureArmed)
      GestureTuning.cs        all gesture/activation/clutch/scroll/stabilization thresholds
      GestureDiagnostics.cs   live readout model
      IGestureRecognizer.cs   recognizer contract + GestureState enum
      Recognizers/            Clap, Lasso, Scroll, Swipe
    Profiles/ProfileStore.cs  TuningProfile / ProfileSlot / ProfileFile + JSON store
```

## 4. Architecture

### 4.1 Threads and rates

| Path | Thread | Rate |
|---|---|---|
| `KinectReader` → `KinectCursor.Kinect_OnTrackedBody` | WPF UI thread (the SDK raises events on the thread that created the reader) | ~30 Hz |
| `CursorOutputLoop.Run` | background thread `KinectCursorOutput`, AboveNormal | 8 ms tick |
| `safetyTimer` (stall watchdog) | DispatcherTimer | 250 ms |
| `hoverTimer` | DispatcherTimer | HoverDuration |
| `diagnosticsTimer` (LiveStatus refresh, diagnostics text, calibration polling, `StatusRefreshed`) | DispatcherTimer in the ViewModel | 200 ms |
| `SystemEvents.DisplaySettingsChanged` | SystemEvents thread, marshalled to the Dispatcher | on change |
| `sensor.IsAvailableChanged` | UI thread | on change |
| `LocalVoiceEngine` events (`Recognized`, `StateChanged`) | System.Speech worker thread, marshalled by `VoiceViewModel` via `Dispatcher.BeginInvoke` before `ActionRouter.Execute` | on speech |
| `ActivityLog.EntryAdded` | posting thread (UI in practice); `ShellViewModel` marshals into the ObservableCollection | on event |
| Tray icon menu / double-click | WinForms message loop on the UI thread | on click |

### 4.2 Fixed hand roles

`GestureContext.PointerHand = RightHand`, `SecondaryHand = LeftHand`.
- **Right hand:** pointer, grip press/drag, click anchoring/freeze, lasso right click, hover click.
  It is the only hand that can ever own the pointer.
- **Left hand:** scroll and swipe, and only while the left-fist clutch is armed (GripToPress
  mode). In the legacy two-hand modes it is the clicking hand (MoveGripPressing: left fist holds
  the button; MoveLiftClicking: lifting clicks), and it only acts while a right-hand pointer
  session is active.
- If the right hand leaves the activation zone, the pointer goes idle. There is no fallback to
  the left hand and no hand handoff.

### 4.3 Per body frame (`Kinect_OnTrackedBody`, ~30 Hz)

1. Ignore when `Mode == Disabled`. Record arrival for the watchdog. `deltaTime` comes from the
   sensor `RelativeTime`, clamped to [0.002, 0.2] s (default 1/30). Update frame statistics.
2. `BuildHandSnapshots`: per hand, `Position` (hand − SpineBase), `ForwardDistance`, `State`,
   `IsConfident`, `PositionWeight` (1 / 0.35 inferred / 0), `JointState`, and `IsActivated`
   (activation zone with hysteresis).
3. If control is off (double clap) or a calibration capture is running: feed the calibration
   capture if capturing (right hand only), run the gesture layer (so the clap is still seen),
   update diagnostics, **return**.
4. `UpdatePointerHand` (right hand only), see 4.4.
5. `UpdateSecondaryHandClicking` (left hand, legacy two-hand modes only; otherwise releases its grip).
6. Hover timer, `RunGestureLayer`, `UpdateDiagnostics`.

### 4.4 Pointer sessions and startup stabilization (`PointerStabilizer`)

`usedHandIndex` is `PointerHand` only while a session is **Active**.
- **Waiting**: the right hand is not activated. There is no cursor output.
- **Stabilizing**: the right hand is activated. The session needs `PointerSettleTime`
  (default 0.25 s, UI slider "Pointer settle") and ≥ `PointerSettleMinFrames` (5) consecutive
  good samples. A good sample has the hand joint `Tracked` (not Inferred), SpineBase usable,
  and hand speed ≤ `PointerMaxHandSpeed` (6 m/s) versus the previous sample. Any bad sample
  restarts the count. There is no cursor output and no grip during this phase.
- **Active**: `BeginControlSession(seed)`:
  1. release grips;
  2. reset the filters, lock, gestures and cursor state;
  3. `CursorMapper.SeedSmoothing(mean of last 3 good samples)`, which starts the One Euro filter
     and dead-zone state at that position with zero velocity;
  4. the first `SetTarget` makes the output loop **snap once**. It snaps because `ClearTarget`
     armed it; there is no glide from a stale position.
- **While Active**, each sample is checked by `CheckActiveSample`:
  - a sample moving faster than 6 m/s is **skipped as a glitch**. The previous target is held,
    and the skipped time carries into the next filter step;
  - more than `PointerMaxGlitchFrames` (3) glitches in a row ends the session and it
    re-stabilizes.
- Every teardown path resets the stabilizer to Waiting, so reacquisition always re-stabilizes.

### 4.5 Pointer filtering chain (in order, Active sessions only)

1. `GetOutputPosition` = `OutputRect.Center + (input − InputRect.Center) × MoveScale × AlignScale`.
2. **Pre-filter clamp** to the nearest real monitor (`DesktopLayout.Clamp`). This removes the
   "sticky edge": the filter never chases a target beyond the screen or into a dead area
   between mismatched monitors.
3. **One Euro filter**:
   - `MinCutoff = 15 Hz × (0.4/15)^Smoothing`;
   - `Beta = SpeedResponsiveness × 0.0001`;
   - `DerivativeCutoff = 1 Hz`;
   - `alpha` is multiplied by `PositionWeight`.
4. **Soft jitter dead zone** (`JitterDeadzone`):
   - within the radius the output holds still;
   - beyond it, the output trails the filtered position by `r²/distance`. That is continuous at
     the boundary and falls toward zero lag at speed;
   - this replaced the old all-or-nothing jump (§6).
5. **Stationary lock**, tuned by these settings/constants:
   - locks after `LockDwell` s within `LockRadius` px at ≤ 45 px/s;
   - breaks beyond `BreakoutRadius` px or above 220 px/s;
   - on release, the offset decays with τ 0.10 s;
   - stood down while any grip is held.
6. **Click freeze / anchor** (`PublishCursorTarget`).
7. `SetCursorTarget` → clamp to the nearest monitor again → `outputLoop.SetTarget`.

`CursorMapper.ResidualNoise` is the RMS of (raw − filtered), τ 1 s. While the hand is still,
it measures tracking noise and is shown in diagnostics.

### 4.6 High-rate output path (`CursorOutputLoop`)

- The frame handler publishes a target under a lock. The loop eases toward it
  (τ 15 ms, tick delta ≤ 0.25 s).
- It calls `SetCursorPos` only when the rounded pixel changes.
- `ClearTarget()` hands the cursor back to the physical mouse and arms a snap for the next target.
- `KinectCursor.UpdateOutputLoopState()` is the one start/stop decision:
  `Mode != Disabled && controlEnabled && !calibration.IsCapturing`.
- It is the only code that moves the cursor.

### 4.7 Discrete actions

- Everything discrete goes through `ActionRouter.Execute(ControlAction)`:
  - LeftMouseDown/Up, LeftClick, RightClick;
  - Scroll (fractional notches accumulated, whole notches sent);
  - NextWindow/PreviousWindow (Alt+Tab / Shift+Alt+Tab batches);
  - ToggleControl.
- Grip/click code in `KinectCursor` emits actions through the router too.
- Pointer movement never goes through the router.

### 4.8 Gesture engine (`GestureEngine.Update`), priority

0. **Clap** runs whenever a body is tracked, even with control off. It owns the on/off switch.
1. **Gate closed** (read live from `router.ControlGate`): reset the session recognizers and the
   clutch → Idle. Switching on takes effect next frame.
2. **No body or no Active pointer session** → reset session recognizers + clutch → Idle.
3. Pointer state. If the vocabulary is disabled (any mode except **GripToPress**) → reset
   session recognizers and stop.
4. **Lasso** (right hand) → `RightClick`. Stands down during a drag.
5. **SecondaryClutch** (left hand):
   - decides `context.IsSecondaryGestureArmed`, and is eligible only while the left hand is activated;
   - releasing it resets the scroll accumulation. The recognizers reset themselves when they see
     the clutch drop.
6. **Swipe** (clutched left hand) → Next/PreviousWindow. Suppressed during a drag or while the
   clap owns the hands.
7. **Scroll** (clutched left hand). Suppressed during swipe cooldown or while the clap owns the hands.

- `ResetControlSession()` resets lasso/swipe/scroll/clutch but spares the clap.
- `Reset()` resets everything.
- Recognizers never call Win32. They emit via `IActionSink`, and must finish writing state
  before emitting.

### 4.9 Left-fist clutch (`SecondaryClutch`)

A `HandStateFilter` configured from `GestureTuning`:
- Engage: a confident `Closed` held for `ClutchEngageDuration` (0.10 s).
- Release: `Open` held for `ClutchReleaseDuration` (0.15 s). Release never waits on confidence.
- Unknown/NotTracked/Lasso frames are neutral.
- Extra release after `ClutchLossTimeout` (0.40 s) with no `Closed` observed at all.
- Immediate release if the left hand leaves the activation zone or loses position, or the
  pointer session ends.
- Diagnostics show `Off / Open / Closing / ARMED`.
- An open, raised or moving left hand does nothing.

### 4.10 Scroll (clutched left hand)

1. The clutch arms.
2. The hand (height/X smoothed with τ 0.06 s) must stay within `ScrollEngageSteadyRadius`
   (0.03 m) for `ScrollEngageDwell` (0.20 s). Moving re-anchors the dwell.
3. **Neutral = mean height over the dwell**, frozen for the whole engagement. This prevents
   bursts when the fist closes mid-movement, and prevents drift.
4. Rate control:
   `rate = ScrollSpeed × R × ((|offset| − 0.03 dead zone) / R)^ScrollCurve`, with `R = 0.10 m`.
   - The rate is capped at 25 notches/s.
   - With `ScrollCurve 1` it is the original linear rate; the default 1.5 is gentler near
     neutral and faster far out.
   - Sign: raising the fist scrolls up unless `InvertScroll` is on.
5. Scrolling pauses while horizontal hand speed > 0.6 m/s, so a swipe takes priority.
6. Opening the fist stops scrolling at once and discards neutral.

### 4.11 Swipe (clutched left hand)

- Unchanged thresholds: ≥ `SwipeMinDisplacement` within 0.40 s, ≥ 0.9 m/s average, vertical
  wander ≤ 0.12 m, ≥ 3 samples over ≥ 0.08 s.
- +X → NextWindow, −X → PreviousWindow.
- 1.0 s cooldown, which also suppresses scroll.
- Only runs while the clutch is armed. The clutch dropping clears the history; the cooldown
  keeps running.

### 4.12 Body-relative coordinates, mapping and calibration

- **Geometry:** all gesture geometry is metres relative to SpineBase.
- **Activation:** `Height ≥ ActivationMinHeight` and `ForwardDistance ≥ ForwardActivationDistance`.
  Release is 0.08 m below either threshold.
- **Pointer frame:** body-relative position, X ±0.185 m per hand, Y − `PointerCenterHeight`.
- **Uncalibrated mapping:**
  - InputRect `(-0.18, 1.65, 0.18, -1.65)`, `ScaleAlignment.LongerRange`, × `MoveScale`;
  - the mapping geometry is unchanged from the original.
- **Calibrated mapping (`UseCalibratedRange`):**
  - InputRect `BuildInputRect()` (HandRangeX × HandRangeY centred on HandCenterX, vertical 0),
    `ScaleAlignment.Both`;
  - **`MoveScale` is ignored** (forced to 1). The rectangle alone defines reach, and a leftover
    Movement Scale can no longer push the edges out of reach;
  - the vertical centre is owned by `PointerCenterHeight`;
  - unticking "Calibrated range" reverts instantly.
- **Guided capture** (`Calibrate` button; right hand only; only activated, fully tracked samples):
  - Five points in order: centre, left, right, top, bottom.
  - Each point is the mean of a 0.6 s hold within 3 cm.
  - Each extent must be ≥ 0.08 m from centre in its own direction.
  - Result:
    - `HandRangeX = (right − left) × 0.9`;
    - `HandRangeY = (top − bottom) × 0.9`;
    - `HandCenterX = midpoint`;
    - `PointerCenterHeight += vertical midpoint`;
    - calibrated mode turns on.
  - The 0.9 factor is a 5% edge assist per side.
  - Tiny ranges are rejected and the previous mapping is kept.
  - While capturing, control output is off; the button reads "Cancel".
  - The linear mapping was kept deliberately. A piecewise mapping pinning the captured centre was
    rejected because it puts a gain change in the most-used area. The centre point is used for
    validation and reported in the result text.
- **Output space:**
  - `DesktopLayout` = virtual-desktop bounds (`SM_*VIRTUALSCREEN`) plus monitor rects
    (`EnumDisplayMonitors`), all physical pixels (needs `app.manifest` PerMonitorV2);
  - negative origins are supported; no primary-monitor assumption;
  - re-captured on display change.

### 4.13 Body selection and sensor availability (`KinectReader`)

- **Locking a body:**
  - Locks only a body with ≥ 3 of 6 core joints (Head, SpineShoulder, SpineMid, SpineBase,
    ShoulderL/R) `Tracked`.
  - Prefers the highest score, then the nearest body.
  - Falls back to the best available body after 30 frames.
- **Degraded body:** if the locked body scores ≤ 1 for 30 frames while another body scores
  ≥ 4, the lock drops (`OnLostTracking` → full reset) and the good body is picked up.
- **Lost body:** missing > 5 frames → `OnLostTracking`.
- **Sensor unavailable** (`IsAvailableChanged`) → immediate `OnLostTracking`.

### 4.14 Settings, profiles, help

- **Settings:** loaded on `Window_Loaded` (mode last, so the sensor opens after every value is
  in place), saved on normal close.
- **Settings batches:** profile load and Default go through `KinectCursor.ApplySettings(action)`:
  1. release grips;
  2. cancel calibration;
  3. apply;
  4. release again;
  5. `ResetControlState`;
  6. `ApplyInputMapping`;
  7. `UpdateOutputLoopState`.
- **Profiles:**
  - `TuningProfile` holds every tuning and calibration value. It holds no control mode and no
    runtime state.
  - All members are nullable, so older profiles leave newer settings untouched.
  - Save asks before overwriting a non-empty slot.
  - Status shows the active profile and "(modified)" after any tuning change.
- **Help:**
  - `ControlHelp` is the single source of explanations (what, higher, lower, too high, too low).
  - `TuningSlider` looks its entry up by `HelpKey` (defaults to `Label`); plain toggles/buttons
    use `HelpBinding.Attach(element, title)` (in GesturesPage.xaml.cs). Both set the tooltip and
    feed `HelpHub`, so **new controls need a `ControlHelp` entry whose Title equals the key**.
  - `HelpHub.Show(title, isExplicit)`: hover = preview in the drawer if it is open; the ? glyph
    = open and pin. The drawer is part of `MainWindow`; the full guide is on the Settings page.

### 4.15 KINECT-OS shell (UI layer)

- `App.OnStartup` creates `MainWindow` (no `StartupUri`). `MainWindow` creates one
  `ShellViewModel` (its DataContext) which owns `KinectCursorViewModel` (`Engine`),
  `VoiceViewModel`, `ActionsViewModel`, `DisplaysViewModel`, the activity collection and the
  UI settings. Pages bind `Engine.*`, `Status.*` (= `Engine.Status`), `Voice.*`, `Actions.*`,
  `Displays.*`. Bindings only see **properties**: a model with public fields renders blank.
- **Navigation:** `ShellViewModel.Sections` (NavItem: Section, Title, Subtitle, IconKey,
  Badge) in a ListBox; `SelectedSection` → `CurrentSection`; `MainWindow.ShowPage` swaps the
  pre-built page UserControl into `PageHost` with a fade (`EnablePageTransitions`).
  `NavigateCommand` accepts a NavItem, a `ShellSection` or its name as a string.
- **Chrome:** `WindowChrome` (CaptionHeight 52, GlassFrameThickness -1, no Aero buttons) over
  a `SingleBorderWindow`; `WindowBackdrop.TryApply` in `SourceInitialized` asks DWM for dark
  mode, rounded corners and acrylic; on success `Backdrop.Background` switches from the opaque
  gradient to the translucent tint. Root margin 7 when maximized. Header controls carry
  `WindowChrome.IsHitTestVisibleInChrome`.
- **Live status:** `KinectCursorViewModel.UpdateStatus()` (timer + engine events) fills
  `LiveStatus`; `ControlState` = Off (mode Disabled) / Standby (control off) / Ready /
  Active (pointer session). `Headline`/`Subline` are the one-line summaries used by the rail
  footer, tray tooltip and widget.
- **Master toggle:** `Engine.IsControlEnabled` (TwoWay) → `KinectCursor.SetControlEnabled(value,
  "control center")`; `ControlEnabledChanged` raises it back when a clap flips it.
- **Compact mode:** `ShellViewModel.WindowRequest` ("compact" / "expand" / "quit") is handled
  by `MainWindow`: `EnterCompact` shows `OverlayWindow` at the remembered position and hides
  the main window; `ExitCompact` reverses it. Minimize → compact when `CompactOnMinimize`.
  The widget is `AllowsTransparency`, `Topmost` bound to `OverlayAlwaysOnTop`, never activated,
  draggable, double-click expands; position persists via `OverlayLeft/Top`.
- **Tray:** created on `Loaded`, disposed on `Closed`; Open / Compact / toggle control / Quit.
- **Quit path:** window Close → `ShellViewModel.Quit()` → voice off, UI settings saved,
  `Engine.Quit()` (SaveSettings, Mode = Disabled via the setter, log). The X button quits;
  minimize does not.
- **Activity feed:** engine posts via `ActivityLog.Post` at the existing RuntimeLog sites
  (mode, control gate, calibration, display change, stall; KinectReader: sensor availability,
  body lock/loss; router actions via `KinectCursor.PostActionActivity`, scroll coalesced; VM:
  profiles/defaults; voice). Capacity 200, newest first in `Shell.Activity`.

### 4.16 Voice (local, off by default)

- `VoiceCommandCatalog.BuiltIn` binds phrases + aliases to `ActionCatalog` descriptors; only
  descriptors with `IsImplemented` enter the grammar. `LocalVoiceEngine` builds one closed
  `Grammar`: optional wake word (`VoiceWakeWord`, default "Kinect") followed by `Choices` of
  `SemanticResultValue(phrase, commandId)`; confidence ≥ 0.62; `SetInputToDefaultAudioDevice`.
- `VoiceViewModel.IsEnabled` starts/stops the engine (`ApplyDeferredStartup` restores the
  persisted state after the window is up). Recognitions execute through
  `Engine.ExecuteAction(action, "voice")`; descriptors with `ShellCommand` ("open", "compact",
  "calibrate") are raised to the shell instead.
- Windows 11 + Kinect mic audio enhancements can make the sensor reconnect; the Voice page
  says so. Voice never moves the pointer.

### 4.17 Action catalog and the extended vocabulary

- `ControlActionType` gained `EnableControl/DisableControl`, window management (Win+Up/Down/
  Left/Right, Win+D, Win+Tab, Alt+F4), media/volume keys and `LaunchApp` (shell-executes
  `Parameter`). All go through `KeyboardControl.Chord/Tap` (one SendInput batch) or
  `Process.Start`. `IControlGate.SetControlEnabled(bool, source)` was added.
- `ActionRouter.Execute(action, source)` records `LastSource` ("gesture" default, "voice",
  "control center"); `ActionExecuted` handlers read it.
- `ActionCatalog.All` is the human-readable index (Pointer, Windows, WindowManagement, Media,
  System, Apps, Intelligence) with `IsImplemented`, `CanRunFromUi` (mouse-button actions and
  Close window are not runnable from the page), trigger text and optional `ShellCommand`.
  **Adding an action = enum member + router case + catalog descriptor (+ voice phrase).**

## 5. Implemented functionality

Status key: **HW** = hardware verified by the user. **CV** = compile verified only, awaiting
physical testing.

**Pointer / control**
- HW: body-relative pointer, One Euro filter, speed responsiveness, stationary lock,
  high-rate output, virtual-desktop/DPI mapping, activation geometry.
- CV:
  - fixed right-hand pointer (no left fallback);
  - startup/reacquisition stabilization + seeding;
  - glitch rejection;
  - soft (continuous) jitter dead zone, which replaced the hard dead zone that was HW;
  - pre-filter nearest-monitor clamp;
  - MoveScale ignored in calibrated mode;
  - guided 5-point calibration, which replaced the free sweep;
  - scored body selection + degraded-body switch + sensor-availability reset.

**Clicking / dragging**
- HW: grip press (0.08 s confident close / 0.12 s open), click anchoring + freeze, drag, safe
  release.
- The legacy modes Move only, Hover to click, Move + grip pressing and Move + lift clicking are
  present. They now use fixed roles (right points, left clicks), and they are not HW-tested in
  this form.

**Gestures**
- HW: lasso right click, double-clap toggle (including releasing a drag).
- The earlier second-hand scroll and swipe were HW. The **left-fist clutch versions are CV**:
  - clutch;
  - clutch-gated scroll with steady neutral capture, rate curve, invert and horizontal hold;
  - clutch-gated swipe.

**KINECT-OS control center (CV, offscreen-rendered via `--ui-smoke-test`, never shown on
hardware yet):** shell with custom chrome + acrylic, nav rail, header status pills, master
control toggle, help drawer; Home / Gestures (live gesture cards + advanced tuning) / Voice /
Actions (catalog + Run) / Displays (monitor layout + hand space + guided calibration) /
Profiles (3 slots, rename persists) / Settings (UI options, health, diagnostics, activity log,
guide, about) / AI (placeholder); floating widget; tray icon; activity feed; local voice
commands (System.Speech); new routed actions (window management, media, control on/off,
LaunchApp). The old settings window, `ParameterControl` and `HelpWindow` are gone.

**Diagnostics panel (10 lines, now under Settings → Advanced diagnostics):**
1. Tracking: body count, body quality (q x/6), control enabled
2. Pointer session state + Moving/Stationary/Locked + lock displacement
3. R and L hand: state / confidence / joint Tracked-or-Inferred / in zone
4. L clutch + secondary mode + clap
5. Gesture + last action
6. Scroll neutral, offset and rate
7. Frame dt average and recent max, residual noise px, glitch count
8. R hand height and forward distance
9. Desktop bounds and monitor count
10. Calibration state and values

## 6. Stability findings (intermittent startup/runtime jitter)

Root causes found in code and addressed (all CV):
1. **Hard jitter dead zone was bimodal.**
   - When tracking noise was below the radius, the cursor was rock steady.
   - When noise was slightly above it (lighting, distance, seated posture), every crossing
     jumped the full radius back and forth. Those hops also kept the stationary lock from
     engaging whenever hop size > lock radius or hop speed > 45 px/s.
   - The user's last saved settings (dead zone ~13 px, lock radius ~9.7 px) were squarely in
     that regime.
   - Fixed by the soft dead zone.
2. **Body lock took the first tracked body**, including partial/phantom bodies, and never let
   go while that body stayed "tracked". This gives a wildly jumping skeleton until restart.
   Fixed by scored selection + degraded switch.
3. **Sessions started from the first raw sample** of a rising arm, often Inferred, and the
   left hand could become the pointer. Fixed by fixed roles + stabilization + seeding.
4. **Single-frame joint glitches were filtered in.** They are now skipped.
5. **The mode radio converter wrote back the unchecked radio's mode**, so every UI mode change
   and app close also re-selected the old mode. The sensor re-opened and the output loop
   restarted during shutdown. Fixed in `RadioCheckedToBoolConverter.ConvertBack`
   (`Binding.DoNothing` for unchecked). This bug came from upstream.
6. **Sensor disconnect** was only caught by the 1 s stall watchdog. It is now reset immediately.

Checked and not a cause: multiple cursor writers (only the output loop), duplicate event
subscriptions (all are made once in constructors), settings applied after control starts
(mode is applied last), and unbounded `dt` (clamped). If jitter persists, read `runtime.log`
and the diagnostics `noise` / `q` / `glitches` / frame-dt readouts before touching filter
tuning.

## 7. Hardware-tested state

**The user has run extensive physical Kinect testing.** Hardware verification only comes from
the user's reports. The reference settings below were tested *before* the soft dead zone and
the clutch. Treat them as a starting point, not defaults. In particular, the dead zone may
want re-tuning.

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

Code defaults (`Settings.settings`/`App.config`; ViewModel `DEFAULT_*` for the Default button):

| Setting | Code default |
|---|---|
| MoveScale | 1 |
| Smoothing | 0.2 in Settings / 0.7 via the Default button (known mismatch) |
| SpeedResponsiveness | 20 |
| JitterDeadzone | 3 |
| ClickFreeze | 0.15 |
| PointerSettleTime | 0.25 |
| PointerCenterHeight | 0.5 |
| ForwardActivation | 0.15 |
| ActivationMinHeight | 0.25 |
| ScrollSpeed | 60 |
| ScrollCurve | 1.5 |
| InvertScroll | false |
| SwipeMinDisplacement | 0.25 |
| StationaryLock | on / radius 15 / dwell 0.35 / breakout 35 |
| UseCalibratedRange | false |
| HandRange | 0.5 × 0.3 |
| HandCenterX | 0 |
| HoverRange | 20 |
| HoverDuration | 2 |
| Mode | 2 (GripToPress) |

User-reported hardware findings (earlier builds):
- Pointer is dramatically smoother than the original. Stationary lock works; ~0.23 s dwell felt better.
- Grip click, drag, lasso right click and swipe work. Double clap toggles control and releases a drag.
- Scroll worked but felt over-sensitive. That motivated the clutch rebuild.
- Dual-monitor mapping works, but edge reach and ergonomics needed work. That motivated the
  calibration changes.
- Occasional startup/runtime jitter, see §6.
- Windows 11: Kinect microphone audio enhancements caused repeated sensor disconnect/reconnect.
  Disabling them stabilized it. Check this first if the log shows repeated
  "Sensor UNAVAILABLE / available".

## 8. Development / build-test loop

```
inspect relevant code → smallest coherent change → build Debug → build Release (0/0)
→ (UI change) run --ui-smoke-test on the built exe, read ui-preview\*.png
→ bin\Debug + bin\Release are the current test builds → report EXE paths + what to test
→ user physical Kinect test → feedback → next iteration
```

- Label results **COMPILE VERIFIED** vs **HARDWARE VERIFIED**. Never promote one to the other
  yourself.
- Overwrite `bin\Debug` / `bin\Release` in place. Don't pile up copied test exes unless a
  comparison build is specifically useful.
- Never commit build output.
- `user.config` is per exe path, so Debug and Release keep separate last-used settings.
  Profiles are shared, so they are how to move a tuning between builds.
- **Adding a tuning setting** touches:
  - `Settings.settings`, `Settings.Designer.cs`, `App.config`;
  - `KinectCursorViewModel`: property (and `TuningProperties` set), Load, Save, `DEFAULT_*` +
    ResetToDefault, `CaptureProfile`/`ApplyProfile`;
  - `TuningProfile`;
  - a `TuningSlider` (or toggle + `HelpBinding.Attach`) on the right page (Gestures →
    advanced tuning groups; Displays → range values);
  - a `ControlHelp` entry whose Title equals the slider's `HelpKey`/label.
- **Adding a UI-only setting** (like `CompactOnMinimize`): the three settings files +
  `ShellViewModel` property + `LoadUiSettings`/`SaveUiSettings` + a toggle on Settings +
  `ControlHelp` entry. Not part of profiles.
- **Adding an action:** `ControlActionType` member + `ActionRouter` case (+ `KeyboardControl`
  chord) + `ActionCatalog` descriptor (+ `VoiceCommandCatalog` phrase, + `ControlAction.Describe`).
- **Adding a page:** UserControl under `Views/Pages`, `ShellSection` member, `NavItem` in
  `ShellViewModel`, `pages[...]` in `MainWindow.BuildPages`, icon in `Icons.xaml`.
- New `.cs` files must be added to the csproj `<Compile Include>` list; new `.xaml` files as
  `<Page Include>` with their `.xaml.cs` marked `DependentUpon`.
- Every `StaticResource` key must exist in `Themes/*.xaml` or the page's own resources; the
  smoke test is what proves it.

## 9. Backlog (not implemented)

1. **Hardware validation of the engine phase** (unchanged):
   - fixed hand roles;
   - clutch, and clutch-based scroll/swipe feel (tune `ScrollCurve`, dwell and clutch timings);
   - soft dead zone (re-tune `JitterDeadzone`);
   - stabilization latency;
   - guided calibration four-corner reach on the dual-monitor layout.
2. **Validation of the KINECT-OS UI on the real machine:** acrylic backdrop and custom chrome
   (drag, snap, maximize margin, DPI change between monitors), compact mode round trip,
   widget drag/position persistence, tray menu, live cards while pointing, calibration
   progress UI, voice recognition with the Kinect mic (watch for sensor reconnects), the new
   routed actions (Win+Arrow, media keys), profile rename persistence.
3. Possibly expose clutch timings / scroll dead zone in the UI if hardware testing shows they
   need per-user tuning. They are currently `GestureTuning` constants.
4. Smoothing default mismatch (Settings 0.2 vs Default button 0.7).
5. Custom voice commands (editor + storage next to profiles; `VoiceCommandSource.Custom`),
   `LaunchApp` targets, "move window to display".
6. AI assistant layer (provider/model selection, intent → catalog actions, workflows). The AI
   page reserves the place; nothing contacts a service.

## 10. Known gaps / observations

- A hard kill (Task Manager "End task" on the process tree / power loss) can still skip every
  handler. Unhandled exceptions, Windows session end and ProcessExit release the button via
  `MouseControl.ReleaseIfInjected()`.
- `CursorOutputLoop.Dispose()` is never called; Stop via Mode = Disabled is what runs.
- The log does not record per-frame data. Use Settings → Advanced diagnostics for live values.
- The acrylic backdrop needs Windows 11 22H2+; older builds get the opaque gradient. The
  offscreen previews always show the opaque fallback.
- Voice uses the Windows default input device; there is no device picker yet.
- The Run buttons on the Actions page act on the foreground window, which is KINECT-OS itself
  while you click them (Minimize/Snap will move this window).

## 11. Architectural invariants — DO NOT BREAK

1. **Single cursor-position writer.** Only `CursorOutputLoop` calls `MouseControl.MoveTo`/`SetCursorPos`.
2. **Never write the cursor from the Kinect body-frame handler.** Publish targets; the loop writes.
3. **Preserve the high-rate output architecture**: background thread, time-based ease,
   write-on-change, `ClearTarget` hands the mouse back, `UpdateOutputLoopState` is the one
   start/stop decision.
4. **Don't casually rewrite the filter/click pipeline**:
   `clamp → One Euro → soft dead zone → stationary lock → click freeze/anchor → clamp → output loop`,
   plus `HandStateFilter`. Change one stage at a time and keep the order.
5. **No injected mouse-down may survive** tracking loss, sensor unavailability, stall, control
   disable, mode change, calibration, profile load/defaults, display change, session end,
   glitch destabilization, app exit, crash or equivalent. `ReleaseAllGrips()` goes before
   `ResetControlState()`. Every `LeftMouseDown` has a `handGrips[]` flag, and
   `MouseControl` tracks injected downs for the process-level fail-safe.
6. **Fixed hand roles.** Only `PointerHand` (right) can own the pointer or anchor the cursor.
   Never reintroduce a "first activated hand" or handoff.
7. **Secondary gestures require `SecondaryGestureArmed`** (the left-fist clutch). Recognizers
   read `context.IsSecondaryGestureArmed`. Don't scatter left-hand `Closed` checks.
8. **Every pointer session goes through `PointerStabilizer`** and starts from `SeedSmoothing`.
   All teardown paths reset it to Waiting.
9. **Recognizers emit semantic `ControlAction`s through `IActionSink`.** They never call Win32
   or the cursor, and they write state before emitting.
10. **`ActionRouter` is the discrete-action boundary** for gestures now and voice/agent inputs
    later. Pointer movement stays out of it.
11. **Double-clap disable keeps the sensor and body tracking running**, and the clap stays
    above the gate in engine priority. Only `Mode = Disabled` closes the sensor.
12. **Preserve body-relative (SpineBase) tracking.**
13. `GestureContext` is reused per frame and must not be retained. Avoid per-frame allocations
    on the 30 Hz path.
14. Keep DPI awareness and physical-pixel `DesktopLayout` mapping. Use `SetCursorPos`, not
    SendInput absolute. No primary-monitor assumptions.
15. Use real sensor `deltaTime`. Never assume 30 Hz.
16. Uncalibrated mapping geometry stays the original. Calibration is opt-in, and in calibrated
    mode MoveScale is not applied.
17. Settings batches (profiles, defaults) go through `KinectCursor.ApplySettings`.
18. The mode radio converter must return `Binding.DoNothing` for an unchecked radio.
19. No broad refactors, no framework migration, no frontend redesign unless asked.
20. Don't rename the assembly/exe or change AssemblyVersion without warning the user.
21. **Never claim hardware behaviour is verified because it compiles.** Don't launch the app
    without asking; it takes the user's real cursor. Use `--ui-smoke-test` for the UI.
22. Keep the upstream MIT license notice.
23. **The UI never touches the engine directly.** Views bind to view models; every change of
    control goes through `KinectCursorViewModel` (settings, `IsControlEnabled`,
    `ExecuteAction`, calibration, profiles) so the engine's release/reset paths stay intact.
24. **`ActionRouter.Execute` runs on the UI thread only.** Voice (worker thread) and any future
    input must marshal through the Dispatcher first.
25. Voice and AI inputs use the same `ControlAction` vocabulary and catalog; they never move
    the pointer and never call Win32 themselves.
26. `ControlHelp` stays the single source of help text; `HelpHub` is the only route into the
    drawer. New controls get an entry, not inline copy.
27. Keep the smoke test honest: it must never show a window, open the sensor, start voice,
    create the tray icon or write `runtime.log` (`RuntimeLog.Suspend()`).
