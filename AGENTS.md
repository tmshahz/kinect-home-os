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

- **Voice (beta, off by default), strictly wake-gated:** say "Kinect" on its own → ✦ chime →
  ONE command within 4 s ("volume 70", "mute", "next window"...) → back to waiting. Anything
  said without that cycle does nothing. Local Windows speech recognizer, routed like gestures.
  **AI** = placeholder section only.

Future (NOT implemented): custom voice commands, the assistant layer, more gestures, "move
window to display", deeper app control.

## 2. Toolchain

- Windows 11, Kinect for Windows SDK 2.0 (`KINECTSDK20_DIR=C:\Program Files\Microsoft SDKs\Kinect\v2.0_1409\`)
- C# / WPF / **.NET Framework 4.8**, **legacy non-SDK csproj**, AnyCPU, Debug + Release
- References: `Microsoft.Kinect` 2.0.0.0 (`Private=False`), `System.Runtime.Serialization`
  (profiles JSON), `System.Speech` (voice), `System.Windows.Forms` + `System.Drawing` (tray icon)
- Build tool: VS 2022 **Preview** MSBuild
- **Not** a Node/npm/web or `dotnet`-SDK project. No `npm`, no `dotnet build`, no test projects.
- **UI check without launching:** `bin\Debug\KinectV2MouseControl.exe --ui-smoke-test` (exit 0
  = pass). Loads every view offscreen, writes `%LOCALAPPDATA%\KinectHomeOS\ui-smoke-test.txt`
  and `ui-preview\*.png` (read the PNGs). No window, no sensor, no voice, no tray, no log.
- **Voice check without a microphone:** `bin\Debug\KinectV2MouseControl.exe --voice-self-test`
  (~5 min, exit 0 = pass, 3 = fail). Parser, grammars, Core Audio round trip, chime, then the
  real wake-gated engine on synthesized speech with the chime looped back as echo; commands
  are recorded, never executed. Report: `%LOCALAPPDATA%\KinectHomeOS\voice-self-test.txt`.

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

- `user.config` (last-used settings): `%LOCALAPPDATA%\KinectV2MouseControl\...exe_Url_<hash>\1.2.1.0\`
  - it is **per exe path**;
  - it is saved on normal close only.
- Profiles: `%LOCALAPPDATA%\KinectHomeOS\profiles.json`
  - 3 slots, shared by Debug and Release;
  - atomic write;
  - an unreadable file is moved to `.bad`.
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
| `Models/CursorControlOutput/KeyboardControl.cs`, `Win32Input.cs` | Alt+Tab chords; SendInput plumbing |
| `Models/Actions/ControlAction.cs`, `ActionRouter.cs` | Semantic actions → Win32 (discrete-action boundary). `Execute(action, source)`; window-management, media, control on/off and `LaunchApp` actions |
| `Models/Actions/ActionCatalog.cs` | Human-readable action index (implemented vs planned) for the Actions page and the voice grammar |
| `Models/Voice/WakeGatedVoiceEngine.cs` | Voice state machine WakeOnly → Acknowledging (chime) → Listening (4 s); wake/command acceptance (confidence, lead, pre-silence, duration, gate), timeout, one command per wake |
| `Models/Voice/VoiceSession.cs`, `VoiceGrammars.cs`, `VoiceCommandParser.cs`, `VoiceCommand.cs` | Session/decision types; SRGS wake + command grammars; deterministic parser + `SpokenNumber`; command catalog |
| `Models/Voice/VoiceFeedbackSounds.cs`, `VoiceSelfTest.cs`, `AudioInputDevices.cs` | Synthesized chime/dismiss; `--voice-self-test`; mic list |
| `Models/CursorControlOutput/SystemVolume.cs` | Core Audio master volume / explicit mute (SetVolume, Mute, Unmute) |
| `Models/Diagnostics/ActivityLog.cs` | In-memory recent-activity feed (coalescing) shown on Home/Settings |
| `ViewModels/ShellViewModel.cs` | Navigation, help drawer, compact/tray requests, activity collection, UI settings, commands |
| `ViewModels/LiveStatus.cs` | Bindable engine snapshot (ControlState Off/Standby/Ready/Active, hands, gestures, signal, calibration) |
| `ViewModels/VoiceViewModel.cs`, `ActionsViewModel.cs`, `DisplaysViewModel.cs`, `ProfileSlotViewModel.cs` | Page view models |
| `Views/MainWindow.xaml(.cs)` | KINECT-OS shell: WindowChrome + acrylic (`WindowBackdrop`), rail, header pills, page host, help drawer, compact mode, tray |
| `Views/OverlayWindow.xaml(.cs)` | Floating compact widget |
| `Views/Pages/*.xaml` | Home, Gestures, Voice, Actions, Displays, Profiles, Settings, AiPage |
| `Views/Controls/TuningSlider`, `IconView.cs` | Labelled slider with help glyph; vector icon control |
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
    icon or writes `runtime.log`.
27. **Voice: no Windows action from WakeOnly.** Wake grammar and command grammar are never
    enabled together; no grammar contains both the wake word and a command; no dictation.
28. **One command per wake**, only from a phrase whose speech began after the chime gate, only
    via `CommandRecognized` → `VoiceViewModel` (`TryAuthorize` + `TryMarkExecuted`).
29. Voice parsing is exact (catalog phrases + strict volume pattern); out-of-range volume is
    refused, never clamped. Keep `--voice-self-test` passing for any `Models/Voice` change.

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
- [ ] "Kinect" → chime → "volume 43" → 43%; back to waiting
- [ ] "volume 100" / "mute" without the wake cycle: nothing
- [ ] Second command after one wake: nothing; timeout; "cancel"; unknown phrase; "volume 200" refused
- [ ] Own chime never wakes or commands; speaking right after the chime works

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
   microphone (watch for sensor reconnects).
3. **Validate the KINECT-OS UI on the real machine:** acrylic/custom chrome (drag, snap,
   maximize, cross-monitor DPI), compact mode + widget position, tray menu, live cards while
   pointing, calibration progress UI, the new routed actions, profile rename persistence.
4. Expose clutch timings / scroll dead zone in the UI only if hardware testing shows per-user
   tuning is needed.
5. Smoothing default mismatch (Settings 0.2 vs Default button 0.7).
6. Custom voice commands, `LaunchApp` targets, "move window to display", the assistant layer
   (which would sit after a parser refusal, inside the same wake session).
