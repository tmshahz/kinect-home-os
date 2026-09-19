# AGENTS.md — Kinect Home OS (operational guide for Codex and other coding agents)

For the detailed architecture narrative see `CLAUDE.md`, which is the same project seen in
more depth. This file is the checklist. If code and docs disagree, the code wins. Update the
docs in the same change. Docs describe current truth; git history is the history.

## 1. Mission

Turn an Xbox One Kinect (Kinect v2) into a Windows spatial gesture-control system,
**Kinect Home OS**. The project forked from TangoChen's `KinectV2MouseControl` (MIT, 2018) and
has been substantially rebuilt. The control engine is stabilized (hardware status per
CLAUDE.md §5/§7) and now wears **KINECT-OS**, the control-center UI: shell window with
acrylic chrome, 8 pages, floating compact widget, tray icon, help drawer, activity feed,
local voice commands and an action catalog. The UI is compile + offscreen-render verified
only.

```
Kinect body input → body-relative tracking → gesture recognition → semantic actions → Windows control
```

Current interaction model:
- **Right hand = pointer only.** It has to settle briefly before taking the cursor. Fist =
  press/drag. Lasso = right click.
- **Left hand = secondary gestures only**, and only while the **left fist is closed (clutch)**.
  Steady fist, then move up/down = scroll. Fast sideways sweep = switch window. An open left
  hand does nothing.
- **Double clap** = control off/on. Tracking keeps running while off.

- **Voice (beta, off by default), strictly wake-gated:** say the wake word ("Jarvis" by
  default, user-editable on the Voice page, 1-3 words) on its own → ✦ chime → ONE command
  within 4 s ("volume 70", "mute", "next window", or a custom command) → back to waiting.
  Begin speaking within 4 s. Whisper (default) records only post-chime audio, ends after 0.7 s
  silence (10 s cap), and transcribes locally. Windows recognizes only the wake word in this
  mode; the selectable Windows fallback retains command grammars. Missing/broken Whisper
  automatically falls back with a notice. Exact local matches route like gestures; unmatched
  text currently shows "Not a command". **Custom commands** (Voice page → Your commands): the user's own phrase → a key
  combination (optionally only when a named app is in front, e.g. "Claude listen" →
  Ctrl+Shift+D only in `claude`), opening an app/file/URL, or a built-in action. Starters
  "GPT listen" / "Claude listen" / "Cursor listen" ship with blank keys.
  The microphone is selectable on the Voice page (System Default or a specific input,
  remembered, with automatic fallback when it disappears), with a live input meter, an Input
  level (device gain) slider, a Wake Sensitivity slider (default 65 → confidence floor ≈0.65),
  and a Test wake word mode that measures but never executes. **AI** = placeholder section only.

Future (NOT implemented): the DeepSeek assistant, more gestures, "move window to display",
deeper app control. Build 2 milestone 1 (local Whisper) is implemented.

## 2. Toolchain

- Windows 11, Kinect for Windows SDK 2.0 (`KINECTSDK20_DIR=C:\Program Files\Microsoft SDKs\Kinect\v2.0_1409\`)
- C# / WPF / **.NET Framework 4.8**, **legacy non-SDK csproj**, AnyCPU, Debug + Release
- References: `Microsoft.Kinect` 2.0.0.0 (`Private=False`), `System.Runtime.Serialization`
  (profiles JSON), `System.Speech` (voice), `System.Windows.Forms` + `System.Drawing` (tray icon)
- Build tool: VS 2022 **Preview** MSBuild
- **Not** a Node/npm/web or `dotnet`-SDK project. No `npm`, no `dotnet build`, no test projects.
- **UI check without launching:** `bin\Debug\KinectV2MouseControl.exe --ui-smoke-test` (exit 0
  = pass). Loads every view offscreen, writes `%LOCALAPPDATA%\KinectHomeOS\ui-smoke-test.txt`
  and `ui-preview\*.png` (read the PNGs). No window, no sensor, no voice, no tray, no log, and
  no settings saved (`MainWindow.IsOffscreenCheck`; before that fix every run overwrote the
  exe's `user.config` with engine defaults).
- **Voice check without a microphone:** `bin\Debug\KinectV2MouseControl.exe --voice-self-test`
  (~5 min, exit 0 = pass, 3 = fail). Parser, grammars, Core Audio round trip, chime, then the
  real wake-gated engine on synthesized speech with the chime looped back as echo; commands
  are recorded, never executed. Also runs four real Whisper scenarios when installed (SKIP
  only if files are missing); `--whisper-only` focuses on those plus unit checks.
  Report: `%LOCALAPPDATA%\KinectHomeOS\voice-self-test.txt`.

### Build (both must pass, target 0 errors / 0 warnings)

PowerShell:
```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Preview\MSBuild\Current\Bin\MSBuild.exe" "C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl.sln" /t:Build /p:Configuration=Debug /v:minimal /nologo
& "C:\Program Files\Microsoft Visual Studio\2022\Preview\MSBuild\Current\Bin\MSBuild.exe" "C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl.sln" /t:Build /p:Configuration=Release /v:minimal /nologo
```

Git Bash (dash-style switches, because MSYS mangles `/p:`):
```bash
"/c/Program Files/Microsoft Visual Studio/2022/Preview/MSBuild/Current/Bin/MSBuild.exe" src/KinectV2MouseControl.sln -t:Build -p:Configuration=Debug -v:minimal -nologo -clp:Summary
"/c/Program Files/Microsoft Visual Studio/2022/Preview/MSBuild/Current/Bin/MSBuild.exe" src/KinectV2MouseControl.sln -t:Build -p:Configuration=Release -v:minimal -nologo -clp:Summary
```

- `-t:Rebuild` for a guaranteed fresh output.
- MSB3027/MSB3021 "file locked" means the exe is running. Ask the user to close it.
- **Do not launch the exe without asking.** It takes over the user's real cursor if they are in
  front of the sensor.

### Executables

- Debug: `C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl\bin\Debug\KinectV2MouseControl.exe`
- Release: `C:\Users\taimu\Cursor\KinectV2MouseControl\src\KinectV2MouseControl\bin\Release\KinectV2MouseControl.exe`
- `executables/KinectV2MouseControl_EXE.zip` = upstream's 2018 binary. It is historical.
- `bin/` and `obj/` are git-ignored. **Never commit exes or build output.**

### Runtime files

- Local Whisper: `%LOCALAPPDATA%\KinectHomeOS\whisper\` (`bin\whisper-server.exe`,
  `models\ggml-base.en.bin`, `server.log`). Optional test override `KINECTOS_WHISPER_DIR`.
  The hidden loopback server is job-owned and stops with voice. Audio is never saved.

- `user.config` (last-used settings): `%LOCALAPPDATA%\KinectV2MouseControl\...exe_Url_<hash>\1.2.1.0\`
  - it is **per exe path**;
  - it is saved on normal close only.
- Profiles: `%LOCALAPPDATA%\KinectHomeOS\profiles.json`
  - 3 slots, shared by Debug and Release;
  - atomic write;
  - an unreadable file is moved to `.bad`.
- Custom voice commands: `%LOCALAPPDATA%\KinectHomeOS\voice-commands.json`
  - shared by Debug and Release; saved ~0.6 s after each edit (and on quit); atomic write;
  - missing file = the three starters (not written until the first edit); unreadable → `.bad`.
- The wake word is a UI setting in `user.config`: `VoiceWakePhrase` (default "Jarvis"). An
  older `VoiceWakeWord` entry ("Kinect") left by an earlier build is deliberately ignored.
- Runtime log: `%LOCALAPPDATA%\KinectHomeOS\runtime.log` (previous launch in `runtime.prev.log`)
  - records sensor availability, body lock, pointer sessions (with dt/noise/glitch stats),
    resets, clutch, calibration and profiles;
  - ask the user for it when diagnosing intermittent problems.

## 3. Code map (`src/KinectV2MouseControl/`)

| File | Responsibility |
|---|---|
| `Models/KinectCursor.cs` | Orchestrator. Frame handler, activation zone, **right-hand-only pointer session**, grip/click/drag + anchoring, left-hand clicking for legacy two-hand modes, hover, stall watchdog, control gate, calibration glue, display changes, `ApplySettings` batches, diagnostics, all reset paths |
| `Models/CursorControlInput/KinectReader.cs` | Sensor open/close/availability. Scored body lock (≥3/6 core joints Tracked, nearest). Degraded-body switch. `OnTrackedBody` / `OnLostTracking` |
| `Models/CursorControlInput/PointerStabilizer.cs` | Waiting → Stabilizing → Active, settle time/frames, glitch skip / destabilize, seed position |
| `Models/CursorControlInput/KinectBodyHelper.cs` | SpineBase-relative geometry, joint weights/states, hand state + confidence |
| `Models/CursorControlInput/HandStateFilter.cs` | Open/closed debounce (grip; reused by the clutch) |
| `Models/CursorMapper/CursorMapper.cs` | Mapping, pre-filter monitor clamp, One Euro, **soft** jitter dead zone, `SeedSmoothing`, `ResidualNoise` |
| `Models/CursorMapper/OneEuroFilter.cs` | `LowPassFilter`, `OneEuroVectorFilter` (+`Seed`) |
| `Models/CursorMapper/StationaryLock.cs` | Absolute lock with hysteresis and decaying release |
| `Models/CursorMapper/PointerCalibration.cs` | Calibrated per-axis range + **guided 5-point capture** (centre/left/right/top/bottom, 5% edge assist) |
| `Models/CursorControlOutput/CursorOutputLoop.cs` | Background ~125 Hz thread. **Sole `SetCursorPos` caller** |
| `Models/CursorControlOutput/MouseControl.cs` | SendInput buttons/wheel, `SetCursorPos` wrapper, `ReleaseIfInjected` fail-safe |
| `Models/CursorControlOutput/VirtualScreen.cs` | `VirtualScreen` bounds + monitor rects. `DesktopLayout.Clamp` (nearest real monitor) |
| `Models/CursorControlOutput/KeyboardControl.cs`, `Win32Input.cs` | Alt+Tab chords, `SendChord` (custom key combinations, with scan codes for Electron apps); SendInput plumbing |
| `Models/Actions/ControlAction.cs`, `ActionRouter.cs` | Semantic actions → Win32 (discrete-action boundary). `Execute(action, source)`; window-management, media, control on/off, `LaunchApp` and `SendKeys` actions |
| `Models/Actions/ActionCatalog.cs` | Human-readable action index (implemented vs planned) for the Actions page and the voice grammar |
| `Models/Voice/WakeGatedVoiceEngine.cs`, `.Whisper.cs` | Wake/session state machine, Windows fallback, post-gate recording and exact parsing, session/generation authorization |
| `Models/Voice/PcmTapStream.cs`, `WhisperService.cs` | Owned PCM clock/ring, VAD, hidden local server lifecycle and in-memory transcription |
| `Models/Voice/VoiceSession.cs`, `VoiceGrammars.cs`, `VoiceCommandParser.cs`, `VoiceCommand.cs` | Session/decision types; SRGS wake + command grammars; deterministic parser (built-in phrases → custom phrases → volume pattern) + `SpokenNumber`; built-in command catalog |
| `Models/Voice/VoiceWakeWord.cs` | `VoicePhrases` (grammar tokens with acronym spelling "GPT" → "G P T", match keys, "Kinect" IPA) and `VoiceWakeWord` (default "Jarvis", validation, max duration) |
| `Models/Voice/CustomVoiceCommands.cs` | `CustomCommandDefinition` (stored), `CustomCommandStore` (voice-commands.json), `CustomCommandRules` (phrase rules, assignable actions), `CustomPhraseSet` (frozen phrases the running recognizer listens for) |
| `Models/CursorControlOutput/KeyChord.cs`, `ForegroundApp.cs` | Key combination parse/format/VKs ("Ctrl+Shift+D"); foreground window's process name + running windowed apps |
| `Models/Voice/VoiceFeedbackSounds.cs`, `VoiceSelfTest.cs` | Synthesized chime/dismiss; `--voice-self-test` |
| `Models/Voice/AudioInputDevices.cs`, `MicrophoneCaptureStream.cs` | Core Audio capture-device list + change notifications; WASAPI capture of a chosen device as the recognizer's input stream |
| `Models/CursorControlOutput/SystemVolume.cs` | Core Audio render volume / explicit mute (SetVolume, Mute, Unmute); non-negative HRESULT = success (S_FALSE on no-op mute) |
| `Models/CursorControlOutput/CaptureVolume.cs` | Core Audio capture endpoint input level (the Voice page Input level slider); writability by no-op write, not the hw-support flags |
| `Models/Diagnostics/ActivityLog.cs` | In-memory recent-activity feed (coalescing) shown on Home/Settings |
| `ViewModels/ShellViewModel.cs` | Navigation, help drawer, compact/tray requests, activity collection, UI settings, commands |
| `ViewModels/LiveStatus.cs` | Bindable engine snapshot (ControlState Off/Standby/Ready/Active, hands, gestures, signal, calibration) |
| `ViewModels/VoiceViewModel.cs`, `ActionsViewModel.cs`, `DisplaysViewModel.cs`, `ProfileSlotViewModel.cs` | Page view models. VoiceViewModel also owns the wake word (draft/apply) and the custom command list (validate, save, restart on phrase changes, run) |
| `ViewModels/CustomCommandRowViewModel.cs` | One editable custom command row |
| `Views/MainWindow.xaml(.cs)` | KINECT-OS shell: WindowChrome + acrylic (`WindowBackdrop`), rail, header pills, page host, help drawer, compact mode, tray. Only the app's own caption buttons show (top-right corner, above the drawer); `WindowBackdrop.HideSystemCaptionButtons` removes WS_SYSMENU so DWM stops drawing its own |
| `Views/OverlayWindow.xaml(.cs)` | Floating compact widget |
| `Views/Pages/*.xaml` | Home, Gestures, Voice, Actions, Displays, Profiles, Settings, AiPage |
| `Views/Controls/TuningSlider`, `IconView.cs`, `KeyChordBox.cs` | Labelled slider with help glyph; vector icon control; key-combination recorder box |
| `Views/ControlHelp.cs`, `HelpHub.cs` | Help text single source; hover/pin routing to the drawer |
| `Themes/Palette.xaml`, `Icons.xaml`, `Controls.xaml` | Visual identity, icon geometries, all shared styles/converters |
| `Models/Gestures/GestureEngine.cs` | Priority: Clap → gate/idle → vocabulary (GripToPress only) → Lasso → **Clutch** → Swipe → Scroll |
| `Models/Gestures/SecondaryClutch.cs` | Left-fist clutch → `SecondaryGestureArmed` |
| `Models/Gestures/GestureContext.cs` | `HandSnapshot`, per-frame context, `PointerHand`/`SecondaryHand` constants, `IsSecondaryGestureArmed` |
| `Models/Gestures/GestureTuning.cs` | All thresholds: activation, lasso, clutch, scroll, swipe, clap, stabilization, lock |
| `Models/Gestures/Recognizers/*.cs` | Clap, Lasso, Scroll (steady-neutral + curve), Swipe (clutch-gated) |
| `Models/Diagnostics/RuntimeLog.cs` | Event log |
| `Models/Profiles/ProfileStore.cs` | `TuningProfile` (nullable members), slots, JSON store |
| `ViewModels/KinectCursorViewModel.cs` | Engine settings, Load/Save, defaults, profiles (+modified tracking, rename persists), calibration, `IsControlEnabled`, `ExecuteAction`, `LiveStatus` refresh, `Quit` |
| `Views/RadioCheckedToBoolConverter.cs` | Mode chips; `ConvertBack` must return `Binding.DoNothing` for unchecked |
| `App.xaml.cs` | Merges themes, creates the window, `--ui-smoke-test`, crash / session-end / process-exit mouse release |
| `Properties/Settings.*`, `App.config` | Persisted settings + defaults |

Control modes: Disabled, MoveOnly, GripToPress (default; the only mode with lasso/clutch/
scroll/swipe), HoverToClick, MoveGripPressing and MoveLiftClicking (the left hand clicks while
the right points). Double clap works in every mode except Disabled.

## 4. Engineering invariants (non-negotiable)

1. Single cursor-position writer: only `CursorOutputLoop` moves the cursor.
2. Never write the cursor from the Kinect body-frame handler. Publish a target instead.
3. Keep the high-rate output architecture (thread, ease, write-on-change, `ClearTarget`,
   `UpdateOutputLoopState` as the single start/stop decision).
4. Don't casually rewrite the filter/click pipeline:
   `clamp → One Euro → soft dead zone → stationary lock → click freeze/anchor → clamp → output loop`,
   plus `HandStateFilter`. It is hardware-tuned.
5. No injected mouse-down may survive any teardown: tracking loss, sensor unavailable, stall,
   control disable, mode change, calibration, profile load/defaults, display change, session
   end/glitch destabilize, app exit, crash. `ReleaseAllGrips()` goes before `ResetControlState()`.
6. Fixed hand roles: right = pointer (the only hand that can own/anchor the cursor), left =
   secondary. No first-activated-hand logic, no handoff, no left fallback.
7. Secondary gestures only via `SecondaryGestureArmed` (the clutch). Don't scatter left-hand
   `Closed` checks.
8. Every pointer session goes through `PointerStabilizer` + `SeedSmoothing`. All teardowns
   reset it.
9. Recognizers emit `ControlAction`s via `IActionSink`, write state before emitting, and make
   no Win32/cursor calls.
10. `ActionRouter` is the discrete-action boundary for gesture, voice and agent inputs. Pointer
    movement stays out.
11. Double-clap disable keeps the sensor and body tracking running. Only `Mode = Disabled`
    closes the sensor. The clap stays above the gate.
12. Preserve body-relative (SpineBase) tracking.
13. Never retain `GestureContext`. No per-frame allocations.
14. DPI awareness + physical-pixel `DesktopLayout`. `SetCursorPos`, not SendInput absolute.
    No primary-monitor assumptions.
15. Use real sensor `deltaTime`.
16. Uncalibrated mapping geometry stays the original. Calibrated mode ignores MoveScale.
17. Settings batches go through `KinectCursor.ApplySettings`.
18. No broad refactors, framework migration, or frontend redesign unless asked.
19. Don't rename the exe/assembly or bump AssemblyVersion without telling the user.
20. Keep the upstream MIT license notice.
21. Never describe hardware behaviour as verified because it compiles.
22. The UI never touches the engine directly: views bind to view models, control changes go
    through `KinectCursorViewModel`.
23. `ActionRouter.Execute` on the UI thread only; voice marshals through the Dispatcher.
    It returns false when nothing was performed.
24. Voice/AI use the `ControlAction` vocabulary and the catalog; they never move the pointer.
25. `ControlHelp` is the only source of help text; new controls get an entry (`HelpKey` =
    title). WPF binds to properties, not fields.
26. The smoke test never shows a window, opens the sensor, starts voice, creates the tray
    icon, writes `runtime.log` or saves settings (`MainWindow.IsOffscreenCheck`).
27. **Voice: no Windows action from WakeOnly.** Wake grammar and command grammar are never
    enabled together; no grammar contains both the wake word and a command (a wake word may not
    be a command phrase, and a custom phrase may not contain the wake word); no dictation.
28. **One command per wake**, only from a phrase whose speech began after the chime gate, only
    via `CommandRecognized` → `VoiceViewModel` (`TryAuthorize` + `TryMarkExecuted`).
    Whisper uses only post-gate samples and never loads a command grammar. Results carry
    both session and input generation; recording/transcription is cancelled on restart.
29. Voice parsing is exact (catalog phrases, then the user's custom phrases by match key, then
    the strict volume pattern); out-of-range volume is refused, never clamped. A custom phrase
    may not shadow a built-in phrase or the volume pattern. Keep `--voice-self-test` passing
    for any `Models/Voice` change.
30. **One microphone stream at a time; every input change is a full restart** through
    `VoiceViewModel` (`RequestReinitialize` / `RetryStart`), which bumps the event generation so
    nothing queued from the old recognizer runs. The engine closes the capture stream before
    disposing the recognizer and restarts in WakeOnly with no session.
31. A custom key combination limited to an app is sent only when that app's process owns the
    foreground window; otherwise nothing is pressed. Changing the wake word or the set of
    custom phrases is a full recognizer restart, like a microphone change.

## 5. Change protocol (every task)

1. Inspect the relevant implementation before editing.
2. Inspect `git status` / `git diff`. Don't clobber uncommitted user work.
3. Preserve working functionality unless the task explicitly changes it.
4. No unrelated refactors.
5. Make the smallest coherent implementation that matches the surrounding style: C# 7,
   explicit braces, long-form properties, and `///` comments that give the *why*.
6. Build **Debug**.
7. Build **Release**.
8. Report errors **and** warnings (target 0/0). For any XAML/view change also run
   `--ui-smoke-test` and look at the previews. For any voice change run `--voice-self-test`.
9. List every source file changed.
10. Give the exact executable paths (§2). The fresh `bin\Debug` / `bin\Release` outputs are the
    current test builds.
11. Spell out what specifically needs physical Kinect testing.
12. Label status **COMPILE VERIFIED** vs **HARDWARE VERIFIED**.

Adding a tuning setting touches:
- `Settings.settings`, `Settings.Designer.cs`, `App.config`;
- `KinectCursorViewModel`: property, `TuningProperties`, Load, Save, `DEFAULT_*`/ResetToDefault,
  Capture/ApplyProfile;
- `TuningProfile`;
- a `TuningSlider` on GesturesPage (advanced tuning) or DisplaysPage (range values);
- a `ControlHelp` entry whose Title equals the slider's `HelpKey`/label.

Adding a UI-only setting: the three settings files + `ShellViewModel` property +
`LoadUiSettings`/`SaveUiSettings` + a toggle on SettingsPage + `ControlHelp` entry.

Adding an action: `ControlActionType` + `ActionRouter` case (+ `KeyboardControl` chord) +
`ActionCatalog` descriptor (+ `VoiceCommandCatalog` phrase, `ControlAction.Describe`).

New `.cs` → csproj `<Compile Include>`; new `.xaml` → `<Page Include>` + `DependentUpon`.

## 6. Regression checklist (input/pointer/gesture changes)

- [ ] Pointer smoothness (slow aim, fast sweep, no 30 Hz stepping, no dead-zone hops)
- [ ] Startup: clean single snap after settle, no jump/jitter burst, repeated launches
- [ ] Reacquisition after lowering the right hand / tracking loss re-stabilizes cleanly
- [ ] Right hand exclusively points; left hand never becomes the pointer
- [ ] Stationary lock engages/breaks cleanly
- [ ] Click anchoring, click freeze, grip click, drag
- [ ] Lasso right click (one per gesture, not during drag)
- [ ] Left open hand does nothing; left fist arms the clutch; releasing stops everything
- [ ] Scroll: settle, neutral, direction, curve, invert, stop on release, no burst on close-while-moving
- [ ] Swipe: only with clutch, both directions, cooldown, no reverse on return, not during drag, not from clapping
- [ ] Double clap toggles both ways, keeps tracking, releases a drag
- [ ] Tracking loss / sensor unplug / stall: button released, cursor handed back
- [ ] Mode change / profile load / Default / calibration / display change: no stuck button
- [ ] Calibration: guided capture, cancel, rejection, four-corner reach, revert by unticking
- [ ] Virtual desktop / dual-monitor mapping, including mismatched monitor sizes and negative origins

Voice (any `Models/Voice` or vocabulary change):
- [ ] `--voice-self-test` passes
- [ ] Minutes of normal conversation near the mic: zero actions (count stray chimes)
- [ ] "Jarvis" → chime → "volume 43" → 43%; back to waiting
- [ ] Wake word box: change it, Apply, the new word wakes and the old one doesn't; restart keeps it
- [ ] Custom command: record the app's shortcut, "Jarvis" → "Claude listen" with Claude in
      front starts dictation; with another app in front nothing is pressed (HUD says why)
- [ ] Custom "open" and "built-in action" commands run; a disabled or invalid row is not heard
- [ ] "volume 100" / "mute" without the wake cycle: nothing
- [ ] Second command after one wake: nothing; timeout; "cancel"; unknown phrase; "volume 200" refused
- [ ] Own chime never wakes or commands; speaking right after the chime works
- [ ] Microphone: switch Default → each input and back; wake/command on each; nothing runs
      during a switch; unplug/disconnect the selected input while running (fallback, no
      crash, returns when reconnected); restart keeps the choice
- [ ] Normal-voice "Jarvis" wakes without yelling at Wake Sensitivity 65; slider changes bite
- [ ] Live meter moves; Input level slider changes the device gain (read-only where unsupported)
- [ ] Test wake word shows confidence and runs NOTHING
- [ ] "volume 0/1/73/99/100" set the exact Windows level; "volume 101/200" refused

## 7. Hardware-tested reference configuration (user-reported; preferences, not defaults)

These were tested **before** the soft dead zone and clutch changes. Re-validate them.

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

Code defaults differ (see `CLAUDE.md` §7). Don't change defaults to match these unless asked.

Hardware tip: on Windows 11, Kinect microphone audio enhancements caused repeated sensor
disconnect/reconnect. Disabling them fixed it. It shows as repeated "Sensor UNAVAILABLE" lines
in `runtime.log`.

## 8. Current backlog

1. **Hardware validation of the current phase.** Everything below is COMPILE VERIFIED only:
   - fixed hand roles;
   - left-fist clutch;
   - clutch-based scroll (steady neutral, curve, invert) and swipe;
   - soft dead zone;
   - startup stabilization / glitch rejection;
   - body selection;
   - guided calibration + edge assist + nearest-monitor clamp;
   - profiles, help, runtime log;
   - mode-radio converter fix.
2. **Validate the wake-gated voice on hardware** (checklist in §6): real-voice confidence vs
   the 0.80 / 0.70 defaults, stray chimes during conversation, the real chime path, the Kinect
   microphone (watch for sensor reconnects), the microphone selector (switching, Bluetooth
   loss/fallback, persistence). First session: no random actions; occasional false wakes whose
   confidence (0.93-0.95) matched real wakes - see CLAUDE.md §4.16 before tuning.
3. **Validate the KINECT-OS UI on the real machine:** acrylic/custom chrome (drag, snap,
   maximize, cross-monitor DPI; only ONE set of caption buttons, usable with the help drawer
   open; taskbar click still minimizes; Alt+F4 behaviour without the system menu), compact
   mode + widget position, tray menu, live cards while pointing, calibration progress UI, the
   new routed actions, profile rename persistence, the Voice page custom command editor.
4. Expose clutch timings / scroll dead zone in the UI only if hardware testing shows per-user
   tuning is needed.
5. Smoothing default mismatch (Settings 0.2 vs Default button 0.7).
6. "Move window to display"; the assistant layer (DeepSeek tool calling + Whisper speech-to-
   text, "Build 2"), which would sit after a parser refusal, inside the same wake session.
