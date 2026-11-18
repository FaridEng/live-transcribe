# Architecture: LiveTranscribe (MVP)

Implements [product-requirements.md](product-requirements.md) v1.1. Requirement IDs (FR-x, D-x, NFR-x) are referenced throughout.

**Status:** v0.3. M0–M3 implemented; see §7 for milestone status.

---

## 1. Technology decisions

| Area | Choice | Why |
|---|---|---|
| Language / runtime | **C# on .NET 10 (LTS)**, `net10.0-windows` | Chosen by the user. .NET 10 is the current LTS. SDK 10.0.401 is installed and pinned in `global.json`. |
| UI | **WPF** | Mature. Supports non-activating topmost windows (FR-2.8), per-monitor DPI v2 (FR-2.9), and RTL text rendering for Persian previews (NFR-5). |
| Tray icon | **WinForms `NotifyIcon`** (`UseWindowsForms`) | Zero extra dependencies; icons are drawn at runtime per state (FR-2.13, FR-6.2). |
| Audio capture | **NAudio** (WASAPI) | Device enumeration, default-device change and disconnect notifications (FR-7), shared-mode capture, resampling to 16 kHz mono PCM. |
| Speech engine transport | `System.Net.WebSockets.ClientWebSocket` + `System.Text.Json` | Gemini Live API is WebSocket + JSON. No SDK dependency, so the engine layer stays swappable (FR-13). |
| Focus / field inspection | **UI Automation (UIA3 COM)** via `FlaUI.UIA3` | Focused element, `IsPassword` (FR-3.7), window and process identity for the focus rule (FR-3.6). |
| Global hotkey | **Low-level keyboard hook** (`WH_KEYBOARD_LL`) | `RegisterHotKey` has no key-up, so it can't do hold-to-talk (FR-2.2). The hook also suppresses the key (FR-2.1) and works by virtual key regardless of layout (FR-2.4). |
| Secrets | **DPAPI** via direct `CryptProtectData` P/Invoke, current-user scope | Key encrypted and bound to the Windows user, with no extra package (FR-11.3). |
| Settings | JSON file in `%APPDATA%\LiveTranscribe\` | Per-user (FR-8), human-readable, secrets stored separately and encrypted. |
| Logging | Small built-in file logger with key redaction | Redaction by default: no audio, transcripts, or keys (FR-11.5). |
| Tests | xUnit | Core logic has no Windows dependencies and is fully unit-testable. |

---

## 2. Elevated apps and autostart (D18)

Windows UIPI blocks a normal-integrity process from sending input to, or inspecting, an elevated window. The MVP handles this as follows:

- **The app runs elevated.** Its manifest requests `requireAdministrator`.
- **Autostart uses a Task Scheduler task** set to *Run with highest privileges* at user logon. That launches elevated **without a UAC prompt each sign-in**. The task is created and removed from Settings → "Launch at sign-in". Creating it requires one UAC confirmation, the first time only.
- **Manual launch** shows a UAC prompt, which is acceptable for personal use (D19).
- **Rejected alternative:** a `uiAccess=true` manifest avoids running as admin, but the executable must be code-signed and installed under `Program Files`. Reconsider only if the app is ever distributed.

**What is actually built (2026-09-20).** The user dropped M7 and then asked for autostart on its own, so the two halves came apart:
- **No elevation.** The manifest stays `asInvoker`. Dictation into a window that runs as administrator does not work, by the user's choice (T15).
- **Autostart is the per-user Run key,** `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `LiveTranscribe`, holding the quoted path of the running executable (`StartupRegistration`). Since the app is not elevated, the Task Scheduler task has no purpose: the Run key needs no administrator rights and shows no prompt at sign-in. Settings → Windows → "Start LiveTranscribe when I sign in to Windows" writes and removes it, and the checkbox reads Windows itself rather than the settings file, so an entry removed with Task Manager or Autoruns shows as off. Every start reconciles the entry with the setting, which repairs the path after the app is moved or rebuilt elsewhere. Verified on 2026-09-20: on → the quoted path appears, off → the value is gone.

---

## 3. Solution structure

```
LiveTranscribe.sln
├─ src/
│  ├─ LiveTranscribe.Core/               net10.0 (no Windows dependencies)
│  │  ├─ Dictation/                      state machine, DictationController, session model
│  │  ├─ Engines/                        ISpeechEngine, ISpeechSession, capabilities, profiles
│  │  ├─ Text/                           text pipeline + deterministic Persian normalizer
│  │  ├─ Settings/                       settings model, validation, defaults
│  │  └─ Abstractions/                   interfaces implemented by the Windows layer
│  ├─ LiveTranscribe.Engines.Gemini/     net10.0
│  │  ├─ GeminiLiveEngine                WebSocket streaming (default profile)
│  │  └─ GeminiFileEngine                record-then-send (FR-13.11)
│  ├─ LiveTranscribe.Platform.Windows/   net10.0-windows
│  │  ├─ Input/                          keyboard hook, text insertion strategies
│  │  ├─ Automation/                     UIA focus tracking, password detection
│  │  ├─ Audio/                          WASAPI capture, device monitor
│  │  ├─ Security/                       DPAPI secret store
│  │  └─ System/                         autostart task, session lock/sleep, full-screen detection
│  └─ LiveTranscribe.App/                net10.0-windows, WPF, composition root
│     ├─ Tray/  FloatingControl/  Indicator/  Windows/  Onboarding/
│     └─ Assets/                         icons, sounds, Vazirmatn font
└─ tests/
   ├─ LiveTranscribe.Core.Tests/         state machine, normalizer, pipeline, settings
   └─ LiveTranscribe.Engines.Gemini.Tests/  message serialization and parsing, error mapping
```

Dependency direction: `App → Platform.Windows, Engines.Gemini → Core`. Core never references the other projects.

---

## 4. Components

### 4.1 DictationController (Core)
Owns the session lifecycle and the FR-6.1 state machine. It is the only component that changes state; everything else raises events or is called by it.

```
            ┌──────────── Cancel / tap < 300 ms ───────────┐
            ▼                                               │
NotConfigured ─(key+mic ok)─► Ready ─activate─────────────► Listening
                               ▲  ▲                           │  │ stop
                               │  └── BlockedPasswordField ◄──┘  ▼
                               │      (caret in a password field)
                               │                                         Processing ──activate──► Busy (transient)
                               │                                              │
                               ├──── Inserted ◄──── target ok ───────────────┤
                               ├──── NoSpeech ◄──── empty final ─────────────┤
                               ├──── NotInserted(text kept) ◄── focus moved / password field / insert failed
                               └──── Error(Mic|Network|Auth|Quota|Transcription) ─► Retry ─► Processing
Paused ◄──► Ready  (tray)
```

**Password fields are refused, with a one-dictation override (FR-3.7, D7, built 2026-09-20).** The
microphone still starts first so nothing is clipped, and the audio still never leaves the process
before the UIA check passes. When the check says "password field", the session is aborted and its
audio is dropped — nothing was sent and nothing is kept for retry.

What is new is the offer that follows. The pill shows **"Dictate anyway"** and **"Not now"** next to a
one-line warning that the audio goes to the active provider, that the free tier may be used by Google
(FR-11.6), and that automatic formatting can change capitals, spacing and punctuation. The offer:

- **covers exactly one dictation.** It is cleared as soon as it is used, and the next password field
  asks again. There is no "don't ask again" (FR-3.7).
- **expires after 20 seconds** and is cleared by any other activation, so ignoring it is a decline.
- **starts a hands-free session** when accepted, because the hold key was released long ago. That
  session skips the password check, since the user has already answered it.
- **types the text with `UnicodeTypingInserter`** instead of pasting it, so a password never reaches
  the clipboard or clipboard history (§4.7, FR-3.4).
- **keeps nothing.** The text is not stored as the last dictation and its audio is never retained for
  retry, so no recovery path can bring a password back. Only the character count is logged.
- **discards the text** rather than keeping it whenever it cannot be inserted — a move to a different
  window, or a failed `SendInput` — with a one-line notice that says so.

Rules implemented here:
- Tap threshold (FR-2.6)
- One session at a time (D6)
- Hands-free maximum length with a warning before it's reached, capped at 10 minutes (FR-9.5, §5)
- Auto-stop after silence, **off by default since M3**: the fixed RMS threshold ended sessions
  mid-sentence on a quiet microphone. It stays in Settings. Since M5, "voice" means about 10 dB above
  a **measured noise floor** (`NoiseFloorTracker`: the quietest chunk of the last 5 s, ignoring noise-gate
  zeros, with the threshold clamped between -60 and -30 dBFS)
- **Audio retained for retry (FR-9.1, M5):** every chunk is also kept in memory for the session. When
  the engine fails (while listening, after end of speech, or on retry), the audio is kept for
  **5 minutes**, and Retry appears on the pill and in the tray. A new dictation that reaches
  Processing replaces it. Retry sends it **as fast as the socket accepts** (T3), with a completion
  timeout of 20 s plus half the audio length, and inserts using the original target's focus rule
  (FR-9.2). Audio from a session that never passed the password check is never kept (D7)
- **Microphone lost mid-dictation (FR-7.4, M5):** if any sound was captured, listening stops and what
  was said is transcribed as a normal stop, with a one-line notice. Otherwise Error: microphone
  ("disconnected")
- **Silent input (FR-7.7, M5):** nothing above -66 dBFS for the first 4 s gives a one-time "No sound
  from X - is it muted?" notice. The same notice follows "No speech detected" when the whole
  recording was silent
- Lock or sleep stops listening without inserting (FR-11.2)

### 4.2 HotkeyService (Platform)
- A `WH_KEYBOARD_LL` hook on a dedicated thread with its own message loop. The callback must return in under ~1 s or Windows removes the hook, so it only enqueues events.
- **Hold-to-talk:** key down → `Activate(Hold)`, key up → `Stop`. Auto-repeat key-downs are ignored.
- **Toggle:** a separate configurable chord. A press starts, the next press stops.
- **Esc** is swallowed only while Listening, and maps to `Cancel` (FR-2.7).
- Keys are matched by **virtual-key code**, so any keyboard layout works (FR-2.4). The matched keys are swallowed (FR-2.1).
- **Stuck modifiers:** if the chord includes modifiers, the service sends synthetic modifier key-ups before inserting text (§6 edge case).
- **Default shortcuts** (changeable): hold `Right Ctrl`, toggle `Ctrl+Alt+Space`. A conflict check at configuration time tries a temporary `RegisterHotKey` for chords (FR-2.5).
- **Shortcut capture (built 2026-09-25, user request).** Settings → Shortcuts no longer offers a fixed list: each row has a "Change" button that arms the hook to report the very next key pressed (single-key rows: hold-to-talk, hold-to-translate) or the next non-modifier key plus whichever of Ctrl/Alt/Shift are held with it (chord rows: hands-free toggle, translate toggle, paste-last), so any key the keyboard can produce is assignable, not only the presets that used to populate the drop-downs. Escape cancels instead of being captured. While a capture is armed, and for 300 ms after it completes, the hook swallows every key event, so a key being reassigned - which may be the one already bound to the shortcut it is replacing - is never also acted on, and an OS auto-repeat or the key's own key-up cannot leak into the old binding. The existing conflict checks (two shortcuts sharing a key, the `RegisterHotKey` probe for FR-2.5) run unchanged at Save, against whatever was captured.
- **Capture prompt overflow (fixed 2026-09-28, user report).** The capture prompt used to be written into the "Change" button itself, which is a fixed 80px wide - a sentence like "Hold Ctrl/Alt/Shift, then press the key" overflowed onto the "Off" button next to it. The prompt now goes in the row's own display `TextBlock` (220px wide, plenty of room), and the button just says "Waiting..." while a capture is armed.
- **Modifier dropped from every captured combo, root cause found and fixed 2026-09-28 (user re-report: "hold Alt then press 1" still captured as bare "1", on both hold and chord rows).** Both `HandleCapture` paths were reading modifier state with `GetAsyncKeyState(VK_CONTROL/VK_MENU/VK_SHIFT)` at the moment the non-modifier key completed the capture - but the very same hook swallows the modifier's own key-down (returns 1, never calls `CallNextHookEx`) while a capture is armed, and Windows' key-state table (what `GetAsyncKeyState` reads) is fed by the normal input pipeline that swallow short-circuits, so it could still report the modifier as up. The runtime matchers (`ModifiersHeldFor`, `ModifiersMatch`) queried the same API and had the identical exposure, which is why toggle/hands-free chords carried it too. Fixed by dropping `GetAsyncKeyState` entirely: `HookCallback` now tracks `_ctrlDown`/`_altDown`/`_shiftDown` itself from the keydown/keyup events it already observes, updated unconditionally at the top of the callback before any capture/hold/chord branching, so every consumer (capture, `ModifiersHeldFor`, `ModifiersMatch`) reads state the hook itself is guaranteed to have seen rather than a possibly-stale external table.
- **Hold shortcuts can now carry modifiers too (built 2026-09-28, user request).** Originally the two hold rows only ever took one bare physical key (no Ctrl/Alt/Shift), which is what made an "Alt + 1 only registers Alt" report look like a capture bug when it was really someone trying that on a single-key row. The user then asked for the actual feature: hold-to-talk/translate with a modifier chord, e.g. hold Alt and press 1 to start, so the shortcut is not just tappable but genuinely holdable with a combination. `HotkeyHook.Hold`/`TranslateHold` are now `ToggleChord` (key + Ctrl/Alt/Shift), not a bare `int`, and `HotkeySettings` gained `HoldRequiresCtrl/Alt/Shift` and `TranslateHoldRequiresCtrl/Alt/Shift` (all false by default, which reproduces the old single-key behaviour exactly - no migration needed). The hook only starts a hold when the chord's key goes down **and** every modifier it requires is already held (`ModifiersHeldFor`, an "at least" check, not the toggle chords' exact `ModifiersMatch`: the hold key can itself be a modifier, like the default Right Ctrl, so requiring an exact "no other modifier down" would break matching its own press). Either the key or a required modifier being released ends the session; the key itself stays swallowed (auto-repeat included) until its own key-up even if a modifier ended the session first, so a finger still resting on it cannot leak into the app once the chord is no longer complete - but a modifier's own release is *not* swallowed (unlike the chord's key), so the app's idea of whether Alt is still down stays consistent with what actually happened. Alt specifically also gets the existing `SendMenuMaskKey` treatment (injected at match time, same trick `TryChord` already used) so a bare Alt-release-after-swallowed-key never opens a menu. Settings → Shortcuts capture for these two rows is a new `BeginCaptureHold`/`HandleCapture` mode, distinct from the plain single-key capture it replaced (now removed) and from `BeginCaptureChord` (which never accepts a bare modifier alone, so it could not do the old default-Right-Ctrl case): a modifier released before any other key is pressed completes the capture as that modifier alone; a non-modifier key pressed while modifiers are held completes it as a chord.

### 4.3 TargetTracker (Platform)
`Capture()` runs at activation and records the `TargetSnapshot`: foreground `HWND`, process id and
process name. These are plain Win32 calls, fast enough for the activation path. A foreground window
belonging to **our own process** (the tray menu takes focus, the floating control does not) produces
no snapshot, so a tray-started dictation locks nothing and lands wherever the caret is (FR-1.2).

`IsPasswordFieldAsync()` and `CheckAsync()` need the focused *element*, which means UI Automation.
`CheckAsync(snapshot)` returns:
- `PasswordField`: never insert (FR-3.7, D7)
- `SameWindow`: insert at the current caret, including a different field in that window (FR-3.6)
- `DifferentWindow`: don't insert → NotInserted, text kept (D5)
- `Unknown`: nothing was locked, so insert at the caret (FR-1.2)

Every UIA query runs on a worker thread behind a timeout: 400 ms on the activation path so the
indicator still meets NFR-1.1, 900 ms at insertion time where being sure matters more. A query that
outlives its timeout is abandoned rather than awaited, because a hung UIA call cannot be cancelled
(risk T7), and a timeout counts as "unknown", never as "safe". The UIA client is built in the
background at startup so the first password check does not spend its budget on COM initialization.

Editability detection is **advisory only**. It never blocks activation, because UIA support varies by app.

### 4.4 AudioCaptureService (Platform)
- **Opened on activation and closed on stop,** so the microphone is released while Ready (FR-7.8).
- **No clipping of the first syllable (NFR-1.3):** capture starts immediately and in parallel with the engine connection. All chunks go into the session's `AudioBuffer`, and the engine session drains it once connected. Network setup time never loses audio.
- **Format:** WASAPI shared capture → resample to **16 kHz, 16-bit, mono PCM**, emitted in 100 ms chunks.
- **Device choice:** the selected device, or "System default" (FR-7.1). A missing device falls back to the default with a notice, and the saved preference is kept (FR-7.3). Picked from the pill's right-click menu or in Settings; Settings keeps an unplugged saved device in its list as "(not connected)".
- **Disconnect (FR-7.4):** NAudio's `MMDeviceNotificationClient` (created once, off the activation path) watches for the active device being removed or leaving the Active state and stops the capture, which reports `MicrophoneException(Disconnected)` through `RecordingStopped`. An error raised by WASAPI itself is reported the same way.
- **Silence detection:** RMS per 100 ms chunk drives the pill meter, the silent-input notice (FR-7.7) and the noise-floor tracker (§4.1).
- **Settings test (FR-7.6):** records 4 s from the chosen device into memory, shows the level, reports the measured floor and peak in dB, and plays it back with `SoundPlayer` from a `MemoryStream`. Nothing is written to disk.
- **Privacy-denied access (FR-7.5):** the three `CapabilityAccessManager\ConsentStore\microphone` switches (device, apps, desktop apps) are read before opening, because a blocked desktop app may get silence instead of an error. `E_ACCESSDENIED` / `UnauthorizedAccessException` from WASAPI is mapped the same way. Both give `MicrophoneFailure.AccessDenied` → Error: microphone with "Windows is blocking the microphone", and an "Open privacy settings" button (`ms-settings:privacy-microphone`) on the pill and in Settings.

### 4.5 Speech engines (Core interfaces, Gemini implementations)

```csharp
public interface ISpeechEngine
{
    EngineCapabilities Capabilities { get; }            // FR-13.4
    Task<ISpeechSession> StartSessionAsync(EngineSessionOptions options, CancellationToken ct);
    Task<EngineTestReport> TestAsync(CancellationToken ct);             // FR-13.10
}

public interface ISpeechSession : IAsyncDisposable
{
    event Action<string>? InterimText;                  // not shown in the UI since 2026-09-18 (T10)
    event Action<string>? FinalSegment;                 // streaming insertion (FR-3.2)
    ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm16kMono, CancellationToken ct);
    Task<TranscriptResult> CompleteAsync(CancellationToken ct);  // end of speech → full final text
}

public sealed record EngineCapabilities(
    bool Streaming, bool InterimResults, bool SmartCleanup, bool LanguageHints,
    bool ManualTurnControl, TimeSpan MaxSessionLength);
```

- **Engine profile** (FR-13.1): `{ id, name, engineType, modelId (free text, FR-13.2), secretRef, languageHints, cleanupMode, isFallback }`. `engineType` selects the implementation from a registry, so adding a provider means adding a class. Nothing outside the engine assumes Gemini (D21).
- **Error mapping** (FR-6.1): every engine maps its failures to the typed `EngineError` values `Auth | Quota(resetHint) | Network | ModelNotFound | Transcription`. The controller never sees provider-specific codes.

**GeminiLiveEngine (default, D9).** The protocol follows Google's Live transcription docs as of September 2026; re-check field names at implementation time.
1. Connect to `wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent`. Send the key in the `x-goog-api-key` header rather than the URL query string, so it never appears in URLs or logs.
2. Send `setup` with:
   - `model: "models/<modelId>"`
   - `inputAudioTranscription.languageCodes`: `[]` for Auto, `["fa-IR"]` for Persian, `["en-US"]` for English (FR-4.1)
   - `mode`: `SMART` (Standard cleanup) or `VERBATIM` (Light cleanup) (FR-5.5)
   - `realtimeInputConfig.automaticActivityDetection.disabled = true`, for manual turn control that matches hold-to-talk and toggle
3. Send `activityStart`, then `realtimeInput.audio` chunks (`audio/pcm;rate=16000`, base64), then `activityEnd` on stop.
4. Ignore `serverContent.interimInputTranscription` in the app (T10), collect `serverContent.inputTranscription` as final segments. The session completes when the turn completes or a final arrives after `activityEnd`, with a timeout that maps to `Transcription`.

**GeminiFileEngine (built 2026-09-20).** Record-then-send using non-live `gemini-3.5-transcribe`: the second API type (FR-13.11) and the usual fallback profile (FR-13.8). The dictation is buffered in memory, wrapped in a RIFF header and posted once to `models/<modelId>:generateContent` as inline `audio/wav`; nothing touches the disk, and the request is capped at 9.6 MB (5 minutes) to stay under the API's 20 MB inline limit. Measured against `gemini-3.5-transcribe`: **1.8 s** after a 4.6 s clip and **2.7 s** after a 29.8 s one, against 0.34–0.69 s on the Live engine (T9), which is the price of record-then-send.

Three things the provider forced, all verified on 2026-09-20:
- The transcription models reject `systemInstruction` ("Developer instruction is not enabled for this model"), so the transcribe-don't-answer instruction (FR-4.3, FR-4.4, FR-13.6) is the first part of the user turn instead.
- They also reject `thinkingConfig` ("Thinking is not enabled for this model"), so the payload carries `temperature: 0` and nothing else: the model id is free text (FR-13.2), so the request has to be one every model accepts.
- They answer in `parts[].audioTranscription.text`, not `parts[].text`. The reader takes both, so a general model works as well.

Its capabilities are `Streaming=false`, `InterimResults=false`, `SmartCleanup=false` (this API has no SMART mode, and asking a general model to tidy the text is a different feature with its own risks) and `MaxSessionLength=5 minutes`. Settings disables the clean-up control with that reason when this profile is active, and the hands-free watcher already caps a session at the engine's limit (FR-13.4).

**Model directory (`GeminiCatalog`).** One `GET /v1beta/models` shared by both engines answers "does this key reach this model, and does the model take this API?". `TestAsync` uses it for **Test connection** (FR-13.10) — it separates a bad key from a model that is not live, and costs no session quota — and `IEngineCatalog.ListModelsAsync` uses it to fill the model drop-down with what the key can actually reach, filtered to `bidiGenerateContent` for Live profiles and `generateContent` for file profiles. The box stays editable, because an id that is not listed must still be typeable (FR-13.2).

**Fallback (FR-13.8).** Opt-in, one profile, chosen in Settings. When a dictation fails with `Quota`, `Network`, `Authentication` or `ModelNotFound` and the audio was kept, the controller sends that same recording to the fallback profile automatically; a `Transcription` failure does not fall back, because a second engine would make the same nothing of it. The switch is announced on the pill every time, with the fallback's name and its data-use note (FR-13.9), and a fallback that fails too stops there and leaves the audio for a manual Retry — never a loop between two profiles.

### 4.6 Text pipeline (Core)
An ordered list of `ITextStage`. Every stage is pure and unit-tested.

| # | Stage | Behavior | Req |
|---|---|---|---|
| 1 | *RefinementStage* (slot) | **Empty in the MVP.** This is where a second model plugs in if Q12 requires it, without redesign. | FR-13.5, R3 |
| 2 | `CharacterNormalizer` | **Deferred on 2026-09-17:** the user sees no wrong characters in practice, so it waits until one shows up. Arabic ي→ی and ك→ک; Persian (۰–۹) and Arabic-Indic (٠–٩) digits → Latin. Bidi control stripping already ships in `ControlCharacterFilter`. | FR-5.6, 5.13, 5.14 |
| 3 | `SuffixJoiner` | **Dropped on 2026-09-17: the user does not need it.** A Latin-script token followed by space or no separator and then a plural-family suffix (`ها، های، هایی، هام، هات، هاش، هامون، هاتون، هاشون، هاست`) would become token + ZWNJ + suffix, kept to that closed list to avoid false joins. | FR-5.12, D13 |
| 4 | `LineBreakPolicy` | Newlines and list bullets → single spaces, unless "allow line breaks" is on | D8 |
| 5 | `WhitespaceTidy` | Collapse double spaces, trim | FR-5.1 |
| 6 | `CaretContextSpacing` | **Dropped from M4 on 2026-09-17 for latency.** It needs a UIA `TextPattern` read at insertion time, which is heavier and much more variable per app than the `IsPassword` property, and FR-1.4 is a SHOULD. Until it returns, the user types their own leading space where they want one. | FR-1.4 |

**Formal Persian (D2):** a transcription model has no instruction channel, so Formal style **can't be produced by the engine alone**. The MVP keeps the setting but shows *"Formal style requires a refinement model — not configured"* until Q12 is decided. Colloquial is the default, so the core loop is unaffected. See §8.

### 4.7 InsertionService (Platform)
Strategies, chosen per insertion:

| Strategy | How | Used for |
|---|---|---|
| **ClipboardPaste** (default) | Save all clipboard formats → set text with the `ExcludeClipboardContentFromMonitorProcessing` and `CanIncludeInClipboardHistory = 0` formats → send Ctrl+V → restore the original clipboard after a short delay | Most apps. One edit, one undo, autocomplete-friendly (FR-1.5, 3.4, 3.5). |
| **UnicodeTyping** (`UnicodeTypingInserter`, built 2026-09-20) | `SendInput` with `KEYEVENTF_UNICODE`, one UTF-16 unit at a time, in batches of 100, after the shortcut modifiers are released | Confirmed password dictations, where the text never touches the clipboard (FR-3.7). Also the strategy for paste-unfriendly apps, still to be wired to the per-process table. Layout-independent (FR-3.3). |

- **Per-process overrides:** a small built-in table maps process name to strategy (for example, Remote Desktop client → UnicodeTyping). It can be extended in the settings JSON.
- **Never Enter or Tab:** the text pipeline strips line breaks, and the service rejects control characters (FR-1.6).
- **Streaming mode:** each `FinalSegment` passes through stages 2–6 and is inserted as an append. Inserted text is never deleted (FR-3.2).
- **Success check:** a best-effort UIA read-back where `TextPattern` or `ValuePattern` is available. If the paste visibly failed, the text goes to NotInserted with the recovery offer (FR-9.3).

### 4.8 UI (App)
- **FloatingControl:** a WPF window with `WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW`, so clicking it never takes focus (FR-2.8).
  - Shows state as icon **shape plus color plus motion**: a pulsing ring while Listening, a spinner while Processing (FR-6.2).
  - Start, stop, and cancel buttons.
  - No live preview of the words (removed 2026-09-18, T10). The level bars are the activity cue (FR-2.12). The bubble above the pill carries state details and warnings only, RTL-aware, in the Vazirmatn font.
  - Recovery flyout with only the offers that apply: "Dictate anyway" and "Not now" for a password field (FR-3.7), Copy and Paste-at-caret when a dictation could not
    be inserted (FR-9.3), Retry while kept audio exists (FR-9.1), and Open privacy settings when Windows
    blocks the microphone (FR-7.5).
  - Right-click menu: "Paste last dictation" and "Copy last dictation" while the latest dictation is in memory (T13), then the microphone list, then "Settings…" (added 2026-09-18 at the user's request).
    It is drawn inside the same window rather than in a popup, because a popup would take focus away
    from the target field (FR-7.1, FR-2.8). Opening Settings does take focus, which is intended.
  - A 440×436 window: a 200 px transparent band, the pill row, then another 200 px band. Flyouts hang in the band above the pill, and flip to the one below when the pill is near the top of its screen (T11). Both bands are click-through.
  - Remembers its position per monitor (FR-2.9).
  - Stays above full-screen windows (user report and request, 2026-09-21). FR-2.11 asks for hiding there, which was never built (M7 skipped, T15); instead `NoActivateWindow` raises the pill and the listening indicator to `HWND_TOPMOST` again, without activation, when the foreground window changes or moves, and every 2 s as a backstop, because a full-screen page (Fullscreen API) can sit above a topmost window. Exclusive-mode full-screen games cannot be drawn over.
- **ListeningIndicator:** a minimal non-activating pill, shown during Listening whenever FloatingControl is disabled or hidden (FR-6.3).
- **Tray:** state icon plus a menu with Start/Stop, Retry last dictation (only while kept audio exists, so it works with the floating control turned off), Copy last dictation (while one is in memory; copy, not paste, because opening the tray menu moves the focus, T13), Pause, Settings, Quit (FR-2.13).
- **Paste-last shortcut:** Ctrl+Alt+Z by default (Settings → Shortcuts, or Off) pastes the latest dictation at the caret again (T13).
- **Password fields:** no dialog. The session stops and the control shows "Dictation into password fields is blocked" (FR-3.7, D7).
- **Engine profiles in Settings (FR-13.1, built 2026-09-20):** one editor over a working copy of the profile list, so nothing reaches settings until Save. It holds the name, the API, the model (an editable drop-down filled by **Load list**), **Test connection**, and the two drop-downs that decide which profile dictates and which one it falls back to. The API key box above it stays one key per provider, shared by that provider's profiles (FR-13.7). Saving switches the profile for the next dictation, with no restart (FR-13.3).
- **Settings and Onboarding:** normal WPF windows in English (D10). Onboarding runs API key → validate → microphone test → shortcut → practice field → privacy disclosure (FR-10).
- **Sounds:** short start, stop, and error cues (FR-6.5).

### 4.9 Settings and secrets
- `settings.json` holds all FR-8 settings, engine profiles without keys, and floating-control positions. Settings apply live through `IOptionsMonitor`-style change events.
- `secrets.bin` holds DPAPI-encrypted API keys referenced by `secretRef` (FR-11.3, 13.7).
- Uninstall or "Remove all data" deletes the folder and the autostart task (FR-11.7).

---

## 5. Main sequence: hold-to-talk, after-stop insertion

```
Hotkey↓ ─► Controller.Activate
            ├─ TargetTracker.Capture (foreground window, Win32 only)
            ├─ Audio.Open ──► AudioBuffer (starts filling immediately, stays in this process)
            ├─ State = Listening (indicator ≤ 150 ms; NFR-1.1)
            └─ TargetTracker.IsPasswordField ─ yes ─► abort, nothing sent ─► BlockedPasswordField
                                             └ no ──► Engine.StartSession (connect + setup)
                                                        └─► drain buffer ──► stream
Hotkey↑ ─► hold < 300 ms? ─► Cancel silently (FR-2.6)
         └► Audio.Close ─► session.CompleteAsync ─► State = Processing
                             └─► final text ─► empty? ─► NoSpeech
                                            └► TextPipeline ─► keep text ─► TargetTracker.Check
                                                 ├─ same window / unknown ─► Insertion ─► Inserted
                                                 └─ moved / password / failed ─► NotInserted (copy/paste offer)
```

---

## 6. Requirement traceability (MUST items)

| Requirement | Component(s) |
|---|---|
| FR-1 target, spacing, no Enter | TargetTracker, InsertionService, ControlCharacterFilter (FR-1.4 spacing dropped, §4.6) |
| FR-2 hotkey, floating control, tray | HotkeyService, FloatingControl, Tray |
| FR-3 insertion modes, focus rule, password | InsertionService, TargetTracker, ControlCharacterFilter |
| FR-4 languages, never respond | GeminiLiveEngine setup (transcription-only model, no instruction channel) |
| FR-5 output rules | Engine cleanup mode + TextPipeline |
| FR-6 states | DictationController, FloatingControl, ListeningIndicator |
| FR-7 microphone | AudioCaptureService |
| FR-8 settings | Settings, SettingsWindow |
| FR-9 retry, recovery | DictationController (audio retention), recovery flyout |
| FR-10 onboarding | OnboardingWindow |
| FR-11 privacy | DPAPI store, logging redaction, lock/sleep handling, mic release |
| FR-12 lifecycle | App (single-instance mutex), autostart task |
| FR-13 engines | ISpeechEngine registry, profiles, capability gating |
| D18 elevated apps | Elevated process + scheduled-task autostart |

---

## 7. Implementation milestones

| # | Milestone | Done when |
|---|---|---|
| M0 | Solution skeleton | **Done (2026-09-15).** Builds on .NET 10; tray icon with state; single instance; JSON settings; DPAPI secret store; redacting file log; minimal settings window for API key and model id |
| M1 | **Walking skeleton** | **Done (2026-09-16), tested by the user with a real microphone.** Hold Right Ctrl, speak, release: WASAPI capture → GeminiLiveEngine → line-break/whitespace pipeline → clipboard paste with restore and history exclusion. Engine verified with sample audio (final text 514 ms after end of speech). |
| M2 | States and UI | **Done (2026-09-17).** Full state machine; floating control; listening indicator; sounds; cancel; tap threshold; toggle mode |
| M3 | Target safety | **Done (2026-09-17).** Focus rule (D5) with the target captured at activation and checked before insertion; busy (D6, from M2); password fields refused before any audio is sent, without the FR-3.7 override, which is deferred (D7, T8); control characters stripped and refused at insertion so nothing is ever submitted (D8); recovery flyout with Copy and Paste-at-caret. UIA password detection verified against a WPF `PasswordBox`: 18 ms cold, ~1 ms warm |
| M4 | Text pipeline | **Done (2026-09-17), reduced by the user to the settings.** Language, clean-up, Persian style and line breaks are now in Settings and apply to the next dictation without a restart. Formal style states plainly that it needs a refinement model. The normalizer (§4.6 stage 2), the ZWNJ suffix joiner (stage 3) and caret-context spacing (stage 6) were all dropped: the first two because the engine's output is good enough in practice, the third for latency |
| M5 | Microphone and errors | **Built 2026-09-18, waiting on the user's test.** Device list and pick from a right-click on the floating pill (which also opens Settings, by the user's request), saved preference, fallback to the default without clearing the preference, and an announcement when the live device changes. Disconnect mid-session transcribes what was captured (FR-7.4). Privacy-denied detection with a way to the Windows page (FR-7.5). Microphone picker, level meter, test recording and playback in Settings (FR-7.6). Silent-input notice and a measured noise floor for auto-stop (FR-7.7). Retry of kept audio from the pill and the tray (FR-9.1/9.2, T3 resolved). 8 controller tests and 7 noise-floor tests. The multi-device and unplug behaviour needs a machine with several microphones |
| M6 | Engine profiles | **Built 2026-09-20, waiting on the user's test.** Settings → Engine profiles: add, rename, delete, pick the API (Live or record-then-send), pick the model from a live list or type an id, **Test connection**, choose which profile dictation uses and which one it falls back to. `GeminiFileEngine` on `gemini-3.5-transcribe` (§4.5), capability gating on the clean-up control and the session limit, and automatic fallback with a visible announcement. 3 controller tests for the fallback; the file engine is verified against the real API, not mocked |
| M7 | System integration | **Skipped by the user on 2026-09-20 ("we do not need them"), except autostart.** Not built: elevation, lock and sleep, full-screen hiding, onboarding, streaming insertion mode. **Built the same day, at the user's request:** launch at sign-in through the per-user Run key (§2, FR-12.1). The manifest stays `asInvoker`. See T15 for the requirements this leaves open |
| M8 | Acceptance | Run [evaluation-corpus.md](evaluation-corpus.md) and [app-compatibility-test-plan.md](app-compatibility-test-plan.md); decide Q12 |

---

## 8. Technical risks and open items

| # | Item | Plan |
|---|---|---|
| T1 | Formal Persian needs a refinement model (§4.6) | Setting shown as "requires refinement model" until Q12. If wanted earlier, implement option (c) in M4: a Gemini text model applied only when Formal is on. |
| T2 | Live API protocol details | **Verified 2026-09-16** with `tools/LiveTranscribe.ConnectionCheck`: header auth, `setup` with SMART mode and manual activity, `setupComplete`, `voiceActivity`, `interimInputTranscription`, `inputTranscription`, `generationComplete`. Final text arrived ~0.1 s after `activityEnd` for a 4.6 s English clip. |
| T3 | Retry of retained audio: can the Live API accept faster-than-real-time audio? | **Resolved 2026-09-18: yes.** `ConnectionCheck --engine --fast` sent 4.6 s and 8.9 s clips in about 100 ms. The transcripts were identical to real-time sends, and the final text arrived 1.3 s and 2.3 s after end of speech (the server works at roughly 4× real time). Retry uses the Live engine; `GeminiFileEngine` is not needed for it. |
| T4 | Clipboard restore with delayed-rendering formats (e.g. Office) | Restore only formats that can be materialized; test in Word/Excel during M1 and M3. |
| T9 | Latency is the user's first priority (2026-09-17) | Two fixed costs removed: the completion grace after the final transcript now ends after one 60 ms quiet window instead of always waiting 250 ms, and the clipboard restore no longer blocks the result, so the app is Ready about 350 ms sooner. The clipboard snapshot limit stays at 64 MB by the user's choice. Text pipeline stages cost microseconds and are not a latency factor. |
| T5 | Per-dictation connection setup adds latency | Audio is buffered, so nothing is lost. Measure in M1; if final latency misses NFR-1.2, pre-connect when the hotkey is pressed (already parallel) or keep a warm session while Ready. |
| T6 | Running elevated: files and paths the app opens are admin-owned | Keep all data in the user's `%APPDATA%`; don't create files elsewhere. |
| T8 | FR-3.7 password override not implemented (§4.1) | **Resolved 2026-09-20: built, by the user's decision ("ساخته شود").** The build and FR-3.7 agree again; see §4.1 for the flow. 3 controller tests cover it: confirming types the text and keeps nothing, a failed insertion discards it, and the next password field asks again. |
| T10 | Preview bubble shows only sometimes, and late | **Resolved 2026-09-18: live preview removed, at the user's request ("fix it if possible, otherwise remove it").** The delay is on Gemini's side, not in the app. The same 29.8 s clip, sent in real time with the same code (`ConnectionCheck --engine`), got 62 interim updates with the first at 0.8 s on one run, and 3 updates at 4.2 s, 12.1 s and 28.1 s on the next. The user's own 59 s dictation got its first update at 27.5 s. Final-text latency was fine throughout (340–690 ms after end of speech, T9 confirmed). The client cannot make the server send interims, so the preview was cut: the controller no longer forwards `InterimText` and the log no longer counts preview updates. The engine still exposes the event (ConnectionCheck uses it). FR-2.12 is a SHOULD for "a preview *or* activity cue", and the level bars remain the activity cue. Revisit if a later model streams interims reliably. |
| T11 | Flyouts can go off-screen near the top of a monitor | **Fixed 2026-09-19.** The window is now 440×436 with the pill in its own row between two 200 px transparent bands, instead of 440×200 with the pill on the bottom edge. The bubble, the pill menu and the recovery offers move into the band below the pill when the pill sits closer to the top of its screen (WinForms `Screen.FromPoint` on the anchor, so it is per monitor) than the band above needs; their order mirrors, so the same flyout stays nearest the pill. The anchor is still the pill's bottom-centre, so a saved position keeps the pill exactly where it was (verified: anchor 1616,885 → window at 1396,649). Clicks still pass through both bands (verified with `WindowFromPoint` above, below and on the pill). |
| T13 | A dictation into a window with no text field is lost silently | Reported by the user on 2026-09-18. The paste "succeeds" (Ctrl+V is sent), so no error and no recovery offer appear, and the text is gone from view. **Built 2026-09-18:** the latest dictation (already kept in memory, D17) can be recovered at any time until the next one: a paste-last shortcut (Ctrl+Alt+Z by default, Settings → Shortcuts, or Off; not Alt+Shift, which switches the keyboard layout), "Paste last dictation" and "Copy last dictation" in the pill's right-click menu, and "Copy last dictation" in the tray menu. **Not built:** detecting a missing text field before pasting (UIA "is the focused element editable?"). Many apps (Electron, terminals, games, canvas editors) report no editable element and still accept a paste, so a false "no field" would block working insertions. If the user wants it, the safe form is: paste as today, but when UIA reports no editable focus, also keep the Copy/Paste offer on the pill for a few seconds. **Built 2026-09-21 at the user's request, in that safe form:** the trackers report `TargetCheck.NoTextField` only when sure (Windows: UIA answered and the focused element is not a password, Edit, Document or ComboBox, has no writable Value pattern and no Text pattern, or nothing has focus; Android: the accessibility service is on and no node has input focus). A timeout stays Unknown. The controller still tries the insertion (on Windows the paste is sent, because some apps take one without exposing a field), then puts the text on the clipboard and leaves it there, returns to Ready and raises `DictationNotice.CopiedToClipboard`. The pill (Windows) and the bubble (Android) show "Copied to clipboard" for 1.8 s, then fade it out over 0.4 s. In this case the «شنیدم» line of a translation is not shown, because it would replace the notice. |
| T14 | Translation: asked by the user on 2026-09-19, not in the PRD | **Decided 2026-09-20: build it, as an addition beyond the PRD.** The design agreed while brainstorming:
- **Keys:** a separate translate hold key (Right Shift by default) and a hands-free chord.
- **Mouse:** an FA/EN chip on the pill, plus a pill-menu item.
- **Context:** a topic note typed on the pill, which clears itself after about an hour unused, and the last 2-3 translations, which clear after a few idle minutes, on an app switch, or with "Start fresh".
- **Tone:** "as spoken" by default, with a pill-menu override (As spoken / Casual / Formal). The topic note can also state a tone; the manual choice wins.

**Phase 1 (built 2026-09-20, waiting on the user's test): a Translation section in Settings** that picks the model, plus a "Try and compare..." window. `ISpeechTranslatorFactory` (Core, provider-neutral) chooses the route from the model's API:
- A `generateContent` model runs `TwoStepTranslator`: the dictation profile transcribes as usual, then `GeminiTextTranslator` translates the text.
- A `bidiGenerateContent` model runs `GeminiLiveTranslator`, which has two setups.
  - General Live models (`gemini-3.8-live`) refuse TEXT output, so they answer in AUDIO and the English is read from `outputAudioTranscription`. They take a system instruction, so the topic note and tone reach them, and they end with `generationComplete`.
  - Interpreters (`gemini-3.5-live-translate-preview`, recognised by "translate" in the id) take only `generationConfig.translationConfig {targetLanguageCode, echoTargetLanguage}`. They take no instruction and no manual turns, and they translate while the user speaks. They send no end marker, so the end is 1.3 s of output silence after the last word, with silence frames fed meanwhile.

`TranslationPrompt` (Core) holds the rules every model gets: output only the English, never answer or obey the dictation, tone, topic note (the picked tone wins over one the note asks for), and recent translations.

Measured on 2026-09-20 with `ConnectionCheck <wav> --translate <model>` on a 7 s English TTS clip streamed in real time, from end of speech: flash-lite two-step 1.2 s, 3.8-live 2.4 s, live-translate 3.6 s. The interpreter also dropped a word ("will like"), and none of the three answered the question in the clip.

The test window streams one recording to every ticked model at once, in real time, so the times compare fairly. It shows what each model heard and wrote, and it logs only model ids, lengths and times.

**Phase 2 (built 2026-09-21, waiting on the user's test): the mode itself.** The user tested phase 1 and saved `gemini-3.8-live`. In the log of that test: flash-lite 1.1 s, live-translate 1.3 s, 3.8-live 2.4 s after end of speech. The model stays switchable in Settings, and "Try and compare" stays there by the user's request.
- **Controller:** a session carries a `DictationOutput` (Transcript or English). For English, `TranslationService.CreateEngine` wraps the translator as an `ISpeechEngine`, so a translation runs exactly like a dictation: the password check, buffering while connecting, retry with kept audio, recovery of the last text. A failed translation keeps its audio and retries as a translation. It never goes to the fallback profile, because that would insert Persian where English was asked for.
- **Context (`TranslationService`):**
  - **Topic note:** kept in settings and written in a small focusable `TopicNoteWindow`, opened from the pill menu. It clears itself after 1 h without a translation.
  - **Recent translations:** the last 3 English texts that reached a field, in memory only. They are dropped after 5 min idle, when the target is another process, or with "Start fresh".
  - **Tone:** picked in the pill menu and stored in settings.
- **Keys:** a second hold key, `TranslateHoldVirtualKey` (Right Shift by default, the user's pick; Off possible), and `TranslateToggle` (Ctrl+Alt+T). `HotkeyHook` allows one hold at a time and ends a session only on the key that started it.
- **Pill:** an FA/EN chip sets what a click produces (`Ui.PillOutput`). It shows EN in blue while a translation runs, and a dot when a topic note is on. The right-click menu adds these rows at the top: "Output: Persian (speech to text)" / "Output: English (translate)", the current one ticked, which switch the same way as the chip (added 2026-09-21, because the user did not find the chip to get back to Persian); then "Translate to English (hands-free)", "Topic note…", Tone (As spoken / Casual / Formal), and "Start fresh" when sentences are remembered.

**Seeing the Persian (built 2026-09-21, the user picked options 1 and 2 of 3):** a mistranslation could not be told from a mishearing. `TranscriptResult.Heard` now carries `TranslationResult.Transcript` through the controller. For `3.8-live` that is its `inputTranscription`, a side transcription and not necessarily what the model understood.
- **Bubble:** after an inserted translation, the pill bubble shows «شنیدم: …» for 7 s, held while hovered.
- **Menu:** `LastHeard` is kept in memory next to `LastDictation` and replaced with it, and the pill menu offers "Paste the Persian" / "Copy the Persian".
- **Not built:** option 3, "check before inserting", is on hold unless the first two prove not enough.

**Android parity for topic note and tone (built 2026-09-25, user request).** T16 had left these out of A1. Both use the same `TranslationService` as Windows (Core, provider-neutral), so only the phone-side UI was missing:
- **Topic note, first version:** a "Topic note…" row in the bubble's long-press menu opened `TopicNoteActivity`, a small `Theme.Material.Light.Dialog` activity with one `EditText` - the phone counterpart of `TopicNoteWindow`. **Replaced the same day**, before the user had finished testing it: launching a real Activity is a task switch, which felt like "leaving the app" - the app the user was dictating into went to the background, however briefly, which is exactly what the bubble design (§9) exists to avoid.
- **Topic note, as built:** "Topic note…" (a dot suffix when one is set) now opens a small card added as its *own* window through the same `IWindowManager` the bubble root uses, same `AccessibilityOverlay` type, differing only in one flag: no `NotFocusable`, so this one window (and only this one) can take input focus and show the keyboard (`InputMethodManager.ShowSoftInput`), while the app behind it is never backgrounded and never loses its own state. Save/Clear/Cancel close it and remove the window; whatever had focus before regains it.
- **Note tap bug, then dictation into the note (found and fixed 2026-09-28, user report).** `BubbleOverlay.OnTap()` closed the note on *any* tap while it was open, before reaching the code that starts recording - so tapping the bubble to speak into the note just closed the card instead. Two fixes were weighed: (A) stop closing the note on tap and rely on the existing insert-into-focused-field code, since the note's `EditText` already holds real input focus - cheap, but unverified whether Android reports focus for the app's own overlay window the way it does for a normal app's field; (B) a dedicated mic wired directly into the note, bypassing the accessibility path entirely - guaranteed, more plumbing. **The user picked B.** `Overlay/NoteRecorder.cs` is a small, self-contained mic session independent of `DictationController`: it takes the active profile's engine and key, starts `AndroidAudioCapture` and an `ISpeechSession` directly, and hands the final `TranscriptResult` text back to the caller instead of running it through `ITextInserter`. The note card gained its own 🎤 button (in `BubbleOverlay`) that starts/stops this session and splices the recognized text in at the `EditText`'s own selection (`AppendToNote`, the same "add a leading space only when needed" idea as `AccessibilityTextInserter.TrySetText`). `RecordingService.Start/Stop` are still called around it (same reason the bubble's own recording does: Android silences the microphone of an app with nothing in the foreground). The note's own mic defers to the bubble's dictation if that is already running (checks `Dictation.State`), and `CloseTopicNote()` cancels any in-flight note recording; `NoteRecorder.Cancel()` also guards the case where the note is closed while the engine connection is still being awaited, so that connection cannot land afterwards and leave the microphone open with nothing listening. Typing still works exactly as before; the mic is additive.
- **Tone:** the long-press menu grew a second row, under the first, with "As spoken" / "Casual" / "Formal" chips - shown only while English output is selected, since tone means nothing for a plain Persian dictation. Picking one calls `TranslationService.SetTone` directly, same as the Windows pill menu's Tone submenu.

**Earlier findings:**

 **The stored key can do it; the app does not.** Measured on 2026-09-19 with `ConnectionCheck --models` / `--generate` / `--describe`. The key reaches the whole `generativelanguage` catalogue, not only the transcription models: a one-sentence English→Persian translation took **806 ms on `gemini-3.5-flash-lite`** and 3375 ms on `gemini-3.8-flash`, both good quality. There is also **`gemini-3.5-live-translate-preview`** (`bidiGenerateContent`, 16k input / 32k output, version 3.5-live-translate-06-2026), which would translate inside the Live session instead of as a second step; its protocol and quality are untested. `gemini-3.5-transcribe` (non-live, `generateContent`, 98k input, `thinking: true`) is confirmed present for the M6 file engine (FR-13.11). **Decision needed before building anything:** translation is a new requirement, so it needs a PRD amendment or a D-item. Two shapes: (a) a second pass over the final text with flash-lite, about +0.8 s per dictation, reusing whatever machinery Formal Persian (T1) and technical terms (T12) need anyway; (b) an engine profile on the live-translate model, one step and no extra latency, but a different engine to write and verify. |
| T15 | M7 skipped by the user (2026-09-20) | **Decision: not built.** The PRD still lists these, so the build now differs from it on: **FR-11.2 MUST** (stop listening on lock, sleep or user switch; today a dictation running at that moment keeps recording), **FR-10.1/10.2 MUST** (guided first run and the privacy disclosure before the first dictation; today the key goes into Settings directly and the free-tier note sits at the bottom of Settings), ~~**FR-12.1** (launch at sign-in)~~ **built on 2026-09-20 after the user asked for it separately** - the per-user Run key, not the elevated task (§2), **FR-2.11 SHOULD** (hide in full-screen apps), **FR-3.2 SHOULD** (streaming insertion), and **D18** (no elevation: dictation into a window running as administrator fails, because UIPI blocks input from a normal process). Revisit any of them if the need appears; FR-11.2 is the smallest and the only privacy one. |
| T12 | Technical terms sometimes in Latin, sometimes in Persian script | Seen by the user on 2026-09-18 with Language already on Auto. The app does no word-level processing, so this is entirely the engine. Language and clean-up settings don't fix it. The user will collect the words that come out wrong. A short, repeated list → deterministic replacement rules in the pipeline (custom vocabulary, currently out of MVP by D11). A scattered list → refinement model (Q12, R3). |
| T16 | Android version: asked by the user on 2026-09-21, not in the PRD | **Decided 2026-09-21: build it, as an addition beyond the PRD** (the PRD is Windows-only). Shape: the floating-bubble design agreed while brainstorming (§9), on an accessibility service, for personal sideloaded use; Play Store policy does not apply. Stack: .NET for Android, chosen so `LiveTranscribe.Core` and `Engines.Gemini` are reused unchanged (the whole `DictationController`, translation and retry included). Open risks, to be settled on the user's phone: (1) whether Android lets the accessibility service record: a microphone foreground service is started on tap, and if the microphone is still silenced the silence notice says so; (2) `ACTION_SET_TEXT` support per app (fallback: paste, which leaves the text on the clipboard because Android does not let a background app read it back to restore it); (3) sideload setup on Android 13+ ("Allow restricted settings"). Not in A1: an engine-profile UI (one Live profile, model editable), hold-to-talk. Topic note and tone, both left out of A1, were added 2026-09-25 (§8 T14). |
| T7 | UIA can be slow or hang in some apps | **Implemented in M3** (§4.3): worker thread, 400 ms on activation and 900 ms at insertion, abandoned on timeout, treated as unknown. Measured 2–39 ms against Chrome and ~1 ms against WPF. Password detection in Chromium, Electron and Office still has to be confirmed in the M8 compatibility pass. |
| T17 | Android update from 0.4 to 0.5 refused to install (2026-09-25) | The Android project pinned no debug keystore, so a build signed with whatever debug key existed at `%USERPROFILE%\.android\debug.keystore` on the machine that built it - auto-generated, and different on every machine or fresh session unless one already exists there. 0.4 and 0.5 were most likely built with two different debug keys, which Android reports as an "app not installed" signature conflict rather than an update. The user worked around it by uninstalling 0.4 first, losing its settings (the stored key, in particular) as a result. **Fixed 2026-09-28** (user go-ahead): generated one debug keystore (`keytool -genkeypair`, alias `androiddebugkey`, same distinguished name and 30-year validity as the auto-generated one), committed it at `signing/debug.keystore`, and pointed `LiveTranscribe.Android.csproj` at it with `AndroidSigningKeyStore`/`AndroidSigningKeyAlias`/`AndroidSigningStorePass`/`AndroidSigningKeyPass`. Every build from any machine or session now signs identically. One more uninstall+reinstall is still needed on the phone to move onto this key, since whatever is installed now carries a different (per-machine) debug key; every build after that updates normally. **Public repository:** the keystore and its passwords are not included. The csproj imports an optional, Git-ignored `signing/signing.props` (template: `signing/signing.props.example`) that points at your own keystore; without it, builds fall back to the machine's auto-generated debug key. |

---

## 9. Android (T16)

`src/LiveTranscribe.Android` (net10.0-android, API 26+, arm / arm64 / x64, v2+v3 signed, namespace `LiveTranscribe.Droid`) sits outside `LiveTranscribe.sln`, so the Windows build does not need the Android workload. It references Core and Engines.Gemini and adds only platform services:

| Core abstraction | Android implementation |
|---|---|
| `IAudioCapture` | `AndroidAudioCapture`: `AudioRecord`, `VOICE_RECOGNITION`, 16 kHz mono PCM16 in 100 ms chunks, in memory only |
| `ISecretStore` | `KeystoreSecretStore`: AES-GCM with a non-exportable Android Keystore key; `allowBackup=false` |
| `ITargetTracker` | `AccessibilityTargetTracker`: the focused node's package stands in for the process; queries run off-thread behind a timeout, and a timeout means unknown |
| `ITextInserter` | `AccessibilityTextInserter`: splices the text in at the selection with `ACTION_SET_TEXT` and puts the caret after it with `ACTION_SET_SELECTION`. It adds a space when the dictation would run into the previous word. Fallback: clipboard + `ACTION_PASTE`. It refuses password nodes |
| `IClipboardWriter` | `AndroidClipboardWriter` |

- **The bubble** (`Overlay/BubbleOverlay`): a `TYPE_ACCESSIBILITY_OVERLAY` window that never takes focus.
  - **Position:** docked at the screen edge just above the keyboard, using the IME window's bounds from the accessibility window list. With no keyboard showing, it sits a quarter of the way up the screen.
  - **Moving it:** drag it anywhere. On release it snaps to the nearer edge. The side and the offset above the keyboard are remembered.
  - **Controls:** tap to start. While listening, the bubble gives way to a row that runs inwards from the edge: ✓ (insert) where the thumb already is, then the level bars as the recording cue (blue when translating), then ✕ (cancel). The red microphone is gone (user's decision, 2026-09-21, modelled on Wispr Flow).
  - **Long press:** opens options that run inwards from the edge: فارسی / English (the saved `PillOutput`), "Topic note…", plus Retry, Paste last and Paste Persian when they apply, in one row; a second row of tone chips (As spoken / Casual / Formal) appears under it while English is selected (built 2026-09-25, §8 T14). Slide onto an option and let go to pick it.
  - **Messages:** a line above the button shows errors, notices, and «شنیدم: …» after a translation.
- **Visibility (changed 2026-09-21 after the first phone test, by the user's decision):** the bubble shows in three cases:
  - the focused node is editable and not a password, with or without the keyboard;
  - the keyboard is up and the focused node is anything but a password field, or no node is reported at all. This is Wispr Flow's rule, adopted because the Claude and ChatGPT apps did not report their chat box as a focused editable field;
  - the keyboard has been hidden, but input focus is still on the node that had it while the keyboard was up. The user can hide the keyboard and still dictate. The bubble goes when focus leaves that field.

  It also stays while a dictation or its outcome still needs the user. A focus lookup that passes 400 ms is unknown: the bubble keeps its current state, a late answer (up to 3 s) is still applied, and the look is retried up to three times. The inserter does its own password check. The service listens for focus, window, selection and click events, plus content changes (looked at 250 ms after the first event of a burst). Each change in the outcome is logged as package, focused, editable, password, keyboard, bubble and lookup time, never text.
- **Microphone:** `RecordingService` is a `microphone` foreground service. It shows a "LiveTranscribe is listening" notification and runs only while listening.
- **Settings screen** (`MainActivity`) has:
  - the accessibility service status, with a shortcut to its settings;
  - the microphone permission;
  - the API key, write-only, plus Test connection;
  - the spoken language, Auto or Persian only (added 2026-10-01 at the user's request), saved on tap into the same `Dictation.Language` Windows uses. English only stays a Windows-only choice. Both platforms show the same caption from `LanguageModes.Describe`. Auto sends no `languageCodes` to Live; Persian only sends `fa-IR`;
  - the dictation and translation model ids;
  - a test field.
- **Build:** `dotnet build src/LiveTranscribe.Android -c Debug -p:AndroidSdkDirectory=<sdk>`. The APK embeds its assemblies, so it can be copied to the phone and installed.
