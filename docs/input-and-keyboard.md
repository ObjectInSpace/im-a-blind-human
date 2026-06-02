# Keyboard / input model (verified from decompiled bindings)

Input stack: **Unity Input System** (`PlayerInputActions`) with an **`InputSystemUIInputModule`** bridging UI actions
to the uGUI **EventSystem**. `moveRepeatDelay` present (held-direction repeat). Game manages focus explicitly:
`SelectFirstObject()` and `_firstSelectedObject` (menus set an initial selection on open).

## Action maps
- `WorldActions`: Move, Look, Crouch, Interact, Pause, Run (gameplay).
- `UIActions`: **Navigate, Submit, Select, Cancel, ChangeTab, Scroll**, Point (mouse), DialogSkip, Exit, PauseExit,
  Tutor, RadioKnob/LeftHandle/RightHandle, LMB, SkipVideo, SpeedUp(+Trigger).

## Keyboard bindings present (from binding paths in metadata)
- Navigate: **arrow keys** (`up`/`down`/`left`/`right`) AND **WASD** (2D-vector composite).
- Submit/Select: **`enter`**, **`space`**.
- Cancel/Exit/back: **`escape`**.
- Tabs: **`tab`** (ChangeTab). Lists/scroll: **`page`** (PageUp/Down), **`home`**, **`end`**, plus Scroll.
- Other: `q`, `e`, `f`, `shift`, `backspace` for context actions.

## What this means for a screen-reader user
The game already has **full keyboard menu navigation using standard conventions**:
- **Arrow keys** move selection, **Enter/Space** activate, **Escape** backs out, **Tab** changes tab.
- Because navigation runs through `InputSystemUIInputModule` → EventSystem, each move changes
  `currentSelectedGameObject` and fires `ISelectHandler.OnSelect` / `OnDeselect` on the controls
  (`UISelectable`, `HoverableButton`, `PhoneButtonView`).

So a SR user does **not** need new movement keys from us — they use the game's own arrows/Enter/Escape/Tab. Our job is
to make the *currently selected* control perceivable:
- **Native plan:** map each selected control to an `AccessibilityNode` (label + `AccessibilityRole` + value + state)
  and drive `nodeFocusChanged` from the EventSystem selection. Then Narrator announces focus changes as the user
  arrows around, and Narrator's review cursor can re-read/query the focused control on demand. The user navigates with
  the *game's* keys; the screen reader narrates and lets them review.
- The only gaps to watch: (1) menus/controls that change selection WITHOUT a uGUI select event (bespoke
  `MainMenuSignLineElement` circle menu, `ScrollableDropdown`, `FakeSlider`) may need a per-control focus shim;
  (2) ensure focus is set on menu open (game already calls `SelectFirstObject()`), else Narrator has nothing to land
  on; (3) mouse-only / hover affordances need a keyboard/selection equivalent if any exist.

## Caveat on completeness
Binding paths are read from metadata strings, so the *set* of keys is reliable but exact composite groupings per
action are confirmed at runtime. Gameplay (first-person horror movement, interaction, any timed/spatial sequences) is
a separate accessibility problem from menus and is handled in the later gameplay-completeness pass.

## Popups / message windows (mod note, 2026-06-02)
Selecting an object in a 2D room photo can raise a `PopupWindow` (a message with one or more buttons) that must be
dismissed before continuing. On controller, **Square** dismisses it; on keyboard, **Space** dismisses it (Enter does
*not*). The mod does **not** intercept popups — the user presses **Space** to dismiss. (An earlier mod-side popup
stepper was built and then removed once Space was found to work natively, so the popup is left entirely to the game.)
