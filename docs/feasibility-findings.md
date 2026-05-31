# Feasibility Findings (empirical, from decompiled game code)

Source: Cpp2IL (pre-release #21) dummy-DLL export of `GameAssembly.dll` (Unity 6000.3.10f1, IL2CPP metadata v39),
decompiled with ilspycmd into `decompiled/`. Method bodies are stripped (expected for dummydll); signatures,
fields, events, and types are complete — sufficient for signature-based hooking (Harmony) and event subscription.

## Q1: Does the game ship existing accessibility support to build on?

**Answer: No screen-reader/accessibility support exists in game code — but a rich central Subtitle system does.**

- Zero hits in `Assembly-CSharp` for: `Accessibility`, `AssistiveSupport`, `ScreenReader`, `Narrator`,
  `SendAnnouncement`, `TTS`, `TextToSpeech`, `VoiceOver`, `TalkBack`. The `AssistiveSupport`/`SendAnnouncement`/
  `screenReaderStatusOverride` tokens found in `global-metadata.dat` are the **unused Unity engine module**, not
  game code. So there is nothing accessibility-specific to extend.
- The game DOES have a structured, central **subtitle/dialogue text pipeline** — this is the "existing support to
  build on," just framed as subtitles rather than accessibility:
  - `SubtitlesView : MonoBehaviour` with `private RTLTextMeshPro _text;`, **`public void UpdateText(string text)`**,
    `Show()`, `Hide()`, `ShowDialogForTime(string message, TimeSpan time)`.
  - A dialogue-overlay manager interface exposing `ShowSubtitle(string dialogName, Camera, EDialogOverlayType, autoskip)`,
    `ShowSubtitlePopup(string message)`, `ShowSubtitlePopup(EInfoMessageType, float)`, `HideSubtitle()`,
    `IsOpenedSubtitle`, and an **`event Action SubtitleStarted`**.
  - Many `OnDialog(Ended|Hidden|Finished)(bool isDialog, bool isSubtitle)` callbacks across game objects.
  - Speaker attribution available via `characterName` / `characterRaw` fields on dialogue data.

## Q1b: Narrative/dialogue engine

**Yarn Spinner** (open-source, documented) + **Twine**, driven via **Zenject** DI, async via **UniTask**, text via
**RTLTextMeshPro** (TextMeshPro variant). Confirmed assemblies: `YarnSpinner.dll`, `YarnSpinner.Unity.dll`, `Twine.dll`,
`Zenject.dll`, `UniTask.dll`, `RTLTMPro.dll`.

- Yarn hook surface present: `DialogueViewBase.RunLine` / `InterruptLine` / `DismissLine`, `LocalizedLine` with
  `RawText`, `TextWithoutCharacterName`, `CharacterName`, and `GetLocalizedLine`. These are the standard Yarn view
  callbacks — the documented, intended extension point for "do something when a line is presented."

### Two independent clean hook points for dialogue (the bulk of the game)
1. **`SubtitlesView.UpdateText(string)`** — Harmony prefix/postfix → every rendered subtitle line as a clean string.
2. **Yarn `DialogueViewBase.RunLine(LocalizedLine, ...)`** — resolved, localized line + speaker, before typewriter.

No per-frame TMP scraping needed for dialogue.

## Q2: What does the native Unity 6.3 API give us vs. building our own — given we hook text either way?

The hooking work (find text sources, intercept them) is identical for any output channel. The native API's value is
**purely in the output/last-mile layer**. Concretely:

What the native API (`AssistiveSupport`) GIVES us:
- `notificationDispatcher.SendAnnouncement(string)` → routes directly to **Windows Narrator** (and macOS VoiceOver)
  with **no third-party DLL** (no Tolk/UniversalSpeech to ship/maintain). Engine owns the OS bridge.
- `screenReaderStatusOverride` → make output work even if detection is uncertain.
- `AccessibilityHierarchy` + `AccessibilityNode` + `nodeFocusChanged` + `SendScreenChanged`/`SendLayoutChanged` →
  *real* screen-reader navigation of menus (arrow/review cursor through controls with roles), not just fire-and-forget
  speech — **if** we build and sync the node tree to the game's controls.

What the native API does NOT give us (we build regardless of channel):
- Any automatic reading of the game's UI/text. It's scripting-only, agnostic of uGUI/UI Toolkit. The game authored no
  hierarchy, so we get nothing for free.
- Speech queue/interruption/dedup *policy*, verbosity, repeat-last, hotkeys — all ours.
- All the game-specific hooks (subtitles, Yarn, menus, prompts).

What a DIY output stack (UniversalSpeech/Tolk) GIVES us that native doesn't:
- Works on **any** screen reader the player already runs (NVDA/JAWS) with that SR's own speech settings/braille,
  rather than forcing Narrator. Mature, battle-tested from inside IL2CPP mods (it's what the user's other mods use).
- Cost: ship + load a native DLL; it's a separate dependency.

### Bottom line for the decision
- For **dialogue/announcements** (the 80% case here), native `SendAnnouncement` and a DIY UniversalSpeech `Output`
  are **near-equivalent effort** — both are a one-line call behind our own speech wrapper. Native wins on "no extra
  DLL"; DIY wins on "uses the player's existing NVDA/JAWS + braille."
- The native API's **unique** payoff is **Phase 3 menu navigation** via a real `AccessibilityNode` hierarchy — a
  capability UniversalSpeech/Tolk simply cannot provide (they're speak-only). That is the actual reason to prefer
  native, and it's worth building there.
- Because our speech wrapper sits between the hooks and the output call, the channel choice stays **cheap to reverse**
  for the announcement layer. Recommended posture: **build the abstraction; prove native `SendAnnouncement` in Phase 0;
  use native for announcements; reserve the node-hierarchy investment for menus where it's the only option that works.**
  If native `SendAnnouncement` is flaky from IL2CPP, flip the announcement layer to UniversalSpeech with no hook rework.

## Toolchain proven this session
- `Cpp2IL-2022.1.0-pre-release.21-Windows.exe` (at `C:\Users\amock\tools\cpp2il\`) handles metadata v39 / Unity 6000.3.
- `ilspycmd` reads the dummy DLLs. Read-only; game install untouched. No mod loader installed yet.
