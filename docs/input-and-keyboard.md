# Keyboard / input model (verified from the rip)

Input stack: **Unity Input System** (`PlayerInputActions`) with an **`InputSystemUIInputModule`** bridging UI actions
to the uGUI **EventSystem**. `moveRepeatDelay` present (held-direction repeat). Game manages focus explicitly:
`SelectFirstObject()` and `_firstSelectedObject` (menus set an initial selection on open).

Source of truth = the actual asset, NOT metadata strings:
`D:\root\AssetRipper\NINAH\ExportedProject\ExportedProject\Assets\MonoBehaviour\PlayerInputActions.asset`
(2026-06-03 rip). Every action + binding below is transcribed from it. Two control schemes: **Keyboard And Mouse**
and a gamepad scheme the asset literally names **`Gaypad`** (a dev typo; the binding group is `;Gaypad`). The maps are
named **`World`** and **`UI`** (earlier notes said "WorldActions"/"UIActions" — the runtime names are `World`/`UI`).

## `World` map — gameplay
| Action | Keyboard / Mouse | Gamepad |
|---|---|---|
| Move | `W A S D` | left stick |
| Look | mouse delta | right stick |
| Interact | `Space`, `E`, left mouse button | A (south) |
| Run | `Shift` | right trigger |
| Crouch | `Left Ctrl` | right-stick press |
| Pause | `Escape` | Start |

## `UI` map — menus / dialogue / overlays
| Action | Keyboard / Mouse | Gamepad |
|---|---|---|
| Navigate | arrow keys **and** `W A S D` | D-pad, left stick |
| Submit | `Enter` | A (south) |
| Select | left mouse button | A (south) |
| Cancel | `Q` | B (east) |
| Exit (back) | `Q`, `Escape` | B (east), Start |
| PauseExit | `Escape` | Start |
| DialogSkip | `Space`, left mouse button | X (west) |
| SkipVideo | `Space` | A (south) |
| Tutor | `F` | Y (north) |
| ChangeTab | *(none)* | left & right shoulder |
| Scroll | mouse wheel | right stick |
| Point | mouse position | right stick |
| SpeedUp | `Shift` | right trigger |
| LMB | left mouse button | *(none)* |
| RadioKnob | *(none)* | right stick |
| RadioLeftHandle | *(none)* | left shoulder |
| RadioRightHandle | *(none)* | right shoulder |

## What is NOT bound (corrects earlier notes)
The complete keyboard set the game binds is exactly: `space escape w a s d q e f shift leftCtrl enter` + the four
arrow keys. **No F-keys, and no `tab` / `page`(Up/Down) / `home` / `end` / `backspace` / brackets** — earlier
revisions of this doc listed those as game-bound (Tab=ChangeTab, Page/Home/End=scroll), read from metadata strings;
the asset disproves it. ChangeTab, Scroll, RadioKnob and the Radio handles are **gamepad-only** — they have no
keyboard binding at all. This is why the mod safely claims `F7`–`F11`, `PageUp/PageDown`, `Home/End` and `Backspace`
with zero collision.

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
