# LiveTranscribe

This is a Windows voice dictation app in the style of Wispr Flow. It uses C#/.NET 10 and WPF, with Gemini Live as the engine.

## Where things are decided

- `docs/product-requirements.md`: the PRD (v1.1). It is **frozen**, so don't edit it. When the build differs from it, record the difference as a T-item in the architecture doc.
- `docs/architecture.md`: the living design. §7 holds the milestone status. §8 holds risks and open items (T1…T12). Update it with every milestone change.
- `docs/next-steps.md`: the handoff between sessions, with the current state and what to do first. **Read it at the start of every session.**
- `docs/evaluation-corpus.md` and `docs/app-compatibility-test-plan.md`: inputs for M8.

## Keep context in files, not in chat

The user may run `/clear` at any time, and nothing is saved when they do. Write things down as they happen:

- **User decisions** go into `docs/architecture.md` right away: the affected section, plus a T-item in §8 if the decision defers or changes a requirement.
- **After every commit**, update `docs/next-steps.md` so it matches reality: what is done, what is in progress, what comes next, and what is waiting on the user. Commit it with the change or right after it.
- **Something observed but not yet acted on** (a bug report, a log finding, an open question) goes into `next-steps.md` or §8 before the reply ends.
- **Personal preferences** (not project facts) go into Claude's memory, not the repo.
- **End every step's reply** with one line saying what was saved and where.

## Build and run

```
dotnet build -c Debug --nologo -v q
dotnet test tests\LiveTranscribe.Core.Tests
```

- Stop the running app before building (`Get-Process LiveTranscribe | Stop-Process -Force`), because the build can't overwrite a running exe.
- The executable is `src/LiveTranscribe.App/bin/Debug/net10.0-windows/LiveTranscribe.exe`. Relaunch it after building so the user can test.
- Settings: `%APPDATA%\LiveTranscribe\settings.json` (versioned; migrations in `JsonSettingsStore.Migrate`). The logs are in the same folder.

## Git

- Branch `main`. Commit and push after each completed step.
- Use `gh` for GitHub. Never read the stored git credentials.

## Hard rules

- The API key is entered by the user in Settings, never in chat. It is DPAPI-encrypted and never logged or printed.
- Never log dictated text, transcripts or audio. Diagnostics may log counts, lengths and timings only.
- Audio is never written to disk.
- The floating control must never take focus. Draw menus and flyouts inside `FloatingControlWindow`, not in WPF Popups or ContextMenus.
- UI Automation calls always run off-thread behind a timeout. A timeout counts as "unknown", never as "safe".
