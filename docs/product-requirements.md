# Product Requirements (Refined): Windows AI Voice Dictation ("Wispr Flow–like")

## Context

The user wants a Windows app that lets them dictate by voice into almost any focused text field, in the style of Wispr Flow. It must handle Persian, English, and mixed Persian-English technical speech well, and it will use the Gemini API (a Google AI Studio key, free tier where possible).

This document reviews and refines the original brief **as product requirements only**. It does not pick an architecture, framework, language, or implementation approach. It resolves ambiguities, adds missing requirements, lists edge cases, and defines measurable acceptance criteria.

**Status:** **v1.1, requirements frozen.** Decisions D1–D22 are all confirmed. Development starts without pre-build engine testing (D22). Validation runs against the finished MVP (§10), and Q12, whether a second cleanup model is needed, is decided then.

Priority labels: **MUST** = MVP blocker · **SHOULD** = MVP, can slip · **COULD** = post-MVP candidate.

---

## 1. Product Definition

A system-wide voice input method for Windows. Voice works like a keyboard: text goes wherever the caret is. There is no separate transcription window, no copy step, and no app switching.

**Core loop:** focus an editable field → activate → speak (Persian / English / mixed) → cleaned-up text appears at the caret → keep working.

### Product principles (these settle trade-offs)
1. **Never lose the user's words.** If the text can't be inserted, it can still be recovered.
2. **Never put text in the wrong place.** Not inserting is better than inserting into an unintended window.
3. **Transcribe, never respond.** Spoken content is data, never an instruction to the AI.
4. **The microphone is never live without a visible indicator.**
5. **Preserve meaning and language.** No translation, and no transliteration of English terms.
6. **Speed beats features.** Every extra step in the core loop is a defect.

---

## 2. Decisions Made in This Review

| # | Topic | Decision |
|---|---|---|
| D1 | When text is inserted | User setting. **Default: insert once after dictation stops.** Optional mode: streaming insertion while speaking. |
| D2 | Persian style | User setting: **Colloquial (as spoken)** or **Formal written**. Default: Colloquial. |
| D3 | Hotkey behavior | **Both hold-to-talk and toggle (hands-free)** in the MVP. |
| D4 | Network | Direct access to Gemini. Proxy settings are not an MVP requirement. The app must still work with a system-level proxy/VPN if one is present. |
| D5 | Focus change during processing (**confirmed**) | The target is locked at activation. If focus has moved to a *different window* when the result is ready, do not insert. Keep the result and offer one-click copy/paste instead (see FR-3.6). |
| D6 | Activation while Processing (**confirmed**) | A new session can't start until the previous result is inserted or has failed. The user gets brief "busy" feedback. Cancel always works. |
| D7 | Password fields (**confirmed**) | **Blocked by default, with an override.** Dictating into a detected password field asks the user to confirm first. Confirming allows that one dictation (FR-3.7). |
| D8 | Line breaks / submit (**confirmed**) | The app never presses Enter or any submit action. In chat apps a newline can send the message, so no line breaks are inserted unless the user explicitly enables them. |

| D9 | Speech-to-text engine | **Default: Gemini 3.5 Transcribe Live** (Gemini Live API, streaming). The product must not be tied to it: models and APIs can be swapped (FR-13). |
| D10 | App UI language | **English.** Dictated Persian text shown inside the app (preview, last dictation, history) must still display correctly right-to-left. |
| D11 | Custom vocabulary | **Not in the MVP.** Keeping technical terms correct relies on the engine and on FR-5.10/5.11. |
| D12 | Technical and specific terms | **Never translated. Always written in their original English form**, even where a Persianized spelling is common. |
| D13 | Persian suffix on English words | **Joined with a half-space (ZWNJ):** "API‌ها", "model‌ها", "file‌ام". |
| D14 | Digits | **Latin digits (0–9) everywhere**, including inside Persian sentences. |
| D15 | Expected usage | **Up to ~2,000 dictated words per day** (about 15 minutes of speech, roughly 50–100 dictations). |
| D16 | Spoken punctuation / formatting commands | **Not in the MVP.** Punctuation comes from the engine automatically. Words like "نقطه" or "comma" are written as spoken words. |
| D17 | Dictation history | **Not in the MVP.** Only the latest dictation is kept, in memory only, for recovery (FR-9.3). |
| D18 | Apps running as administrator | **Must be supported.** Dictation works into elevated apps. |
| D19 | Audience | **Personal use** for now. Distributing to other users is not an MVP goal. |
| D20 | API key tier | **Free tier** is the default. The user accepts Google's free-tier data-use terms (FR-11.6), and free-tier limits are a hard constraint. |
| D21 | Providers beyond Gemini | **Undecided, deferred.** The MVP ships Gemini engines only, but engine profiles must stay provider-neutral so another provider can be added later (FR-13). |
| D22 | Validation timing | **No pre-build engine testing.** Development starts now; the evaluation corpus and app test plan are run against the finished MVP as acceptance tests. Accepted risks are listed in §10. |

D5–D8 are confirmed.

---

## 3. Scope

### MVP goals
- Reliable insertion into Tier-1 apps (§7.4)
- High-quality Persian and mixed Persian-English transcription with clean formatting
- Activation in under a second, and low end-to-end latency (§7.1)
- Near-zero cost for personal use on the Gemini free tier

### Explicit non-goals (MVP)
- Voice commands for editing ("delete that", "make this formal") and command mode on selected text
- Context-aware tone per app (casual in chat, formal in email)
- File or meeting transcription, speaker diarization
- macOS, mobile, cloud sync, accounts, multiple users, team features
- Offline or local speech recognition
- Custom vocabulary / word lists (D11)
- Persian app UI (D10)
- Spoken punctuation and formatting commands (D16)
- Dictation history (D17)
- Distribution to other users: installer polish, code signing, auto-update, multi-user support (D19)
- Providers other than Gemini (D21), although the engine design must allow them later
- Languages other than Persian and English
- Translation of any kind

---

## 4. Functional Requirements

### FR-1 Dictation session and target
- **1.1 MUST** A session has a *target*: the focused editable element and its window at the moment of activation.
- **1.2 MUST** Activation is allowed even when no editable field can be detected, because detection is unreliable in some apps. If insertion is impossible, the result goes to the recovery path (FR-9.3).
- **1.3 MUST** If text is selected in the target, the dictated text replaces the selection, the same way typing does.
- **1.4 SHOULD** Spacing adapts to the caret context. Add a leading space when the character before the caret is not whitespace or an opening bracket. In English, capitalize at a sentence start. When the context can't be read, use safe defaults: no leading space, and capitalization based on the transcript alone.
- **1.5 SHOULD** One inserted dictation can be undone with a single standard undo in the target app, as far as the app allows.
- **1.6 MUST** The app never sends Enter, Tab, or submit keystrokes as part of insertion (D8).

### FR-2 Activation and control
**Global shortcut**
- **2.1 MUST** The shortcut is user-configurable, works system-wide, and the key is not passed through to the target app. For example, it must not type a character or open a menu.
- **2.2 MUST** Hold-to-talk: listen while the key is held, and stop and process on release.
- **2.3 MUST** Toggle (hands-free): one gesture starts listening and the same gesture stops it. Examples of a gesture are a separate shortcut or a double-tap, to be specified in UX design.
- **2.4 MUST** The shortcut works no matter which keyboard layout is active (Persian or English).
- **2.5 MUST** Shortcut conflicts are detected while the user sets the shortcut. The app rejects or warns about combinations Windows reserves or that are already registered. It ships with a sensible default.
- **2.6 MUST** A short, accidental press shorter than the minimum duration (for example < 300 ms, tunable) is discarded silently. It makes no API call and inserts nothing.
- **2.7 MUST** Cancel: a dedicated action (for example Esc while listening, plus a cancel button on the floating control) discards the session. No API call is made and nothing is inserted.

**Floating control**
- **2.8 MUST** Clicking the floating control never takes keyboard focus away from the target field. This is critical: if it did, the control couldn't do its job.
- **2.9 MUST** The control stays on top of other windows, is very small, can be dragged, and remembers its position per monitor. It handles multi-monitor setups and DPI scaling.
- **2.10 MUST** From the control the user can start, stop, and cancel, and see the current state.
- **2.11 SHOULD** Hide automatically when a full-screen app is in front (games, presentations). The listening indicator still shows (FR-6.3).
- **2.12 SHOULD** Show a live preview or activity cue of recognized speech inside the control, never inside a separate window.

**Tray**
- **2.13 MUST** A system tray icon shows the state and gives access to Start/Stop, Settings, Pause app, and Quit.

### FR-3 Insertion modes (D1)
- **3.1 MUST** *After-stop mode (default):* the final cleaned-up text is inserted once, as a single edit where the target app allows.
- **3.2 SHOULD** *Streaming mode (opt-in):* text is inserted progressively while the user speaks.
  - Only finalized segments are inserted. The app never deletes or rewrites text it has already inserted.
  - If focus leaves the target mid-stream, insertion pauses and text is buffered (FR-3.6 applies).
  - The settings UI states that streaming mode may use lighter cleanup and is less reliable in apps with autocomplete or custom editors.
- **3.3 MUST** Insertion reproduces the text exactly, including Persian characters, ZWNJ, and mixed-direction text. The active keyboard layout must not affect it.
- **3.4 MUST** Insertion must not leave side effects: the user's clipboard content is kept, dictated text doesn't linger in clipboard history, and the keyboard layout isn't changed.
- **3.5 SHOULD** Search or autocomplete fields receive the text as one edit, not one keystroke at a time where that can be avoided.
- **3.6 MUST** Focus-change rule (D5): when a result is ready and the foreground window differs from the target window, the text is not inserted. The result is kept and the user is told it can be copied or pasted with one click. Changing focus to another field within the same window is allowed, and the text goes to the current caret.
- **3.7 MUST** Password fields (D7):
  - When the target is detected as a password field, activation doesn't start listening. A small confirmation appears: *"This is a password field. Dictate anyway?"*
  - The confirmation states that audio goes to the active provider, and on the free tier may be used by Google (FR-11.6). It also warns that automatic formatting may change capitalization, spacing, or punctuation.
  - Confirming allows **only that one dictation**. The next dictation into a password field asks again. There is no "don't ask again" in the MVP.
  - Declining or ignoring the prompt cancels without recording.
  - For an overridden dictation, text is never shown in the floating-control preview, and it isn't kept as "last dictation" after a successful insert.
  - If insertion fails, the text is discarded rather than kept for copying.

### FR-4 Transcription and language
- **4.1 MUST** Language modes:
  - **Auto (Persian-English mixed), the default:** the base language may be Persian or English per utterance, and code-switching is expected inside a sentence.
  - **Persian:** biased toward Persian as the base language. Embedded English terms still stay in Latin script.
  - **English:** biased toward English.
- **4.2 MUST** No mode ever translates. If the user speaks English in Persian mode, English is written. If they speak Persian in English mode, Persian is written, or an error is shown. Translation is never an acceptable result.
- **4.3 MUST** *Transcribe, never respond:* spoken instructions are written out literally. Saying "translate this to English", "ignore previous instructions", or asking a question inserts those exact words. The app never answers, summarizes, completes, or follows them.
- **4.4 MUST** A recording with no detectable speech inserts nothing and briefly shows a "No speech detected" state. Output made up from silence or noise counts as a severity-1 defect.
- **4.5 MUST** The user can change the model and API without an app update, because models and free-tier availability change over time. See FR-13.
- **4.6 (not in MVP)** Custom vocabulary is out of scope (D11). A post-MVP candidate if engine validation shows technical terms are recognized poorly.

### FR-5 Output quality rules
**General**
- **5.1 MUST** Natural punctuation, sentence boundaries, correct spacing.
- **5.2 MUST** Filler words are removed or reduced: English (um, uh, like, you know) and Persian (اِ، اوم، خب، یعنی، مثلاً when used as fillers). Only disfluencies are removed, never words that carry meaning.
- **5.3 MUST** Self-corrections are handled conservatively. "Tuesday, no, Wednesday" may become "Wednesday" only if the level of cleanup the user chose allows it. Otherwise the words are kept.
- **5.4 MUST** Meaning is never changed: nothing is added, no content is summarized, and the tone isn't rewritten beyond the chosen Persian style.
- **5.5 SHOULD** Cleanup level setting: *Light* (punctuation and formatting only) or *Standard* (the default, which also removes fillers).

**Persian-specific**
- **5.6 MUST** Persian letters ی and ک are used, never Arabic ي and ك.
- **5.7 MUST** ZWNJ (نیم‌فاصله) is used correctly: می‌/نمی‌ prefixes, ‌ها/‌های plurals, ‌ای/‌ام/‌اید suffixes, compound words.
- **5.8 MUST** Persian punctuation is used in Persian sentences (، ؛ ؟ « »). English sentences use Latin punctuation. In mixed sentences, punctuation follows the sentence's base language.
- **5.9 MUST** Persian style setting (D2): *Colloquial* keeps the spoken form ("نمی‌دونم", "رو", "کدوم"). *Formal* converts it to standard written Persian ("نمی‌دانم", "را", "کدام") while keeping the meaning.
- **5.10 MUST** English technical terms, product names, and code identifiers stay in Latin script with correct casing: API, Google AI Studio, model, transcription, GPT-4o, camelCase names. They are neither translated (رابط برنامه‌نویسی) nor transliterated (ای‌پی‌آی).
- **5.11 MUST** Technical and domain-specific terms of English origin are written in their **original English form even when a Persianized spelling is common**: "file", not "فایل"; "model", not "مدل" (D12). Ordinary Persian vocabulary stays in Persian script.
- **5.12 MUST** Persian suffixes on English words are joined with a half-space (ZWNJ): "API‌ها", "model‌های", "file‌ام" (D13). Never a regular space, and never glued on with no separator.
- **5.13 MUST** Latin digits (0–9) everywhere, including in Persian sentences, dates, times, and amounts (D14). Persian digits (۰–۹) or Arabic-Indic digits in engine output are converted.
- **5.14 MUST** No invisible or bidi control characters are inserted other than ZWNJ.

**Worked acceptance example**
Spoken: «این API رو از Google AI Studio گرفتم ولی هنوز نمی‌دونم کدوم model برای transcription بهتره»
- Colloquial → `این API رو از Google AI Studio گرفتم، ولی هنوز نمی‌دونم کدوم model برای transcription بهتره.`
- Formal → `این API را از Google AI Studio گرفتم، ولی هنوز نمی‌دانم کدام model برای transcription بهتر است.`

### FR-6 States and feedback
- **6.1 MUST** Canonical states. The original list is consolidated here: "Idle" and "Ready" are merged, and "Stopped" and "Complete" become short-lived.

| State | Kind | Meaning |
|---|---|---|
| Not configured | Persistent | Missing API key or no usable microphone |
| Ready | Persistent | Can start dictation |
| Listening | Persistent | Microphone is capturing |
| Processing | Persistent | Audio sent, waiting for text |
| Inserted | Transient (~1 s) | Success |
| Cancelled | Transient | Session discarded by the user |
| No speech detected | Transient | Nothing inserted |
| Busy | Transient | Activation refused during Processing (D6) |
| Paused | Persistent | User turned dictation off from the tray; the shortcut does nothing |
| Error: microphone | Actionable | Device missing or disconnected, access denied by Windows privacy settings, input silent or muted |
| Error: network | Actionable | No connection or timeout |
| Error: authentication | Actionable | Invalid or revoked API key |
| Error: quota / rate limit | Actionable | Free-tier limit reached; show the reset time if known |
| Error: transcription | Actionable | Provider error or unusable response |
| Not inserted: text kept | Actionable | Target changed or insertion failed; copy/paste available |

- **6.2 MUST** Listening is unmistakable at a glance, and never shown by color alone (colorblind-safe: use shape or motion too).
- **6.3 MUST** The listening indicator is shown **even when the floating control is disabled**. "Floating control disabled" means *hidden while Ready*, not hidden while listening.
- **6.4 MUST** Error messages say what happened and what to do next, in one short line, with no technical dump.
- **6.5 SHOULD** Optional start, stop, and error sounds. They are on by default and can be turned off.
- **6.6 COULD** Mute or lower system audio while listening, so media playback isn't transcribed.

### FR-7 Microphone
- **7.1 MUST** The user can pick from the Windows input devices or choose "System default", which follows changes in Windows.
- **7.2 MUST** The selection is remembered across sessions and reboots.
- **7.3 MUST** If the saved device is missing at activation, the app falls back to the system default and shows a visible notice. It does not overwrite the saved preference, and it switches back automatically when the device returns.
- **7.4 MUST** If the device disconnects while listening, listening stops, audio captured so far is kept for processing or retry, and the user is notified.
- **7.5 MUST** When Windows privacy settings deny microphone access, the app detects it and tells the user how to fix it.
- **7.6 SHOULD** Settings include an input level meter and a test recording.
- **7.7 SHOULD** Warn when the input is silent while listening (muted hardware or wrong device).
- **7.8 MUST** The microphone is released when not listening, so the Windows "microphone in use" indicator is off when Ready. *This conflicts with avoiding clipped first syllables; see NFR-1.3.*

### FR-8 Settings
**MUST:**
- Gemini API key: entered masked, with a Test/Validate action and a Remove action
- Microphone: device picker and test
- Shortcut: hold-to-talk key and toggle gesture, with conflict detection
- Hotkey default mode
- Language mode: Auto mixed / Persian / English
- Persian style: Colloquial / Formal
- Insertion mode: After stop / Streaming
- Floating control: on/off and reset position
- Hands-free safety: maximum session length, and auto-stop after N seconds of silence (on/off)
- Launch at Windows sign-in

**SHOULD:** cleanup level, engine profiles (FR-13), sounds, allow line breaks (D8).

Settings apply without a restart and are stored per Windows user account.

### FR-9 Errors, retry, recovery
- **9.1 MUST** On a network, quota, or transcription error, the captured audio is kept **in memory** so the user can **Retry** without speaking again, until the next session starts or a short timeout passes.
- **9.2 MUST** Retry obeys the focus rule (FR-3.6) at the moment of insertion.
- **9.3 MUST** Recovery path: every result that couldn't be inserted stays available as "last dictation", with one-click copy and paste-at-caret.
- **9.4 (not in MVP)** Dictation history is out of scope (D17). The latest dictation stays in memory only and is gone when the app quits.
- **9.5 MUST** A hands-free session that reaches its maximum length warns before stopping, then processes what was captured. It never discards audio silently.

### FR-10 Onboarding (first run)
- **10.1 MUST** Guided setup: get and paste an API key (link to Google AI Studio) → validate → choose and test the microphone → choose the shortcut → try a dictation in a practice field.
- **10.2 MUST** A plain-language privacy disclosure (FR-11.6) is shown before the first dictation.
- **10.3 SHOULD** Offer to launch at sign-in.

### FR-11 Privacy and security
- **11.1 MUST** The app never starts listening without an explicit user action. There is no auto-start listening on launch, wake, unlock, or reconnect.
- **11.2 MUST** When the session is locked, the machine sleeps, or the user switches accounts during listening, listening stops immediately and nothing is inserted after resume.
- **11.3 MUST** Every API key is stored encrypted and bound to the Windows user. It is never shown in full after entry, never written to logs or crash reports, never placed on the clipboard, and never sent anywhere except the API of the provider it belongs to.
- **11.4 MUST** Audio is not saved to disk by default and is sent only to the active transcription provider.
- **11.5 MUST** No telemetry or analytics by default. Diagnostic logs contain no audio, transcripts, or keys unless the user turns on a clearly labeled debug option.
- **11.6 MUST** Disclose that on Google's **unpaid Gemini API tier, submitted content may be used by Google to improve its products and may be seen by human reviewers**. Google's pricing page lists "Used to improve our products: Yes" for the free tier of Gemini 3.5 Transcribe (Live) and "No" for paid. Recommend a paid-tier key for sensitive dictation. Show the matching disclosure for any other provider the user configures (FR-13.9).
- **11.7 MUST** Uninstalling removes the stored key, settings, and history, or offers to.

### FR-12 App lifecycle
- **12.1 MUST** Only one instance runs at a time, it lives in the system tray, and it launches at sign-in if enabled.
- **12.2 MUST** On quit, the microphone and global shortcuts are released.
- **12.3 MUST** Supported OS: Windows 10 22H2 and Windows 11.
- **12.4 COULD** Notification when an update is available.

### FR-13 Swappable speech engines (models and APIs)
Goal: Gemini 3.5 Transcribe Live is the default, but replacing the model or the whole API must be a settings change, not a product change.

- **13.1 MUST** *Engine profile*: a saved configuration holding the provider/API type, model ID, credentials, and engine options (language hints, cleanup mode). The user can keep several profiles; exactly one is active.
- **13.2 MUST** The model ID can be typed in freely, not only picked from a built-in list. Preview model names change; Google's own pages already show both `gemini-3.5-transcribe-live` and `gemini-3.5-transcribe-live-preview`.
- **13.3 MUST** Switching the active profile takes effect on the next dictation, with no restart and no app update.
- **13.4 MUST** *Capability-aware behavior*: each profile declares what its engine can do:
  - streaming or record-then-send
  - interim results
  - built-in cleanup/formatting
  - language hints
  - manual turn control (push-to-talk)
  - maximum session length
  - free-tier availability

  Features the active engine can't support are disabled with a one-line reason, never silently broken. For example, with a record-then-send engine, streaming insertion mode falls back to after-stop mode.
- **13.5 MUST** *Product rules don't depend on the engine*: FR-4 and FR-5 (Persian rules, Colloquial/Formal, no translation, Latin technical terms) define the product, whatever model is behind it. If an engine can't meet them on its own, the product must still meet them. That may require an optional **text-refinement stage** with its own separately selectable model and API (a second profile type), which the user can turn off.
- **13.6 MUST** Every stage that uses a general-purpose LLM, including refinement, must pass the "transcribe, never respond" (FR-4.3) and "no invented text" (FR-4.4) tests.
- **13.7 MUST** Credentials are stored per provider under FR-11.3 and sent only to that provider.
- **13.8 SHOULD** *Fallback profile*: the user can opt in to automatically retrying with a secondary profile when the primary fails (quota, outage, auth). The switch is visible every time, never silent, because a different provider means different data-use terms.
- **13.9 SHOULD** Each profile shows its privacy/data-use note and whether it's on a free or paid tier.
- **13.10 SHOULD** *Test and compare*:
  - A "Test connection" action for each profile.
  - A benchmark action that runs the evaluation corpus (§8.1) against any profile and reports the §8.2 metrics plus latency, so switching engines is based on evidence.
- **13.11 SHOULD** The MVP supports at least **two API types**: the streaming Gemini Live API and one record-then-send API (for example non-live `gemini-3.5-transcribe`). This proves the flexibility in practice, not just on paper. Other providers are deferred (D21), but nothing in profiles, settings, or error handling may assume Gemini.
- **13.12 COULD** Usage meter per profile: audio minutes today and this month, and the estimated cost on paid tiers.
- **13.13 COULD** Export/import profiles without their keys.

---

## 5. Default Speech Engine: Gemini 3.5 Transcribe Live — Fit Against Requirements

Facts come from Google's public docs as of September 2026 (see Sources). Items marked **Validate** are measured on the evaluation corpus during post-build acceptance (D22).

| Topic | What Google documents | Product implication |
|---|---|---|
| Availability | Announced Aug 26, 2026 as a **public preview** in the Gemini API and AI Studio. One doc page describes it as GA, and the model IDs differ between pages (`-live` vs `-live-preview`). | Treat the name and behavior as unstable → FR-13.2. Re-check before release. |
| Free tier | Pricing page lists a free tier. Paid tier is about $0.005/min audio in plus $0.004/min text out, roughly **$0.009/min**. | Free tier is the chosen default (D20). For reference, the expected ~15 min/day would cost only about $4/month on paid, if privacy or limits ever require switching. |
| Data use | Free tier: used to improve Google products **Yes**. Paid: **No**. | FR-11.6 disclosure is required. |
| Rate limits | Not published per model. Limits apply **per Google Cloud project, not per API key**, and are shown in AI Studio. | **Validate** P3 on the free tier: about 2,000 words / 15 minutes / up to 100 dictations per day, plus short bursts of several dictations per minute. Extra keys in the same project don't add quota. Also check whether the fallback model (FR-13.8) has separate quota. |
| Languages | 85+ languages with Persian (`fa-IR`) listed, automatic detection, language hints, and code-switching. Language detection is described as per utterance. | **Validate (top risk):** mixing languages *inside one sentence* (the core use case) may be weaker than switching between utterances. |
| Streaming results | Interim (partial) and final transcripts. | Streaming mode inserts **finals only** (FR-3.2). Interims fit the floating-control preview (FR-2.12). |
| Turn control | Automatic, hybrid (server detects the start, client ends the turn), and manual (push-to-talk). | Hold-to-talk and toggle both fit D3. **Validate** that the first syllable isn't clipped (NFR-1.3). |
| Smart mode | Removes fillers, resolves self-corrections, formats lists and bullets, and polishes grammar and capitalization. | Covers much of FR-5.1–5.3. But **list/bullet formatting can add line breaks → conflicts with D8** (Enter sends in chat apps), and resolving self-corrections is more aggressive than FR-5.3. Map Cleanup *Light* to non-Smart and *Standard* to Smart, and strip line breaks unless allowed. |
| Custom vocabulary | Up to 1,000 terms; works best with about 100 or fewer. | Not used in the MVP (D11). Because nothing steers recognition of technical terms, **Validate** FR-5.10/5.11 carefully. The feature remains an easy post-MVP option if needed. |
| Session length | Live sessions max out at **10 minutes**. | The hands-free maximum must be ≤ 10 minutes, or continue across sessions without losing words (FR-9.5). |
| Latency | "Sub-second" latency claimed; time to final transcript said to be 70% faster than the previous model. | **Validate** against NFR-1. |
| Not documented | Converting colloquial ↔ formal Persian, ZWNJ and ی/ک correctness, keeping English technical terms in Latin script, never translating. | **Validate.** If these fail, FR-13.5 needs a refinement stage, which adds latency, quota use, and instruction-following risk (FR-13.6). |

Constraints that still apply to *any* engine:
- **P1.** Accepts audio, gives strong Persian and mixed-language recognition, and is usable on a free tier. If there is no free tier, the cost must be disclosed.
- **P2.** Latency supports NFR-1. Streaming insertion requires an engine with incremental results.
- **P3.** Free-tier limits allow the expected usage with headroom (D15): **≥ 3,000 words / ~25 minutes of audio / 150 dictations per day**, and bursts of at least 5 dictations per minute.
- **P4.** The app reacts properly to quota and rate-limit responses (FR-6, FR-9), including the fallback profile when enabled (FR-13.8).
- **P5.** The FR-4.3 and FR-4.4 tests run against every engine and refinement model before it's recommended.
- **P6.** Model deprecation and changes to free-tier terms are ongoing risks, which FR-13 mitigates.

---

## 6. Edge-Case Catalog

| Area | Edge case | Required behavior |
|---|---|---|
| Focus | User clicks into another app while Processing | FR-3.6: don't insert; keep text; offer copy/paste |
| Focus | User moves to another field in the same window | Insert at current caret |
| Focus | User keeps typing in the target during Processing | Insert at the caret's current position |
| Focus | No field focused (desktop, Explorer background) | Allow dictation → recovery path |
| Focus | Target window closed before the result is ready | Recovery path |
| Floating control | Clicking it would steal focus | Forbidden (FR-2.8) |
| Target type | Password field | Blocked by default; confirm once to dictate; no preview, nothing kept (FR-3.7) |
| Target type | Chat app where Enter sends | No newline or submit ever inserted (D8) |
| Target type | Search box with autocomplete on each key | Single edit where possible (FR-3.5) |
| Target type | Terminal / console | Tier 2; text inserted literally, never executed |
| Target type | Rich-text editor (Word, Gmail) | Keep the formatting at the caret |
| Target type | Field with a character limit | Insert what fits; the rest stays recoverable |
| Target type | LTR-only field showing Persian | Insert correct text; how the field renders it is out of scope |
| Privilege | Target app runs as administrator | **Must work** (D18). Launch at sign-in must still work without a manual approval every time. **Validate** feasibility. |
| Privilege | UAC prompt, lock screen, secure desktop | Not supported; never listen there |
| Environment | Remote Desktop / VM / Citrix window | Tier 3 best effort; documented |
| Environment | Full-screen game or presentation | Control hides; the indicator still shows while listening |
| Environment | Multi-monitor, mixed DPI | Control positions and scales correctly |
| Keyboard | Persian layout active | Shortcut works; English text inserted correctly |
| Keyboard | Shortcut conflicts with another app | Detected at configuration time |
| Keyboard | Key released very quickly | Discard, no API call (FR-2.6) |
| Keyboard | Stuck modifier after hold-to-talk | Modifier state must not stay "pressed" in the target |
| Audio | Bluetooth headset switches profile (start delay, quality drop) | First words not lost (NFR-1.3); quality warning is a SHOULD |
| Audio | Mic muted / wrong device / silent input | Warning (FR-7.7), "No speech detected" |
| Audio | Background media or other speakers | Mute/lower audio is a COULD; no invented text |
| Audio | Device unplugged mid-session | FR-7.4 |
| Session | Hands-free left on by accident | Silence auto-stop and maximum length (FR-8, FR-9.5) |
| Session | Very long dictation (minutes) | Supported up to the configured maximum; no silent cut |
| Session | Activation during Processing | Busy feedback (D6) |
| Session | Lock, sleep, or user switch during listening | FR-11.2 |
| Network | Offline / timeout | Network error + in-memory retry |
| Provider | Invalid or revoked key | Authentication error pointing to Settings |
| Provider | Quota exhausted | Quota error + reset time + retry |
| Provider | Model returns an answer instead of a transcript | Severity-1 defect (FR-4.3) |
| Provider | Model ID renamed or retired (preview → GA) | Clear "model not found" error pointing to the engine profile; fallback if enabled (FR-13.8) |
| Provider | Live session hits the 10-minute engine limit | Continue or stop per FR-9.5; never lose words |
| Provider | Smart formatting produces bullets or line breaks | Line breaks stripped unless the user allows them (D8) |
| Provider | User switches profile while a dictation is in progress | The current dictation finishes on the old profile; the new one applies from the next dictation |
| Provider | Active engine lacks a feature the user enabled (streaming, cleanup mode) | Feature disabled with a reason; safe fallback (FR-13.4) |
| Content | Spoken punctuation words ("نقطه", "comma") | Written as words; the engine punctuates automatically (D16) |
| Content | Persian or Arabic-Indic digits in engine output | Converted to Latin digits (D14) |
| Content | Persian suffix on an English term ("modelها") | Joined with ZWNJ: "model‌ها" (D13) |
| Content | Emails, URLs, file paths, code identifiers | Standard format (`name@example.com`, `camelCase`) when clearly meant |
| Content | Numbers, dates, currency, versions | Per the digit rule (5.13); versions and technical tokens use Latin digits |
| Content | Mixed sentence starting with an English word | Correct text; Persian-first line rendering is up to the target app |

---

## 7. Non-Functional Requirements

### 7.1 Latency (NFR-1). These are proposed targets, to be confirmed in provider validation.
- **1.1** Shortcut press → listening indicator visible: **≤ 150 ms**
- **1.2** Stop → text inserted (after-stop mode, utterance ≤ 15 s, typical broadband): **p50 ≤ 1.5 s, p95 ≤ 3 s**
- **1.3** No clipped first syllable: speech starting right after the press is captured, even though the microphone is released while Ready (FR-7.8)
- **1.4** Streaming mode: first text inserted ≤ 2 s after speech starts
- **1.5** App launch to Ready: ≤ 3 s

### 7.2 Reliability (NFR-2)
- Insertion success rate on Tier-1 apps **≥ 98%** of attempts; success here means correct text at the correct location
- **Zero** instances of text inserted into an unintended window in the test matrix
- **Zero** lost dictations: every failure is recoverable (FR-9.3)

### 7.3 Resource use (NFR-3)
- Negligible CPU while Ready, microphone released, and a small memory footprint appropriate for an always-running tray app

### 7.4 Compatibility tiers (NFR-4)
- **Tier 1 (MUST):** Chrome and Edge web text fields, including ChatGPT, Gmail web, and Google search; Notepad; Microsoft Word; Outlook; VS Code; Telegram Desktop; WhatsApp Desktop; Windows Start/Search box
- **Tier 2 (SHOULD):** Firefox; Slack, Teams, and Discord; JetBrains IDEs; Windows Terminal; File Explorer rename and address bar; common Electron apps
- **Elevated apps (MUST, D18):** any Tier 1/Tier 2 app running as administrator, for example an elevated terminal, an elevated IDE, or admin tools with text fields
- **Tier 3 (best effort, documented):** Remote Desktop/VM/Citrix, custom-drawn editors, full-screen games
- **Not supported:** UAC secure desktop, lock screen. Password fields are supported only through the per-dictation override (FR-3.7).

### 7.5 Accessibility and UI (NFR-5)
- Status is not shown by color alone, controls work from the keyboard, and the settings screens work with a screen reader
- The app UI is in English (D10). Any Persian text the app displays (live preview, last dictation, history) must render right-to-left correctly, with bidirectional mixed text in the right order, using a quality Persian font (for example Vazirmatn).

---

## 8. Success Metrics and Acceptance Tests

### 8.1 Persian / mixed-language evaluation corpus (post-build acceptance, D22)
At least 150 recorded utterances from the target user(s), across these categories:
- Colloquial Persian
- Formal Persian
- Mixed Persian-English technical (AI, software, IT)
- Pure English
- Names and product names
- Numbers, dates, and versions
- Emails and URLs
- Noisy environment
- Bluetooth microphone
- "Instruction-like" utterances (for FR-4.3)
- Silence and noise-only clips (for FR-4.4)

### 8.2 Quality targets (proposed)
| Metric | Target |
|---|---|
| Edit-free rate: output needs no manual correction (human judged) | Mixed technical ≥ 80%, pure Persian ≥ 85%, English ≥ 90% |
| Technical terms kept in Latin script, correct spelling | ≥ 95% |
| Translation incidents | 0 |
| Answering/following spoken instructions | 0 |
| Hallucinated text on silence or noise | 0 |
| ZWNJ and ی/ک correctness | ≥ 98% of applicable tokens |
| Colloquial/Formal setting obeyed | ≥ 95% |

### 8.3 Experience targets
- In a usability session, a first-time user completes onboarding and a first successful dictation in **≤ 3 minutes**
- For the core loop on Tier-1 apps, the user needs no app switching, no copying, and no manual pasting

---

## 9. Changes vs. the Original Brief (summary)
- Hotkey behavior split into hold-to-talk and toggle; cancel, accidental-press, and conflict handling added
- Floating control gets a strict *no focus stealing* rule; "disabled" redefined so the listening indicator still shows
- Insertion modes defined (after stop by default, streaming optional) with rules against rewriting text already inserted
- Rules added for target locking, focus changes, and recovery; the app never submits or presses Enter
- Language modes defined; "never translate" and "transcribe, never respond" made explicit
- Persian output rules made testable: ی/ک, ZWNJ, punctuation, colloquial/formal, digits, loanwords
- State list consolidated and extended with quota, authentication, microphone permission, busy, no-speech, and not-inserted states
- Microphone fallback, disconnect, and permission handling added
- Retry without re-speaking; local history as a setting
- Privacy: free-tier data-use disclosure, lock/sleep behavior, key handling, no telemetry, microphone released while Ready
- Measurable latency, reliability, and quality targets; app compatibility tiers; evaluation corpus
- Default engine set to Gemini 3.5 Transcribe Live, with swappable engine profiles, capability-aware features, an optional refinement stage, and engine benchmarking (FR-13, §5)

---

## 10. Post-Build Validation (D22)

Pre-build engine testing was skipped by decision. These tasks run against the finished MVP.

### Accepted risks of building before testing
- **R1. Mixed-language quality:** mixing Persian and English inside a sentence may be weaker than required. *Mitigation:* the engine can be swapped (FR-13).
- **R2. Free-tier limits** may not cover ~2,000 words/day. *Mitigation:* a clear quota error, retry, and switching to a paid key or another profile.
- **R3. Formal Persian, ZWNJ, and English-term rules** may not be met by the engine alone. *Mitigation:*
  - The MVP must be built so a text-refinement stage (FR-13.5) can be added **without redesign**.
  - Mechanical rules (Latin digits D14, ی/ک FR-5.6, ZWNJ before Persian suffixes on English words D13) are applied deterministically to every output regardless of engine.
- **R4. First-syllable clipping and latency** are unmeasured. *Mitigation:* measured in acceptance; the NFR-1 targets stand.

### Tasks
1. **Engine validation (Gemini 3.5 Transcribe Live):** on the corpus, measure free-tier rate limits for your project, mixing languages inside one sentence, Persian orthography (ZWNJ, ی/ک), keeping technical terms in Latin script, Smart vs. non-Smart cleanup, first-syllable clipping, and latency. Decide whether a refinement stage is needed (§5, FR-13.5).
2. **Build the evaluation corpus** (§8.1) and the scoring rubric. *Written:* [evaluation-corpus.md](evaluation-corpus.md). Record it once the MVP is ready.
3. **App compatibility test plan** for the Tier 1–3 apps (§7.4) using the edge cases in §6. *Written:* [app-compatibility-test-plan.md](app-compatibility-test-plan.md). It runs against the first build.
4. **Decide Q12** from the task 1 results using the decision table in [evaluation-corpus.md §6](evaluation-corpus.md).

---

## 11. Open Questions
*Resolved:*
- Q1 → D10: English UI
- Q2 → D11: no custom vocabulary
- Q3 → D12: original English terms
- Q4 → D13: ZWNJ suffix
- Q5 → D14: Latin digits
- Q6 → D15: ~2,000 words/day
- Q7 → D16: no spoken commands
- Q8 → D17: no history
- Q9 → D18: elevated apps supported
- Q10 → D19: personal use
- Q11 → D21: deferred
- Q13 → D20: free tier

- Q14 → D7: password fields blocked by default, with a per-dictation override

- **Q12. Second model for cleanup — decided during post-build acceptance (§10, task 4).** Until then the MVP runs on the engine alone, plus the deterministic rules in R3. Background: a speech-to-text model writes down *what you said*. Turning colloquial into formal Persian ("نمی‌دونم" → "نمی‌دانم") is *rewriting*, which it may not do. The same may apply to half-spaces and keeping English terms in English. If Gemini 3.5 Transcribe Live can't do these reliably, the options are:
  - **(a)** Add a second, general text model that rewrites the transcript. Costs about 0.5–1.5 s extra per dictation, uses more free-tier quota, and adds the risk that it changes or "answers" the text (FR-13.6).
  - **(b)** Drop Formal mode from the MVP and ship Colloquial only, with no extra model.
  - **(c)** Use the second model only when Formal mode is on, so Colloquial stays fast.

---

## Verification (of this requirements document)
- Walk through each FR with the user; each MUST gets confirmed, downgraded, or removed.
- Every MUST has a testable acceptance condition: FR IDs map to §6 edge cases and the §8 metrics.
- All questions except Q12 are resolved (Q12 follows post-build testing); the PRD is frozen as v1.1.
- Architecture and implementation may start (D22). The §10 tasks are the MVP acceptance gate.

## Sources (engine facts in §5, checked September 2026)
- [Gemini 3.5 Transcribe model page](https://ai.google.dev/gemini-api/docs/models/gemini-3.5-transcribe)
- [Live transcription with the Gemini Live API](https://ai.google.dev/gemini-api/docs/live-api/live-transcribe)
- [Gemini Developer API pricing](https://ai.google.dev/gemini-api/docs/pricing)
- [Gemini API rate limits](https://ai.google.dev/gemini-api/docs/rate-limits)
- [Google blog: Intelligent transcription with Gemini 3.5 Transcribe](https://blog.google/innovation-and-ai/models-and-research/gemini-models/gemini-3-5-transcribe/)
