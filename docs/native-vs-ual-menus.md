# Native AssistiveSupport vs. UnityAccessibilityLib — cost/reward, focused on UI/menu narration

Framing (corrected): the choice is **native Unity 6.3 `AssistiveSupport`** vs. **UnityAccessibilityLib (UAL)**
(the AccessMods library). Both sit *behind* the same game hooks. UAL is a **speak-only** stack: it wraps
UniversalSpeech/SAPI and exposes `Output(speaker, text, type)` — it sends strings to whatever screen reader is
running. It has no concept of focus, controls, or a navigable model. Native adds, on top of speaking strings, an
**accessibility object model** the OS screen reader can navigate.

## This game's UI model (verified from decompiled code)
- Standard **uGUI `EventSystem`** focus is in use: `UISelectable`, `HoverableButton`, `PhoneButtonView` implement
  `ISelectHandler`/`IDeselectHandler` with `OnSelect(BaseEventData)` / `OnDeselect(...)`. So
  `EventSystem.current.currentSelectedGameObject` is meaningful and focus changes are observable.
- Input is the **Unity Input System** (`PlayerInputActions` with a `UI` map). Menu nav = directional `Navigate`.
- Settings are a structured tree: `ASettingsInstance` → `TextSettingsButtonsInstance`, `TextSettingsDropdownInstance`,
  slider instances (`SoundSettingsVolumeSlider`, `FakeSlider`), so a focused row's label + current value is reachable.
- Some menus are bespoke (`MainMenuSignLineElement` laid out in a circle by `MainMenuInCircleElementMover`,
  `ScrollableDropdown`, `FakeSlider`) — not plain `Slider`/`Button`, so role/value must be derived per control type.

Conclusion: a focus-driven approach is viable here because the game already drives selection through uGUI's
EventSystem. We don't have to invent a focus model; we observe the game's.

## What each approach delivers for menus

### UnityAccessibilityLib (speak-only)
We hook `OnSelect`/selection-changed, build a string ("Master Volume, 80 percent, slider"), and `Output(...)` it.
- Pros: dead simple; identical effort to the dialogue path; uses the player's own NVDA/JAWS (their voice, rate,
  punctuation, **braille** output); battle-tested from IL2CPP mods.
- Cons — the experience is **fire-and-forget speech, not a navigable UI**:
  - No screen-reader review cursor. The user can't use NVDA's object navigation / "read current control" /
    "where am I" — there's no object there, just past speech. If they miss/zone out on an utterance, the only
    recovery is a mod "repeat last" hotkey we build.
  - No roles/states surfaced to the SR. "Is this a checkbox? checked? a tab? which of how many?" only exists if we
    bake it into the spoken string; the SR can't announce it in its own conventions.
  - No SR-driven navigation conventions (e.g. NVDA form-field navigation, "first/last item", count "3 of 7") unless
    we hand-author every one of those into strings.
  - We must replicate, in strings, things SRs already do: position-in-list, level, expanded/collapsed, value ranges.

### Native AssistiveSupport (object model + speech)
We build an `AccessibilityHierarchy` of `AccessibilityNode`s mapped to the live controls (label, `AccessibilityRole`
— button/toggle/slider/tab/dropdown — value, state), sync `nodeFocusChanged` ↔ the EventSystem selection, and call
`SendScreenChanged`/`SendLayoutChanged` on screen transitions.
- Pros — a **real navigable UI** through Windows Narrator:
  - Narrator's own review/navigation works: move by control, re-read the focused item, query role/value/state on
    demand — because there are actual objects, not just emitted speech.
  - Roles/states are spoken in the **SR's own conventions and language**, consistent with every other app the user
    knows. "Slider, 80%", "tab 2 of 4", "checkbox, checked" come for free from the role/value/state we set.
  - Screen-change semantics: Narrator can announce the new screen and move focus correctly on transitions.
  - No third-party DLL to ship; the engine owns the OS bridge.
- Cons — real added cost and risk:
  - **More plumbing**: construct/teardown the node tree per screen, keep node geometry/labels/values in sync as the
    UI changes, map each bespoke control (sign-line item, FakeSlider, ScrollableDropdown) to a role+value, and bridge
    `nodeFocusChanged` both directions with the EventSystem.
  - **Windows = Narrator.** Native desktop output targets Narrator/VoiceOver. A player who lives in NVDA/JAWS would
    get Narrator for the menu-navigation experience specifically — different from their daily driver. (Plain
    announcements can still go wherever; it's the *navigable hierarchy* that's Narrator-bound on desktop.)
  - **Newest path**: building/syncing a hierarchy from inside an IL2CPP mod is the least-proven thing we'd do; bugs
    here are subtler than "string didn't speak."
  - **No braille story** as clean as a mainstream SR's, depending on Narrator's braille maturity for app-provided
    hierarchies.

## Cost / reward summary
- **Dialogue/announcements:** native and UAL are ~equal effort and reward. Either is fine; native avoids a DLL, UAL
  uses the player's SR. This is *not* where the decision lives.
- **Menus/UI narration — the deciding axis:**
  - UAL gives you **good-enough spoken menus quickly**, but it's speech, not a navigable interface; every
    list-position/role/state nicety is hand-built in strings, and there's no review cursor.
  - Native gives you a **genuinely navigable menu** in Narrator's own idiom (roles, values, review, screen changes)
    — the thing speak-only stacks structurally cannot provide — at the cost of real hierarchy-sync plumbing and a
    Narrator-on-Windows assumption, on the newest/least-proven API.

## Recommendation (hybrid; keeps the reward, caps the risk)
1. **Speech abstraction `ISpeechOutput`** with two implementations: `NativeAnnouncer` (SendAnnouncement) and
   `UalAnnouncer` (UniversalSpeech). Selectable in config. All dialogue/announcements go through it → channel is
   cheap to switch and we can ship value before committing to a hierarchy.
2. **Dialogue + simple popups:** announcement layer (either channel). Fast, low risk, covers the bulk of the game.
3. **Menus/settings:** build the **native AccessibilityNode hierarchy** — this is where native earns its keep and is
   the only way to get true navigation. Gate it behind a Phase-0 proof that hierarchy + `nodeFocusChanged` actually
   drive Narrator from this IL2CPP build.
4. If the native hierarchy proves too unstable from IL2CPP, **fall back to UAL-style spoken menus** with hand-built
   position/role/state strings via the same `ISpeechOutput` — degraded but functional, no rework of the hooks.

Net: native is worth the extra menu cost specifically *because* of navigable UI narration; UAL would leave menus as
narrated-but-not-navigable. Use native for menus, keep the speech layer abstracted so the cheap parts stay portable
and the risky part has a fallback.
