# App Compatibility Test Plan

Supports PRD v1.0 §7.2 (reliability), §7.4 (compatibility tiers), §6 (edge cases) and §10 task 3.

This plan is implementation-neutral: it describes **what must be observed** in each target app, not how insertion works. It runs against the first working build, and again before every release.

---

## 1. Test environment

- **Operating systems:** Windows 10 22H2 and Windows 11 (current release)
- **Display:** one 100% DPI monitor, plus a second monitor at 150% for the floating-control cases
- **Keyboard layouts:** English (US) and Persian (Standard) both installed
- **Settings:** After-stop insertion mode unless the case says otherwise, Colloquial style, Standard cleanup, Auto language
- **Standard test phrase** for insertion checks: C01 from the evaluation corpus. Expected: `این API رو از Google AI Studio گرفتم ولی هنوز نمی‌دونم کدوم model برای transcription بهتره.`
- **Clipboard sentinel:** before each case, copy the text `CLIPBOARD-SENTINEL-42`

---

## 2. Test cases

| ID | Case | Steps | Pass criteria | PRD |
|---|---|---|---|---|
| T01 | Basic insert, empty field | Focus empty field → hotkey (hold) → say C01 → release | Exact expected text appears at the caret, once | FR-3.1, 3.3 |
| T02 | Insert mid-text | Field contains `Hello world`; caret between the words → dictate E06 | Text inserted at the caret with correct spacing; surrounding text untouched | FR-1.4 |
| T03 | Replace selection | Select `world` in `Hello world` → dictate E06 | Selection replaced | FR-1.3 |
| T04 | Toggle mode | Toggle start → say A01 → toggle stop | Same as T01 | FR-2.3 |
| T05 | Floating control start/stop | Focus field → click control → speak → click control | Field keeps focus throughout; text inserted | FR-2.8, 2.10 |
| T06 | Persian integrity | Dictate I01 and I05 | ZWNJ and ی/ک exact; English terms in Latin script; no stray control characters | FR-3.3, 5.6, 5.7, 5.14 |
| T07 | Single undo | After T01, press Ctrl+Z once | Entire dictation removed in one undo (**SHOULD**: record the result, not a blocker) | FR-1.5 |
| T08 | Clipboard untouched | After T01, paste into Notepad and open Win+V history | Paste gives `CLIPBOARD-SENTINEL-42`; the dictated text is not in clipboard history | FR-3.4 |
| T09 | Persian keyboard layout | Switch to the Persian layout → repeat T01 with E06 | Hotkey works; English text inserted correctly; layout still Persian afterwards | FR-2.4, 3.3 |
| T10 | Hotkey doesn't leak | Hold-to-talk in the field without speaking long enough (< 300 ms) | No character typed, no menu opened, no API call, nothing inserted | FR-2.1, 2.6 |
| T11 | Cancel | Start → speak → press Esc | Nothing inserted; Cancelled state shown | FR-2.7 |
| T12 | Focus moved to another window | Start → say C01 → stop → immediately click a different app | Nothing inserted anywhere; text offered for copy/paste | FR-3.6, D5 |
| T13 | Focus moved within the same window | Two fields in the same window: start in field 1, stop, click field 2 before the result arrives | Text inserted into field 2 | FR-3.6 |
| T14 | Never presses Enter | In a chat app where Enter sends, dictate L03 (long paragraph) | Message is **not** sent; no line breaks in the field | FR-1.6, D8 |
| T15 | Autocomplete field | Search box with suggestions: dictate E09 | Text arrives as one edit; no flicker of partial searches (**SHOULD**) | FR-3.5 |
| T16 | Password field override | Focus a password field → hotkey | Confirmation prompt shown; decline records nothing; confirm allows one dictation; no preview text; nothing kept afterwards | FR-3.7, D7 |
| T17 | Busy while processing | Start the next dictation during Processing | Busy feedback; no second session; previous result still inserted | D6 |
| T18 | Streaming mode | Switch to Streaming → dictate L04 | Only finalized segments appear; already-inserted text is never deleted or rewritten | FR-3.2 |
| T19 | Character limit | Field limited to 20 characters → dictate E01 | What fits is inserted; the full text stays recoverable | §6 |
| T20 | No editable field | Click the desktop → dictate E06 | Nothing typed anywhere; text available to copy | FR-1.2, 9.3 |

**Core cases:** T01, T03, T05, T06, T08, T09, T12, T14 and T16 are run on **every** app. The remaining cases run on the apps where they apply (see the matrix).

---

## 3. App matrix

Legend: ● = run · ○ = run if the app has that kind of field · — = not applicable

### Tier 1 (MUST)

| App | Field(s) under test | Core | T02 T04 T07 T10 T11 T13 | T15 | T17 T18 T19 T20 |
|---|---|---|---|---|---|
| Chrome: ChatGPT | Message composer (Enter sends) | ● | ● | — | ● |
| Chrome: Gmail | Compose subject + body (rich text) | ● | ● | — | ● |
| Chrome: Google Search | Search box | ● | ● | ● | ○ |
| Edge: generic web form | Text input + textarea + `maxlength` input + password input | ● | ● | — | ● |
| Notepad | Document | ● | ● | — | ● |
| Microsoft Word | Document body | ● | ● | — | ● |
| Outlook (classic or new) | New mail: To, Subject, body | ● | ● | ○ | ● |
| VS Code | Editor, search box, commit message box | ● | ● | ● | ● |
| Telegram Desktop | Message box (Enter sends) | ● | ● | — | ● |
| WhatsApp Desktop | Message box (Enter sends) | ● | ● | — | ● |
| Windows Start / Search | Search box | ● | ● | ● | ○ |

### Elevated apps (MUST, D18)
Run each app **as administrator**.

| App | Field(s) under test | Core | Extra |
|---|---|---|---|
| Notepad (elevated) | Document | ● | T02, T13 |
| VS Code (elevated) | Editor | ● | T02 |
| Windows Terminal / PowerShell (elevated) | Command prompt line (**must not execute**) | ● | T14 |
| Registry Editor | Find dialog text box | ● | — |

Also verify: app launched at sign-in still reaches elevated targets without a manual approval prompt each time.

### Tier 2 (SHOULD)

| App | Field(s) under test |
|---|---|
| Firefox | Generic web form (same page as Edge) |
| Slack Desktop | Message box |
| Microsoft Teams | Chat message box |
| Discord | Message box |
| JetBrains IDE | Editor, Search Everywhere |
| Windows Terminal (normal) | Command line (must not execute) |
| File Explorer | Rename a file; address bar |
| Any other Electron app you use daily | Main text field |

Run the **core cases** only on Tier 2 apps.

### Tier 3 (best effort; document results)

| Environment | Field under test |
|---|---|
| Remote Desktop session | Notepad inside the remote session |
| Virtual machine window | Notepad inside the VM |
| Full-screen game or presentation | Floating control hides; listening indicator still visible (FR-2.11, 6.3) |

---

## 4. Floating control and indicator checks (run once per build)

| ID | Check | Pass criteria |
|---|---|---|
| U01 | Drag the control to the second monitor (150% DPI) → restart app | Position remembered, correctly scaled |
| U02 | Floating control **disabled** → start dictation | Listening indicator still clearly visible while listening |
| U03 | Listening state with a colorblind simulator or grayscale | Listening is distinguishable without color |
| U04 | Ready state | Windows "microphone in use" indicator is **off** |
| U05 | Lock the PC (Win+L) during listening → unlock | Listening stopped; nothing inserted after unlock |
| U06 | Unplug the selected microphone during listening | Listening stops, notice shown, captured audio kept for retry |

---

## 5. Result recording

One row per **app × field × case**:

| App | Field | Case | OS | Result (Pass / Fail / N/A) | Observed | Notes / screenshot |
|---|---|---|---|---|---|---|

---

## 6. Release gate

| Gate | Target |
|---|---|
| Insertion success across Tier 1 + elevated apps (T01, T03, T06 rows) | ≥ 98% |
| Text inserted into an unintended window (T12, T20) | **0** |
| Message sent or command executed by the app (T14) | **0** |
| Clipboard or keyboard layout altered (T08, T09) | **0** |
| Password-field dictation without confirmation (T16) | **0** |
| Lost dictations: failed insertion without recoverable text (T12, T19, T20) | **0**, except password fields by design |
| Tier 2 core-case pass rate | ≥ 90% (below target = documented known issue, not a blocker) |
