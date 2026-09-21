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
center UI (shell window, floating widget, tray icon, help drawer, activity feed, wake-gated
local voice commands with a user-chosen wake word ("Jarvis" by default) and user-defined custom
commands, action catalog, AI placeholder). The UI is COMPILE VERIFIED +
offscreen-rendered only (§8); the voice pipeline is COMPILE VERIFIED + passes an offline
synthetic-speech acceptance test (§4.16); the engine's hardware status is unchanged (§5, §7).

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

Inputs other than gestures plug in at the `ActionRouter`/`ControlAction` boundary: the
wake-gated voice engine already does (`WakeGatedVoiceEngine` authorizes one command per wake
→ `VoiceViewModel` → `ActionRouter.Execute(action, "voice")`), the Actions page does
("control center"), and the DeepSeek assistant does (source "ai", §4.18): it sits after the
deterministic parser's refusal, inside the same wake session, and only uses a fixed tool list.
`ControlAction.Parameter` carries the `LaunchApp` target and the `SendKeys` combination;
`ControlAction.Request` carries the assistant-era desktop actions' arguments.

**Longer-term directions, not in code:** HUD beyond the compact widget, more
gestures, "move window to display". Consider asynchronous file indexing if the two-second
bounded personal-file search is noticeable.

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
  icon, writes `runtime.log` or saves settings. (Until `MainWindow.IsOffscreenCheck` existed,
  the never-shown window was closed at exit and the normal quit path saved the unloaded
  engine's defaults, Mode 0 and the previews' fake voice state over that exe's `user.config`.) Report: `%LOCALAPPDATA%\KinectHomeOS\ui-smoke-test.txt`;
  renders: `%LOCALAPPDATA%\KinectHomeOS\ui-preview\*.png` (open them with the Read tool).
  Run it after every XAML change: a missing `StaticResource` or a bad template only fails at
  load time, which this catches and a build does not.
- **Voice verification without a microphone: `KinectV2MouseControl.exe --voice-self-test`**
  (about 5 minutes, exit 0 = pass, 3 = fail). Checks the number reader and parser, loads both
  grammars, reads the Core Audio volume and writes the same level (and mute state) back (no
  audible change), lists the microphones read-only and checks a missing one is refused, checks
  the chime, then runs the real `WakeGatedVoiceEngine` against synthesized speech
  (Microsoft David/Zira voices) fed at real-time pace through a simulated microphone, with
  the chime mixed back in as echo. Commands are recorded, never executed; no mic, no speakers,
  no `runtime.log`. Report: `%LOCALAPPDATA%\KinectHomeOS\voice-self-test.txt`. Run it after
  any change under `Models/Voice` or to the command vocabulary.

### Executable paths

- Debug: `C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl\bin\Debug\KinectV2MouseControl.exe`
- Release: `C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl\bin\Release\KinectV2MouseControl.exe`

`bin/` and `obj/` are git-ignored. `executables/KinectV2MouseControl_EXE.zip` is **upstream's
2018 v1.2.1 binary**. It is historical, not the current build.

### Runtime files

| File | Location | Notes |
|---|---|---|
| Last-used settings | `%LOCALAPPDATA%\KinectV2MouseControl\KinectV2MouseControl.exe_Url_<hash>\1.2.1.0\user.config` | .NET user settings, **per exe path** (Debug and Release differ). Saved only on normal close. Tuning + mode + the UI settings (`CompactOnMinimize`, `StartCompact`, `OverlayAlwaysOnTop`, `OverlayLeft/Top`, `OverlayChatOpen`, `VoiceEnabled`, `VoiceWakeSensitivity` (0-100), `VoiceCommandThreshold`, `VoiceDismissSound`, `VoiceInputDeviceId` + `VoiceInputDeviceName` = chosen microphone, empty = System Default, wake word `VoiceWakePhrase` (default "Jarvis"; a stale `VoiceWakeWord` = "Kinect" entry from an earlier build is ignored on purpose)) |
| Profiles | `%LOCALAPPDATA%\KinectHomeOS\profiles.json` | 3 slots, shared by all builds. Atomic write. Unreadable file is moved to `profiles.json.bad`. A slot rename is saved immediately |
| DeepSeek key | `%LOCALAPPDATA%\KinectHomeOS\secrets\deepseek.key` | DPAPI CurrentUser-encrypted; written only by AI → Test key & save after a successful test call; Remove key deletes it. Never in user.config, logs, Activity or the repo; only sent to `https://api.deepseek.com` (redirects disabled) |
| AI self-test | `%LOCALAPPDATA%\KinectHomeOS\ai-self-test.txt` | Written by `--ai-self-test` (exit 0 = pass, 4 = fail): dry-run action policies + scripted-model tool loop; `--live` adds 3 real DeepSeek requests in dry run when a key is saved |
| Local Whisper (Build 2) | `%LOCALAPPDATA%\KinectHomeOS\whisper\` | Default command transcription: `bin\` = whisper.cpp v1.9.4 x64 CPU, `models\ggml-base.en.bin`, `test\*.wav`, `README.md`, `server.log`. Hidden job-owned loopback server, four CPU threads; no audio files saved. Optional test override `KINECTOS_WHISPER_DIR` |
| Custom voice commands | `%LOCALAPPDATA%\KinectHomeOS\voice-commands.json` | Shared by all builds. Saved ~0.6 s after an edit and on quit, atomic write. Missing = the 3 starters (not written until edited). Unreadable → `voice-commands.json.bad` |
| Runtime log | `%LOCALAPPDATA%\KinectHomeOS\runtime.log` (+ `runtime.prev.log`) | Human-rate event log, fresh each launch. **Ask the user for it when diagnosing intermittent issues** |
| UI smoke test | `%LOCALAPPDATA%\KinectHomeOS\ui-smoke-test.txt`, `ui-preview\*.png` | Written by `--ui-smoke-test` only |
| Voice self-test | `%LOCALAPPDATA%\KinectHomeOS\voice-self-test.txt` | Written by `--voice-self-test` only |

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
    VoiceViewModel.cs         voice switch, wake word, custom command list, microphone choice/fallback/retry, thresholds, HUD state, decisions/diagnostics, StartRequestCommand (manual session); the ONLY place a voice command is executed
    CustomCommandRowViewModel.cs  one editable custom command (phrase, kind, keys + "only in" app, target, action)
    VoiceViewModel.Assistant.cs   voice ↔ assistant glue: OtherRequest hook, HUD Thinking/result, cancel, custom/built-in runners for the AI
    AssistantViewModel.cs     AI page: key test/save/remove, model, "Send other requests to AI", typed requests, step log; runs AssistantSession
    ActionsViewModel.cs       catalog grouped by category with Run commands
    DisplaysViewModel.cs      monitor rects + live cursor dot; hand-space geometry (reach rect, thresholds, hand dots)
    ProfileSlotViewModel.cs   one slot: name (rename persists), state, summary, Load/Save
    ObservableObject.cs       INotifyPropertyChanged base + RelayCommand
  Views/
    MainWindow.xaml(.cs)      the shell: WindowChrome + acrylic, rail, header pills, page host, help drawer; compact/tray wiring
    OverlayWindow.xaml(.cs)   floating compact widget (status orb, live level ring, voice HUD, AI button, slide-down chat, power/expand)
    Pages/*.xaml(.cs)         HomePage, GesturesPage, VoicePage, ActionsPage, DisplaysPage, ProfilesPage, SettingsPage, AiPage
    Controls/TuningSlider     labelled slider + value box + help glyph (HelpKey must match a ControlHelp title)
    Controls/IconView.cs      draws an Icons.xaml geometry in the inherited Foreground
    Controls/KeyChordBox.cs   key-combination box: Ctrl/Alt/Win chords and F-keys are recorded, plain text can be typed (Win+H)
    ControlHelp.cs            single source of control explanations (tooltips, drawer, Settings guide)
    HelpHub.cs                static hover/pin routing from any control to the drawer
    Converters.cs             BoolToValue, Visibility, state/kind→brush, IconKey, Scale, Percent, Format...
    WindowBackdrop.cs         DwmSetWindowAttribute: dark mode, rounded corners, acrylic (with opaque fallback); HideSystemCaptionButtons (drops WS_SYSMENU)
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
      KeyboardControl.cs      Alt+Tab, Win+arrow chords, media keys (Tap with repeat), SendChord (custom combinations, with scan codes)
      KeyChord.cs             "Ctrl+Shift+D" parse (lenient) / canonical text / virtual keys
      ForegroundApp.cs        process name of the foreground window; running windowed apps
      SystemVolume.cs         Core Audio IAudioEndpointVolume (render): exact master volume, explicit mute/unmute
      CaptureVolume.cs        Core Audio IAudioEndpointVolume (capture): a mic's Windows input level (the Voice page gain slider)
      Win32Input.cs           SendInput structs/constants
      VirtualScreen.cs        VirtualScreen (bounds, monitor rects) + DesktopLayout (nearest-monitor clamp)
    Actions/
      ControlAction.cs        enum (mouse, scroll, windows, media, control gate, LaunchApp, SendKeys), Describe(), IActionSink, IControlGate
      ActionRouter.cs         Execute(action, source); LastSource; key chords via KeyboardControl; LaunchApp
      ActionCatalog.cs        ActionDescriptor list for the Actions page + voice grammar (implemented vs planned)
    Voice/
      WakeGatedVoiceEngine.cs the state machine: WakeOnly → Acknowledging → Listening; acceptance rules, gate, timeout
      VoiceSession.cs         VoicePhase/Outcome/Verdict, VoiceSession (single-use), VoiceDecision, counters, event args
      VoiceGrammars.cs        SRGS wake grammar (the user's wake word; "Kinect" gets /kɪˈnɛkt/) and command grammar (phrases + custom phrases + volume 0-999)
      VoiceCommandParser.cs   deterministic parser (exact phrases → custom phrases → volume pattern), SpokenNumber, VoiceIntent
      VoiceCommand.cs         VoiceCommand + built-in VoiceCommandCatalog (phrases → catalog action; volume pattern; cancel)
      VoiceWakeWord.cs        VoicePhrases (grammar tokens, acronym spelling, match keys) + VoiceWakeWord (default "Jarvis", rules)
      CustomVoiceCommands.cs  CustomCommandDefinition/Store (voice-commands.json), CustomCommandRules, CustomPhraseSet
      VoiceFeedbackSounds.cs  synthesized wake chime + dismiss tone (IVoiceFeedback)
      VoiceSelfTest.cs        --voice-self-test harness (simulated microphone + chime loopback)
      AudioInputDevices.cs    Core Audio capture-device list (IDs, names, default) + AudioDeviceWatcher (IMMNotificationClient)
      MicrophoneCaptureStream.cs  WASAPI capture of one chosen device → 16 kHz mono stream for the recognizer
    Assistant/
      DeepSeekClient.cs       AssistantJson, DeepSeekKeyStore (DPAPI), IAssistantModel, DeepSeekClient (fixed endpoint, thinking disabled)
      AssistantSession.cs     AssistantTools (strict JSON schemas → ControlAction) + the bounded tool loop (6 rounds, 25 s, cancellable)
      AssistantSelfTest.cs    scripted fake model for --ai-self-test (+ --live dry-run requests)
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
| `WakeGatedVoiceEngine` recognizer events | thread pool (the recognizer is created on a private thread with no synchronization context); state under one lock; `VoiceViewModel` marshals every engine event with `Dispatcher.BeginInvoke` before `ActionRouter.Execute` | on speech |
| Wake chime + gate | one background thread per wake (plays the chime, then opens the gate) | per wake |
| Whisper PCM tap + recording | owned capture pump and async VAD worker, 16 kHz mono; indexed ring uses the capture sample clock | 20 ms frames |
| Whisper server / transcription | hidden job-owned process; async loopback HTTP off the UI thread, 5 s request timeout | per authorized recording |
| Command-window deadline | `System.Threading.Timer` | 4 s (+2.5 s once if a command is mid-utterance) |
| Voice grammar switch | thread pool, outside the state lock, converges to the current phase | per phase change |
| Voice HUD countdown / mic level / diagnostics | `DispatcherTimer` in `VoiceViewModel` while voice is on | 50 ms |
| Selected-microphone capture | background MTA thread `KINECT-OS microphone` (owns every WASAPI COM object), AboveNormal; the recognizer reads from its queue | 10 ms poll |
| Audio device notifications | Windows audio thread → `Dispatcher.BeginInvoke` → 500 ms debounce `DispatcherTimer` → `RefreshMicrophones` | on change |
| Voice input retry | `DispatcherTimer` in `VoiceViewModel` after the input is lost | 0.3-1.5 s, then backing off to 10 s |
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
- **Release grace**: once an Active session has crossed the activation release boundary, it
  holds its last target for `PointerReleaseGrace` (0.35 s). No target or gesture is published
  during that hold, and every grip releases immediately. Returning fully inside the activation
  zone resumes the same session without re-seeding or stabilizing; expiry tears it down normally.
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
  Release is 0.12 m below either threshold. An Active session then holds its last cursor target
  for up to `PointerReleaseGrace` (0.35 s), with grips and gesture activity released, before it
  tears down.
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
  `WindowChrome.IsHitTestVisibleInChrome`. With the frame extended over the whole window DWM
  drew its own min/max/close on top of the app's (and those took the clicks), so
  `WindowBackdrop.HideSystemCaptionButtons` removes WS_SYSMENU (in `SourceInitialized` and on
  every state change; min/max box styles stay for the taskbar and Win+Arrow). The app's
  caption buttons are one top-level strip in the window's top-right corner, above the help
  drawer (whose header sits below the caption band); the header keeps a 144 px spacer for it.
- **Live status:** `KinectCursorViewModel.UpdateStatus()` (timer + engine events) fills
  `LiveStatus`; `ControlState` = Off (mode Disabled) / Standby (control off) / Ready /
  Active (pointer session). `Headline`/`Subline` are the one-line summaries used by the rail
  footer, tray tooltip and widget.
- **Master toggle:** `Engine.IsControlEnabled` (TwoWay) → `KinectCursor.SetControlEnabled(value,
  "control center")`; `ControlEnabledChanged` raises it back when a clap flips it.
- **Compact mode:** `ShellViewModel.WindowRequest` ("compact" / "expand" / "quit") is handled
  by `MainWindow`: `EnterCompact` shows `OverlayWindow` at the remembered position and hides
  the main window; `ExitCompact` reverses it. Minimize → compact when `CompactOnMinimize`.
  The widget is `AllowsTransparency`, `Topmost` bound to `OverlayAlwaysOnTop`, created with
  `ShowActivated=false` so it does not steal keys. Drag the pill; double-click expands;
  position persists via `OverlayLeft/Top`. A live level ring around the orb follows
  `Voice.MicLevel` while a command window is open. Voice HUD states pulse (Listening /
  Recording) or fade quietly (Transcribing / Thinking); Executed / Rejected stay still, with
  `LastCommandText` on its own line. The AI button (`Voice.StartRequestCommand`) opens the
  same command window as a spoken wake (`WakeGatedVoiceEngine.BeginManualSession`); it is
  disabled while voice is off or a session is already open. A chevron toggles a slide-down
  chat panel in the same window. `OverlayChatOpen` is only the remembered chevron state.
  An assistant request also opens that panel without writing the setting: it shows the
  request and the answer (wrapping body text, room for two or three lines) and closes
  about 6 seconds after the answer (`AnswerCardDwellSeconds`). Hovering the panel, focusing
  or clicking the request box, or pressing the chevron pins it (`OverlayChatOpen` becomes
  true) and the dwell does not run; a panel the user already opened is never auto-closed.
  Steps stay listed for a manual open. A one-line request box (`Assistant.SubmitCommand` on
  Enter) and Cancel while busy are unchanged. A click in the request box calls `Activate()`
  so typing works; the widget is created `ShowActivated=false` and does not take focus until
  then. The panel height slide is `PanelSlide`: both `From` and `To`, and only while the
  window is shown. The window is `SizeToContent="WidthAndHeight"`, so the animation resizes
  the HWND every tick and the clock is sampled again inside `HwndTarget.OnResize`. A To-only
  `DoubleAnimation` has no resolvable origin there and throws — that Release crash also
  skipped the normal quit path, so the user's tuning was never saved. A style storyboard
  cannot carry the `From`, because offscreen its clock sits at time zero and holds the panel
  shut; the style `Setter` (280 open) is what the smoke test renders. The help drawer uses
  the same slide.
- **Tray:** created on `Loaded`, disposed on `Closed`; Open / Compact / toggle control / Quit.
- **Quit path:** window Close → `ShellViewModel.Quit()` → voice off, UI settings saved,
  `Engine.Quit()` (SaveSettings, Mode = Disabled via the setter, log). The X button quits;
  minimize does not.
- **Activity feed:** engine posts via `ActivityLog.Post` at the existing RuntimeLog sites
  (mode, control gate, calibration, display change, stall; KinectReader: sensor availability,
  body lock/loss; router actions via `KinectCursor.PostActionActivity`, scroll coalesced; VM:
  profiles/defaults; voice). Capacity 200, newest first in `Shell.Activity`.

### 4.16 Voice: wake-gated, local, off by default

**Build 2 milestone 1 — default Whisper path (COMPILE VERIFIED):** `VoiceSpeechEngine`
selects Whisper (default) or the Windows command-grammar fallback described below. Windows
listens only for the wake word in Whisper mode. After the chime all grammars are disabled;
the owned microphone (including System Default) keeps feeding a 16 kHz PCM tap/ring.
`PcmTapStream` / `PostGateRecorder` use the capture sample clock for the gate; only samples
at/after it are recorded. Energy VAD requires about 150 ms speech, waits up to 4 s for onset,
ends after 0.7 s silence, and caps recording at 10 s. Silence sends no request. Empty/token
and short known hallucinations are discarded. `WhisperService` owns the hidden server via
a kill-on-close Job Object, loopback-only HTTP, drained output, three startup/restart attempts
with backoff. Missing files or repeated startup failures show a Windows fallback notice.

Multipart inference uses in-memory WAV, text output, temperature 0, audio_ctx 512 and wake /
custom phrases / app-name vocabulary. Exact parser matches use the existing execution path;
unmatched text currently shows "Not a command". Session authorization and input-generation
checks drop stale results, including at UI dispatch. Voice off/input changes cancel recording
and transcription and close the server. HUD adds Recording / Transcribing / Thinking; the
optional `VoiceListeningClick` marks recording end. Diagnostics log speech-end → transcript
and transcript → action times. Synthetic real-server tests measured about 0.96–1.00 s from
speech end to transcript including the 0.7 s endpoint wait. Hardware timing remains unverified.

The grammar confidence/utterance acceptance details below describe the **Windows fallback**;
wake isolation and execution authorization apply to both modes. The assistant is not wired yet.

**Interaction:** say the wake word on its own → ✦ chime → one command within 4 s → done, back
to waiting for the wake word. The wake word is the user's (Voice page, 1-3 words, default
**"Jarvis"**; it was fixed as "Kinect" until 2026-09-19 - the history below is about that
word). One wake authorizes exactly one command. The combined one-breath form ("Jarvis volume
fifty") is deliberately **not** supported: a grammar containing wake word + command is
exactly what caused the original false actions.

**Root cause of the original false commands (measured, see §6.7):** the first version loaded
one closed grammar, "Kinect" + command phrase, and executed whatever it reported. A closed
grammar has nowhere else to put speech, so every utterance was scored against it (inventing
the "Kinect"), and the desktop engine also spots grammar phrases inside longer sentences.
"Let's go to sleep, I'm tired" came back as "Kinect go to sleep" at 0.66 > the 0.62
cut-off → Control off. Short conversational aliases ("play", "pause", "wake up", "go to
sleep", "minimize kinect") made it worse. One false match was one action.

**State machine (`WakeGatedVoiceEngine`, `VoicePhase`):**

```
Off ─Start→ WakeOnly ─"Kinect" accepted→ Acknowledging ─chime played + 80 ms→ Listening
               ↑               (chime playing)                 │ (4 s window, +2.5 s once
               │                                               │  if a command is mid-utterance)
               └── Executed / NotRecognized / TimedOut / Cancelled / SpokeTooEarly ┘
```

- **Grammars:** wake (SRGS, the wake word's tokens from `VoicePhrases.GrammarTokens`; "Kinect"
  gets IPA /kɪˈnɛkt/ - the schwa form = "connect" is deliberately absent) and commands (SRGS
  named rules: every catalog phrase + every valid, enabled custom phrase + "volume N
  [percent]" / "set volume [to] N [percent]" with N = 0-999 in words, so 200 is heard as 200
  and refused, never bent to 100). **Never both enabled:** WakeOnly = wake only;
  Acknowledging/Listening = commands only (the old one is disabled before the new one is
  enabled). `GrammarBuilder` could not compile the volume forms ("'' rule reference"), hence
  SRGS. No dictation grammar: it was measured to swallow the real wake word and commands.
  Recognizer adaptation is switched off for this engine.
- **Wake acceptance (all required):** wake-grammar result; confidence ≥ `WakeThreshold`
  (default **0.65**, set by Wake Sensitivity, see below); **lead** (words start − utterance
  speech onset from `SpeechDetected`) ≤ 0.20 s; **≥ 0.30 s of silence before** the utterance
  (onset − end of the previous phrase); word duration 0.20-1.10 s (+0.5 s per extra token of a
  multi-word or spelled wake word, `VoiceWakeWord.MaxDuration`). Near misses (confidence ≥
  0.45) are logged as decisions; everything else is only counted. **The lead/gap/duration
  isolation checks - not the confidence floor - are what keep conversation out** (hardware log:
  ~60 conversational "Kinect" candidates in 20 min, almost all rejected by lead or gap; genuine
  and false wakes both scored 0.86-0.95). So the floor is set below the recognizer's
  HighConfidence band (0.80) to make a normal-voice "Kinect" reliable, especially on narrowband
  Bluetooth mics, without weakening safety.
- **Command acceptance (all required):** phase Listening, current session; the utterance's
  speech onset ≥ the **gate** (audio position taken 80 ms after the chime finished playing),
  except that ≤ 0.30 s of merged chime echo before the gate is tolerated if the words
  themselves start after it; command-grammar result; confidence ≥ `CommandThreshold`
  (default **0.70**, above the engine's CFG rejection threshold 60); lead ≤ 0.25 s; duration
  ≤ 0.5 s + 0.6 s per word (stretched matches rejected) and ≤ 3.5 s; `VoiceCommandParser`
  accepts the text. Then `VoiceSession.TryAuthorize()` (engine) and, on the UI thread,
  `TryMarkExecuted()` (view model) - one action per wake even with duplicate events.
- **Session endings:** any post-gate utterance that fails ⇒ NotRecognized; "cancel" / "never
  mind" ⇒ Cancelled; speech that began before the chime and ran on ⇒ SpokeTooEarly;
  4 s without a command ⇒ TimedOut. Short noise (< 0.30 s) and echo wholly inside the chime
  are ignored without ending the session. Every ending returns to WakeOnly.
- **Chime (`VoiceFeedbackSounds`):** synthesized in code, 150 ms, two-note G6→D7 ping,
  ≈ -14 dBFS, played with `SoundPlayer.PlaySync` on a background thread; the gate opens when it
  returns + 80 ms guard. Optional quiet falling "dismiss" tone (110 ms) when a window closes
  without a command (`VoiceDismissSound`).
- **Timing (self-test, audio time from the end of the spoken "Kinect"):** chime starts
  ~395 ms, command window opens ~675 ms (includes a simulated 40 ms output latency). The
  command grammar is already live during the chime; only the acceptance gate waits.
- **Parser (`VoiceCommandParser`):** normalize (lower case, hyphens → spaces, "%" →
  "percent", punctuation dropped) then **exact** catalog-phrase lookup, then the running
  recognizer's custom phrases (`CustomPhraseSet`, exact by match key), then the volume
  pattern with `SpokenNumber` (0-999 words or digits, strict; > 100 refused). No substring or
  fuzzy matching anywhere. Future assistant fallback goes after a parser refusal.
- **Execution:** only `VoiceViewModel.OnCommandRecognized` executes, only for
  `CommandRecognized`, only if voice is still on: action intents →
  `Engine.ExecuteAction(action, "voice")` (router returns false on failure → HUD shows it);
  shell intents ("open", "compact", "calibrate") → `ShellCommandRequested`; custom intents →
  `RunCustomCommand` (below).
- **HUD:** `VoiceHudState` Wake / Listening / Executed / Rejected drives the Voice page hero
  (orb, title, 4 s countdown bar, mic level), the header pill and the compact widget ("KINECT
  ● Listening…", "Volume → 70%"); outcomes linger 2.6 s.
- **Diagnostics:** Settings → Advanced diagnostics → Voice pipeline: state, thresholds,
  session (wake confidence, gate position, deadline), last wake / last command, counters
  (wakes, executed, not recognized, timed out, cancelled, too early, background ignored, wake
  near-misses, echo/noise ignored) and the last 14 `VoiceDecision`s with confidence, duration,
  lead and gap. Background speech is never transcribed: in WakeOnly the only possible text is
  "Kinect", in the window only catalog phrases. The Activity feed only gets executed commands
  and voice on/off.
- **Wake Sensitivity** (Voice page, 0-100, persisted `VoiceWakeSensitivity`, default 65) is the
  wake control. `VoiceViewModel` maps it to the confidence floor: `threshold =
  clamp(0.90 − 0.40·(sensitivity/100), 0.60, 0.90)` (65 → ≈0.64). It moves ONLY the floor; the
  isolation checks are untouched, so even at 100 an embedded/rushed "Kinect" still cannot wake.
  Legacy 0-1 saved values are scaled ×100 on load. **Command confidence** (0.30-0.99, default
  0.70) is a separate slider. The Last wake readout shows every near miss's confidence.
- **Test wake word** (`VoiceViewModel.IsTestMode`, toggle button): the full pipeline runs so
  wake confidence and the mic meter are real, but `OnCommandRecognized` shows the parsed command
  and does NOT execute it - no action, no shell command. Leaving voice off leaves test mode. For
  comparing mics and tuning sensitivity/level safely.
- **Live level + input gain (Voice page → Microphone card):** the meter is `MicLevel` (0-1) with
  a too-quiet/good/very-loud zone (`MicLevelZone` → `MicZoneToValueConverter`); the percentage is
  `MicLevelPercentText`. **Input level** is the selected device's Windows capture level via
  `CaptureVolume` (Core Audio `IAudioEndpointVolume` on the capture endpoint, scalar 0-1 = the
  Sound-settings slider). Writability is confirmed by a no-op write, not the hardware-support
  flags (the Kinect array reports no hardware volume yet software level sticks); a device that
  refuses the write shows read-only. This is signal strength, separate from Wake Sensitivity.
- Windows 11 + Kinect mic audio enhancements can make the sensor reconnect; the Voice page
  says so. Voice never moves the pointer.

**Wake word and custom commands (2026-09-19, CV + self-test):**
- **Wake word** (`VoiceViewModel.WakeWordDraft` → Apply / Enter): `VoiceWakeWord.TryValidate`
  - letters only, 1-3 typed words, ≥ 3 letters, not a built-in command (incl. cancel/volume
  forms). Applying revalidates the custom commands and restarts the recognizer
  (`RequestReinitialize`). Persisted as `VoiceWakePhrase`; an invalid saved value falls back
  to "Jarvis". All UI text quotes `Voice.WakeWordQuoted`.
- **Custom commands** (`CustomCommandDefinition`, Voice page → Your commands, stored in
  voice-commands.json): phrase + on/off + kind:
  - **Keys** - a `KeyChord` ("Ctrl+Shift+D") sent by `ActionRouter` (`SendKeys` →
    `KeyboardControl.SendChord`: one SendInput batch, modifiers down/key/modifiers up, with
    scan codes because Electron apps read `KeyboardEvent.code`), optionally **only in** an app:
    `ForegroundApp.Matches(foreground process name, OnlyInApp)` must hold at execution time or
    nothing is pressed and the HUD says which app is in front. The dropdown offers running
    windowed apps (process names: ChatGPT, claude, Cursor, msedge…).
  - **OpenApp** - `LaunchApp` with a path / .lnk (Browse keeps the shortcut itself) / URL.
  - **Action** - any `CustomCommandRules.AssignableActions()` catalog entry (not mouse holds,
    not SetVolume, not SendKeys/LaunchApp); shell commands go to `ShellCommandRequested`.
- **Rules** (`CustomCommandRules.CheckPhrase` + `VoiceViewModel.ValidateCustomCommands`):
  letters only (numbers as words), ≤ 6 words, not a built-in phrase or volume form, must not
  contain the wake word, no duplicate among enabled rows, and the kind's settings must be
  complete. A failing row stays listed with its reason but is not in the grammar.
- **Acronyms:** a token in capitals (≤ 4 letters) or without a vowel is spelled in the
  grammar ("GPT" → "G P T"); `VoicePhrases.MatchKey` joins single letters back, so "G P T
  listen" from the recognizer matches "GPT listen".
- **Restart vs. no restart:** the engine freezes a `CustomPhraseSet` (id → phrase) at Start.
  Edits are debounced 600 ms, saved, and a new set whose `Signature` differs from the running
  one triggers `RequestReinitialize`. What a phrase *does* is looked up by id at execution, so
  changing keys / app / target needs no restart. Test mode shows what would run (including
  whether the "only in" app is in front) and presses nothing.
- **Starters** (file absent): "GPT listen" (only in ChatGPT), "Claude listen" (claude),
  "Cursor listen" (Cursor), keys blank - the user records each app's dictation shortcut.

**Microphone selection (Voice page → Microphone card):**
- System.Speech can only open "the default audio device" or a stream. **System Default** keeps
  `SetInputToDefaultAudioDevice` (the path that was hardware-tested). A **specific device** is
  opened by its Core Audio endpoint ID through WASAPI shared mode with AUTOCONVERTPCM
  (Windows converts to 16 kHz/16-bit/mono) by `MicrophoneCaptureStream`, which the
  recognizer reads via `SetInputToAudioStream`. Read blocks until the full request is there
  (a short read = end of stream to SAPI). The legacy waveIn route was rejected: mapping an
  endpoint ID to a waveIn index (DRV_QUERYFUNCTIONINSTANCEID) returned garbage on this machine.
- Devices: `AudioInputDevices.List` (active capture endpoints, full names, stable IDs, default
  = eCapture/eConsole). `AudioDeviceWatcher` (IMMNotificationClient) + the Refresh button
  re-read the list (500 ms debounce).
- Persistence: `VoiceInputDeviceId` (endpoint ID, the stable key) + `VoiceInputDeviceName`
  (display while missing), UI settings, saved on close. Not part of tuning profiles
  (machine-specific).
- **Effective input** = the preferred device if connected and not "suspect", else System
  Default. `VoiceViewModel.ApplyMicrophoneChoice` restarts the recognizer when the effective
  input differs from the bound one (user choice, device lost, device back) or when System
  Default is bound and Windows' default input moved. A missing saved device shows as
  "not connected" in the dropdown with a notice; voice returns to it when it reconnects.
- **Switching = `RequestReinitialize`**: status Reinitializing, generation bumped, HUD cleared,
  then (dispatcher, Background) `Stop()` → generation bumped → `StartEngine`. Every engine event
  is stamped with the generation when raised and dropped on the UI thread if stale - a command
  recognized a moment before a switch is never executed. `Stop()` closes the capture stream
  before disposing the recognizer (unblocks its Read, releases the device), so there is never
  more than one microphone stream. A new recognizer always starts in WakeOnly, no session.
- **Loss**: a vanished device (AUDCLNT_E_DEVICE_INVALIDATED, 2 s without audio, 8 s before the
  first audio for Bluetooth profile switches) ends the stream → engine `Stopped` with the reason
  → voice stays on, status Unavailable, retry (1.5 s, then backing off to 10 s) on the effective
  input. A selected device that fails to open, or dies within 10 s of starting, becomes
  "suspect" and is skipped until it is chosen again, Refresh is pressed or the device set
  changes (stops a flapping Bluetooth link from restart-looping). The same retry also recovers
  from "Audio input is not supported for non-active console sessions" (locked session).
- Measured on this PC (scratch harness, real engine): start 60-350 ms, stop 20-85 ms, capture
  clock 5-35 ms ahead of the recognizer's position (same timeline; `CurrentAudioPosition` uses
  the later of the two for the chime gate, capped at 0.5 s).

**False wakes, what the hardware log showed (2026-09-18 session):** false and real wakes
both scored 0.93-0.95, so raising Wake confidence would not help; the lead/gap checks rejected
~60 conversational "Kinect" candidates in 20 minutes. `SpeechDetected` fires per PHRASE (not
per sound), and is delivered 0.7-1.8 s after the phrase starts, so "speech carried straight on
after Kinect" cannot be detected before the chime without ~1 s of extra latency - not done.
`GapBefore` is measured from the end of the previous *recognized* phrase, so speech the
recognizer made no phrase of counts as silence (it can overstate the pause). Accepted/rejected
wakes log the gap too, for data-driven tuning.

**Wake made easier (2026-09-19 pass):** the follow-up complaint was the opposite - a deliberate
"Kinect" at normal volume was too hard, especially on a close Bluetooth mic. Since the isolation
checks (not the confidence floor) are what keep conversation out, the default floor dropped from
0.80 to **0.65** (Wake Sensitivity 65), which makes genuine wakes reliable while the pure-
conversation self-test corpus still passes with 0 wakes at 0.65. If a user needs it stricter or
looser, Wake Sensitivity moves the floor 0.60-0.90; the isolation gate never moves.

### 4.17 Action catalog and the extended vocabulary

- `ControlActionType` gained `EnableControl/DisableControl`, window management (Win+Up/Down/
  Left/Right, Win+D, Win+Tab, Alt+F4), media keys, `LaunchApp` (shell-executes `Parameter`),
  **`SetVolume`** (Value = 0-100, Core Audio, unmutes above 0; out-of-range refused, never
  clamped) and explicit **`Mute` / `Unmute`** (Core Audio; the old toggle is gone).
  `SystemVolume` treats any non-negative HRESULT as success: SetMute returns S_FALSE when the
  state is unchanged (the first version reported every "volume N" as FAILED because of it).
  `VolumeUp/VolumeDown` send five media-key steps (10%, Windows overlay shows). Keys go
  through `KeyboardControl.Chord/Tap` (one SendInput batch). `IControlGate.SetControlEnabled
  (bool, source)` was added.
- `ActionRouter.Execute(action, source)` returns **true only if the action was performed**
  (false for no-ops, refusals and Core Audio failures), records `LastSource` ("gesture"
  default, "voice", "control center"); `ActionExecuted` handlers read it.
- `ActionCatalog.All` is the human-readable index (Pointer, Windows, WindowManagement, Media,
  System, Apps, Intelligence) with `IsImplemented`, `CanRunFromUi` (mouse-button actions and
  Close window are not runnable from the page), trigger text and optional `ShellCommand`.
  "launch" (`LaunchApp`) and "keys" (`SendKeys`, Parameter = "Ctrl+Shift+D") are implemented
  but only reachable through custom voice commands (no Run button, no built-in phrase).
  **Adding an action = enum member + router case + catalog descriptor (+ voice phrase).**

### 4.18 DeepSeek assistant (Build 2 Shot 1, CV)

- **Routing:** a Whisper transcript (or a typed request on the AI page or the compact-widget
  chat box) first goes through the exact local match (`VoiceCommandParser` with the custom
  phrases). No match → `VoiceIntentKind.Request` → `VoiceViewModel.OtherRequest` →
  `AssistantViewModel.SubmitAsync`, but only when a key is saved and "Send other requests to
  AI" (`SendOtherRequestsToAI`, default on) is set; otherwise the HUD says "Not a command".
  Requests over 4,000 characters are refused.
- **Manual session:** `WakeGatedVoiceEngine.BeginManualSession()` (widget AI button /
  `Voice.StartRequestCommand`) opens the same window as an accepted wake — session, chime,
  gate, one command — and does nothing unless the phase is WakeOnly. Source is logged as
  "button".
- **Widget chat:** `OverlayWindow` hosts the answer, the step log and a one-line request box
  in the same window as the pill. `OverlayChatOpen` records only the user's chevron. An
  assistant request opens the panel on its own (request line + wrapping answer) and closes it
  about 6 seconds after the reply, without writing that setting. Hover, the request box or
  the chevron pins it open; a panel already opened by hand is never auto-closed.
- **Model:** `DeepSeekClient` posts OpenAI-style `chat/completions` to the fixed origin
  `https://api.deepseek.com` (TLS 1.2, redirects off, 20 s timeout), non-streaming, with
  `thinking: {"type":"disabled"}` (verified against api-docs.deepseek.com during the build).
  Models: `deepseek-flash` (default) or `deepseek-v4-pro`. Provider error bodies are never
  shown or logged (they can echo headers); any text containing the key is masked.
- **Tool loop (`AssistantSession`):** system prompt (tools only, prefer one call, no follow-up
  questions, never claim success without a confirming result, file names/window titles are
  untrusted data) + JSON context (monitors, foreground process, enabled custom phrases, assignable
  built-in action ids). At most 6 tool rounds, 8 calls per round, 25 s overall, cancellable.
  `AssistantTools.TryParse` rejects unknown tools, unexpected/missing arguments, non-string
  text, out-of-range monitors; rejections go back to the model as results, never executed.
- **Tools:** launch_app, open_url, web_search, find_files, open_file, place_window,
  list_windows, type_text, custom_command (an enabled custom phrase, "only in" check applies),
  builtin_action (an assignable catalog id). Each is marshalled to the UI thread and executed
  through `ActionRouter` (`engine.ExecuteRequest`, source "ai"); the policies are §9's
  Build 2 milestone 2 notes (`SafeDesktopActions`).
- **Cancellation:** `AssistantViewModel.Cancel` bumps its generation; a new wake
  (Acknowledging), any voice input-generation change (voice off, restart, microphone switch),
  a double clap (`GestureControlToggled`), the Cancel button or "cancel" all stop remaining
  steps. Completed steps are not undone (the HUD says so).
- **Feedback:** HUD `Thinking` with each step, then the model's one-sentence answer; every
  step goes to the AI page step log, the Activity feed and runtime.log with timings.

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
guide, about) / AI (connection, typed request, busy indicator, scrollable step log); floating
widget (live level ring, voice states, transcript, AI button, slide-down chat); tray icon;
activity feed; local voice
commands (System.Speech); new routed actions (window management, media, control on/off,
LaunchApp). The old settings window, `ParameterControl` and `HelpWindow` are gone.

**Wake word + custom commands + caption fix (CV, 2026-09-19):** editable wake word (default
"Jarvis") with Apply and the existing Test mode; custom commands (key combination with
"only in app" check, open app/file/URL, built-in action) with starters for ChatGPT / Claude /
Cursor dictation; only one set of window caption buttons; the smoke test no longer saves
settings. Covered by `--voice-self-test` (parser, rules, key parsing, synthetic "Jarvis" →
"Claude listen" / "GPT listen"); nothing of it is hardware verified.

**Build 2 Shot 1 (CV, 2026-09-20):** local Whisper transcription after the chime (default;
Windows grammar mode kept as fallback), bounded desktop actions, and the DeepSeek assistant
with the AI page (key, model, typed requests, step log). Covered by `--voice-self-test` (four
real Whisper scenarios), `--ai-self-test` (policies + scripted model) and the smoke test.
Live DeepSeek calls and everything on hardware are untested.

**Build 2 Shot 2 (CV, 2026-09-20):** compact-widget live level ring, voice-state motion,
countdown and transcript; AI button that starts a command window without the wake word
(`BeginManualSession`); slide-down chat panel (`OverlayChatOpen`); AI page grouped into
connection / request / steps cards. Covered by the smoke test, `--voice-self-test` (manual
session scenarios) and `--ai-self-test`. Nothing of it is hardware verified.

**Voice (CV + offline acceptance test):** wake-gated state machine, chime, gate, one command
per session (accepted wake or widget button), deterministic parser, absolute volume (`volume 0-100`), explicit mute/unmute, HUD
states, voice diagnostics. `--voice-self-test` passes: 17 recognition scenarios on synthesized speech,
including 85 s of conversation at the shipped thresholds with zero wakes and zero commands,
and real wakes followed by ordinary talk with zero commands. First hardware session
(2026-09-18): wake-gated commands executed as intended and no random actions; occasional
false wakes (chime/shine only). **Microphone selection is CV** (plus a scratch run of the real
engine on both local inputs); switching, fallback, Bluetooth loss and persistence are not
hardware verified.

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

7. **False voice commands from conversation** (reported on hardware): a single always-on
   "Kinect + command" closed grammar, executed on one match. Fixed by the wake-gated voice
   pipeline (§4.16). CV + offline acceptance test.

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
| PointerReleaseGrace | 0.35 |
| PointerCenterHeight | 0.5 |
| ForwardActivation | 0.15 |
| ActivationMinHeight | 0.25 |
| ActivationReleaseMargin | 0.12 |
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
→ (voice change) run --voice-self-test on the built exe, read voice-self-test.txt
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

**Build 2 milestone 2 (implemented, COMPILE VERIFIED):** `SafeDesktopActions`, `InstalledApps`
and `DesktopWindows` sit behind `ActionRouter.ExecuteRequest` on the UI thread. New semantic
actions launch indexed Start Menu/MSIX apps by name (ambiguity refuses), open http/https URLs,
search YouTube/Google/Bing, find up to eight newest personal files, open allowed document/media
files found in that same request, list visible windows, place windows in numbered monitor work
areas, and type up to 500 Unicode characters. Text input refuses shells/system tools and this
process. Window ordering uses `DesktopLayout` left-to-right, then top-to-bottom; placement uses
physical pixels and DWM visible-frame compensation. The existing user-defined custom launch
and key commands retain their behavior. No arbitrary keys or executable paths are exposed by
the new parameterized actions. No deletion, rename, file move or shell command action exists.

File search is limited to personal folders, skips reparse points, denies system/program paths,
and returns explicitly partial results after two seconds. Open revalidates the path/ancestors,
extension and request capability. `--ai-self-test` verifies policies with a dry-run router,
temporary fixtures, app ambiguity and one/two-monitor geometry including negative origins.
Report: `%LOCALAPPDATA%\KinectHomeOS\ai-self-test.txt`, exit 0/4. No physical action is tested.
The DeepSeek assistant built on these actions is §4.18.

1. **Hardware validation of the engine phase** (unchanged):
   - fixed hand roles;
   - clutch, and clutch-based scroll/swipe feel (tune `ScrollCurve`, dwell and clutch timings);
   - soft dead zone (re-tune `JitterDeadzone`);
   - stabilization latency;
   - guided calibration four-corner reach on the dual-monitor layout.
2. **Voice on hardware:** everyday conversation near the Kinect for several minutes (expect
   zero actions; count stray chimes), normal-voice wake reliability at Wake Sensitivity 65
   (raise it or the input level with Test wake word if needed, watching the confidence), chime
   audible and not re-triggering, speaking right after the chime, timeout, "cancel", "volume N"
   incl. 0/1/73/99/100/out of range, mute/unmute, the Kinect mic
   reconnect issue. Microphone selector: switch Default → Kinect → laptop → Bluetooth
   earbuds, wake/command on each, disconnect the selected earbuds while running (fallback, no
   crash), restart (choice persists).
3. **Validation of the KINECT-OS UI on the real machine:** acrylic backdrop and custom chrome
   (drag, snap, maximize margin, DPI change between monitors), compact mode round trip,
   widget drag/position persistence, tray menu, live cards while pointing, calibration
   progress UI, voice recognition with the Kinect mic (watch for sensor reconnects), the new
   routed actions (Win+Arrow, media keys), profile rename persistence.
4. Possibly expose clutch timings / scroll dead zone in the UI if hardware testing shows they
   need per-user tuning. They are currently `GestureTuning` constants.
5. Smoothing default mismatch (Settings 0.2 vs Default button 0.7).
6. **Hardware check of Build 1:** "Jarvis" wake reliability vs "Kinect" (and stray chimes in
   conversation), recording each app's dictation shortcut and "Claude/GPT/Cursor listen" in
   and out of the right app, open-app commands, one caption-button set (taskbar minimize, snap,
   help drawer open, Alt+F4 now that there is no system menu).
7. "Move window to display".
8. **Build 2 Shot 1 on hardware/network:** save a key (AI → Test key & save), then
   `--ai-self-test --live`; Jarvis → "set the volume to thirty"; "Claude listen" in/out of
   Claude; "put ChatGPT on the top right of my second screen" (incl. mixed DPI); "open Edge and
   search YouTube for lo-fi study music"; "open my resume"; end click and perceived latency
   (runtime.log has the timings); internet off keeps exact commands local; cancel an AI request
   with a new wake, voice off and a double clap; app-name ambiguity and file refusals.
9. **Build 2 Shot 2 on hardware:** the level ring moves while speaking; widget voice states
   are readable at a glance; the transcript line appears; the AI button opens a session and
   chimes without the wake word; the chat panel opens, scrolls, accepts a typed request and
   remembers open/closed after a restart; the widget still drags smoothly and stays on top.
   Fixes from Shot 1 hardware testing. Consider asynchronous file indexing if the two-second
   bounded personal-file search is noticeable.

## 10. Known gaps / observations

- A hard kill (Task Manager "End task" on the process tree / power loss) can still skip every
  handler. Unhandled exceptions on a non-UI thread, Windows session end and ProcessExit release
  the button via `MouseControl.ReleaseIfInjected()`. A recoverable UI-thread exception is logged,
  the injected button is released, an activity entry is posted, and the exception is marked
  handled so the process stays up. Settings are saved only on the normal quit path, so the old
  unhandled animation crash discarded the user's tuning. Out-of-memory, access violations and
  similar corrupted-state exceptions are still left unhandled.
- `CursorOutputLoop.Dispose()` is never called; Stop via Mode = Disabled is what runs.
- The log does not record per-frame data. Use Settings → Advanced diagnostics for live values.
- The acrylic backdrop needs Windows 11 22H2+; older builds get the opaque gradient. The
  offscreen previews always show the opaque fallback.
- A selected microphone is captured by the app (WASAPI) rather than opened by the speech
  engine, so its level meter is the app's own peak meter; System Default uses owned capture
  in Whisper mode and the recognizer's input in Windows mode. Privacy settings can block either (reported as
  "access denied").
- Selecting Bluetooth earbuds as the microphone switches them to the hands-free profile, which
  lowers their playback quality while voice is on - a Windows/Bluetooth limitation.
- "play" and "pause" both press the media play/pause key (a toggle): Windows exposes no
  explicit play or pause key.
- The chime plays through the default output device, so at volume 0 or muted it is silent
  (the window still opens).
- The self-test's synthetic voices are recognized less confidently than a person, so its
  recognition scenarios run at 0.45/0.45; only the conversation corpora run at the shipped
  thresholds.
- The Run buttons on the Actions page act on the foreground window, which is KINECT-OS itself
  while you click them (Minimize/Snap will move this window).
- The main window has no system menu (WS_SYSMENU removed to stop DWM drawing a second set of
  caption buttons): no Alt+Space menu; whether Alt+F4 still closes it is untested.
- Shortcuts Windows reserves (Win+H, Win+L…) cannot be recorded by pressing them in the Keys
  box - type them instead. A custom key combination goes to whatever has keyboard focus in
  the front window; "only in" checks the app, not which text box is focused.
- Custom phrases are heard by the same closed grammar, so a sentence beginning with one
  ("Claude is listening…") after a real wake can in principle match it; the lead/duration
  checks are the guard, and the self-test's carry-on corpus includes that sentence.

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
    create the tray icon, write `runtime.log` (`RuntimeLog.Suspend()`) or save settings
    (`MainWindow.IsOffscreenCheck`).
28. **No Windows action from WakeOnly.** The wake grammar and the command grammar are never
    enabled together, and no grammar ever contains both the wake word and a command: a wake
    word may not be a command phrase and a custom phrase may not contain the wake word.
    Whisper mode loads only the wake grammar and disables it during recording/transcription.
29. **One command per session.** A session starts from an accepted wake or an explicit user
    button press; everything after that (gate, one command, generations) is identical. Only a
    phrase whose speech began after the chime gate is accepted, and only via
    `CommandRecognized` → `VoiceViewModel` (session `TryAuthorize` + `TryMarkExecuted`).
    Whisper transcribes only post-gate PCM; results must match session and input generation.
30. **Deterministic voice parsing:** exact built-in phrases, exact custom phrases (by match
    key) and the strict volume pattern only; no substring/fuzzy matching; a custom phrase may
    not shadow a built-in phrase or the volume pattern; out-of-range numbers are refused,
    never clamped. No dictation grammar, no transcription of background speech.
31. Changes under `Models/Voice` must keep `--voice-self-test` passing (it never uses the mic,
    the speakers or executes anything).
32. **One microphone stream at a time; every input change is a full restart.** Switching,
    fallback and recovery go through `VoiceViewModel` (`RequestReinitialize` / `RetryStart`),
    which bump the event generation so nothing queued from the previous recognizer runs; the
    engine closes the capture stream before disposing the recognizer and always restarts in
    WakeOnly with no session. A new wake word or a changed set of custom phrases is the same
    kind of full restart.
33. **A custom key combination limited to an app is only sent when that app's process owns
    the foreground window** (checked at execution time); otherwise nothing is pressed.
34. **The AI acts only through `AssistantTools`** (strict schemas), the enabled custom phrases
    and assignable built-ins, executed through `ActionRouter` on the UI thread. No tool may
    delete, rename or move files, run shell commands or press arbitrary keys; OpenFile needs a
    same-request FindFiles result plus revalidation; TypeText refuses shells/system tools and
    KINECT-OS. One authorized unmatched request = one bounded assistant run (≤ 6 rounds, 25 s),
    cancelled by a new wake, an input-generation change, voice off or a double clap.
35. **Privacy:** audio and Whisper stay local. DeepSeek receives only one request's text, the
    monitor layout, the foreground process name, command names and tool results (window titles
    only via list_windows). The key is DPAPI-encrypted on disk, sent only to
    `https://api.deepseek.com`, and never logged, displayed or saved elsewhere.
