# LiveTranscribe

System-wide voice dictation for Windows, with an experimental Android companion. Hold a key, speak Persian, English or a mix of both, and the text appears in whatever field has focus. It is built on the Gemini Live API.

> **Status:** a personal-use prototype that I use every day. Milestones M0 to M4 are done. **M5 (microphone and error handling) and M6 (engine profiles) are built but still waiting for my own testing.** The Android app is experimental. See [Status](#status).

## Why

Most dictation tools handle mixed Persian and English technical speech badly. They translate or transliterate English terms, use Arabic letter forms, or drop the zero-width non-joiner between a Persian suffix and an English word. LiveTranscribe keeps technical terms in English, writes standard Persian, and inserts the result into any app without taking focus away from it.

## Features

Windows app:

- **Hold-to-talk** (default: Right Ctrl) and a **hands-free toggle** (default: Ctrl+Alt+Space). Every shortcut can be reassigned, including chords with Ctrl, Alt or Shift.
- **Insertion into any focused field.** It pastes through the clipboard, then restores the previous clipboard content and keeps the dictation out of clipboard history. Unicode typing is used where pasting is not safe.
- **Target safety.** The target window is captured when dictation starts and checked again before insertion. Password fields are refused before any audio is sent. A one-time override exists, and it types the text instead of pasting it.
- **Speech-to-English translation mode** (hold Right Shift, or Ctrl+Alt+T hands-free), with an optional topic note that keeps names and terms consistent.
- **A floating "pill" control that never takes focus**, plus a tray icon, a listening indicator and sound cues.
- **Microphone handling:** device selection with fallback to the default, mid-dictation disconnect handling, a silent-input notice, and a level meter with a test recording in Settings.
- **Retry after errors.** If the network or the engine fails, the audio is kept in memory for five minutes and can be sent again.
- **Engine profiles:** Gemini Live streaming over WebSocket, or record-then-send, with a connection test and automatic fallback to a second profile.
- Paste the last dictation again (Ctrl+Alt+Z), and launch at Windows sign-in.

Android app (experimental): an accessibility-service bubble that dictates into the focused text field, with a topic-note card and a choice of spoken language.

## Privacy by design

- The API key is entered in Settings, encrypted with Windows DPAPI for the current user, and never logged.
- Dictated text, transcripts and audio are never logged, and audio is never written to disk. Diagnostics record only counts, lengths and timings.
- Audio is sent to Google's Gemini API. On Google's free tier, Google may use that data under its own terms.

## Architecture

```text
┌──────────────── LiveTranscribe.App (WPF, composition root) ────────────────┐
│  tray icon · floating pill · listening indicator · Settings windows        │
└──────────────┬───────────────────────────────────────────┬─────────────────┘
               │                                           │
 LiveTranscribe.Platform.Windows              LiveTranscribe.Engines.Gemini
  · low-level keyboard hook (hotkeys)          · GeminiLiveEngine (WebSocket)
  · WASAPI capture (NAudio)                    · GeminiFileEngine (record-then-send)
  · UI Automation focus/password check (FlaUI) · translators
  · clipboard paste / Unicode typing
  · DPAPI secret store · autostart
               │                                           │
               └─────────────────────┬─────────────────────┘
                                     ▼
                 LiveTranscribe.Core (net10.0, no Windows dependencies)
                  · DictationController state machine
                  · text pipeline · settings · engine abstractions
```

One dictation, end to end:

```text
hotkey down → capture target (UI Automation) → WASAPI audio, 16 kHz mono PCM
→ Gemini Live over WebSocket → final transcript → text pipeline
→ focus and password check → paste, then restore the clipboard
```

Dependencies point one way: App → Platform.Windows and Engines.Gemini → Core. Core has no Windows dependencies and is unit-tested with xUnit. The Android app reuses Core and Engines.Gemini.

Design documents:

- [`docs/product-requirements.md`](docs/product-requirements.md): the product requirements (v1.1, frozen).
- [`docs/architecture.md`](docs/architecture.md): the living design, with milestone status in §7 and open risks in §8.
- [`docs/next-steps.md`](docs/next-steps.md): the handoff between development sessions.

## Getting started

Requirements:

- Windows 10 or 11, x64
- .NET 10 SDK (the version is pinned in `global.json`)
- A Gemini API key from Google AI Studio

```powershell
git clone https://github.com/FaridEng/live-transcribe.git
cd live-transcribe
dotnet build -c Debug
dotnet test tests/LiveTranscribe.Core.Tests
.\src\LiveTranscribe.App\bin\Debug\net10.0-windows\LiveTranscribe.exe
```

Then right-click the pill, open **Settings**, paste your API key, choose a model and run **Test connection**. Hold Right Ctrl, speak, and release.

Settings and logs live in `%APPDATA%\LiveTranscribe\`.

Android (experimental) needs the .NET Android workload and an Android SDK. No signing key is included in the repository, so sign with your own keystore:

```powershell
dotnet build src/LiveTranscribe.Android -c Debug -p:AndroidSdkDirectory=<path-to-android-sdk>
```

To sign every build with the same key, so new builds install as updates, copy [`signing/signing.props.example`](signing/signing.props.example) to `signing/signing.props` and point it at your own keystore. Both files stay out of Git. Without it, builds use your machine's auto-generated debug key.

Known limits:

- It cannot dictate into windows that run as administrator. This is by choice: the app runs without elevation.
- Exclusive full-screen games are out of reach.

## Status

| Milestone | Scope | Status |
|---|---|---|
| M0 | Solution skeleton | Done (2026-09-15) |
| M1 | Walking skeleton: hold, speak, insert | Done (2026-09-16), tested with a real microphone |
| M2 | State machine and UI | Done (2026-09-17) |
| M3 | Target safety | Done (2026-09-17) |
| M4 | Text pipeline | Done in reduced scope (2026-09-17) |
| M5 | Microphone and errors | **Built 2026-09-18, waiting for user testing** |
| M6 | Engine profiles | **Built 2026-09-20, waiting for user testing** |
| M7 | System integration | Mostly skipped by choice; launch at sign-in is built |
| M8 | Acceptance: evaluation corpus and app compatibility | Not started |

Android: experimental builds; several features are built but not yet tested on a device.

## How this was built

I wrote the product requirements, designed the architecture and made the design decisions recorded in `docs/`. The code was written by AI coding agents, mainly Claude Code, working under my direction from those documents. [`CLAUDE.md`](CLAUDE.md) holds the working rules the agent followed. I test the app myself and use it every day.


## License

MIT. See [LICENSE](LICENSE).

Dependencies keep their own licenses; NAudio and FlaUI are MIT.
