# World accessibility roadmap (post-dialogue/menu)

Expands `PLAN.md` Phase 4 ("gameplay completeness pass") into a concrete, prioritized roadmap, grounded in surfaces
verified by decompile investigation (2026-06-01). Dialogue, menus, and the interaction prompt are DONE; this is what
remains to make the game *playable* blind, not just *perceivable*.

## Framing: reactive vs. proactive

Everything shipped so far is **reactive** — the game pushes an event (a line, a focused control, a "press E"
prompt) and we speak it. A sighted player also has two **proactive** capabilities we haven't touched:

- **Orientation** — at a glance they know which room they're in, who/what is present, and the resource state on the
  HUD. A blind player currently has none of this unless an event happens to mention it.
- **Agency** — they can see a target across the room and walk to it. A blind player can only discover interactables
  by sweeping the camera until the raycast happens to cross one (how "view entrance" was found).

The roadmap fills those two gaps. Genre note: this is a resource-management horror game, so orientation/resource
state and threat awareness carry unusual weight.

## Verified scaffolding (what the game hands us)

From the movement-model + world-HUD investigations (see `project_status.md` and the memory files):

- **Free-roam first-person**: `PlayerController` (WASD + mouse-look + crouch + run). Not node/click-based — so
  spatial features are meaningful, not redundant.
- **`IPlayerService`**: `Vector3 Position`, `Vector3 LookDirection`, `bool IsInRoom`, `TeleportTo(StartPoint)`,
  `UniTask MoveXZ(Vector3, speed)`, `MakeMovable/MakeImmovable`, `SetRunAvailability`. Pose is queryable every
  frame; the engine itself can drive the player to a position.
- **Native room model**: `ARoom`/`IRoom` + `RoomsManager` (`ITickable`). Character-aware — each room tracks
  `CharactersInside` / `AliveCharactersInside` / `KilledCharacters` and links an `AActionableObjectView`.
- **HUD resource state via `IHUDPresenter`**: `GetDayActions` / `GetMaxDayActions` (events), actions-count active
  state, gun shown/hidden, `EHUDAnimation`, day/night via `IDayNightController`.
- **Interactable set**: `AInteractableObject` subclasses, each with a `RaycastTargetHint` (subject/action/icon);
  `ActionableObjectsManager` holds the `IActionableObjectView[]`. `RaycastSource.Target` = current look target.
- **Threat audio already modeled**: `PlayerBodyeaterstepSoundPlayer` (a distinct footstep player for the body-eater).

## Prioritized features

### A. Room + presence orientation  — RECOMMENDED FIRST (highest value-per-effort, zero movement risk)
Speak the room on entry and who is in it. Pure read off the native room model; no teleport/move dependency.
- On `IsInRoom` / room-change (hook `RoomsManager` or poll the active `ARoom`): announce room name +
  `AliveCharactersInside` ("Kitchen. Maggie is here.").
- Open Qs: does a room expose a localized display name, or do we map an `ERoom`-style enum like we map
  `EInfoMessageType`? How is "current room" surfaced — an event, or poll `IPlayerService.IsInRoom` + nearest room?

### B. Status key (day / actions / energy)
A keypress that speaks the resource state the HUD shows persistently and a blind player can't see.
- Source: `IHUDPresenter.GetDayActions/GetMaxDayActions`, day/phase from `IDayNightController`, energy/consumables
  from `IConsumablesController` (seen on interactables). "Day 3, night. 2 of 5 actions left."
- Cheap, pure read, no movement risk. Natural companion to A.

### C. Spatial "what's around me"
Enumerate or cycle nearby `AInteractableObject`s with bearing + distance computed from `Position`/`LookDirection`,
so the player can find things without blind camera-sweeping. This is the NIMNAH analogue of the Date Everything
object tracker/picker (reuse that design, not the code — different engine/coords).
- Read-only (no teleport needed): describe direction ("ahead", "to your left") + rough distance + the prompt's
  subject. Optionally a proximity tone like the Date Everything tracker.
- Open Q: filter to the current room (the room model makes this clean) vs. a fixed radius.

### D. Navigation ("go to X")  — investigate primitives first
Free-roam means reaching things is real work for a blind player. BUT the game exposes `TeleportTo(StartPoint)` and
`MoveXZ(Vector3, speed)` — potentially a *much* cheaper path than porting the Date Everything bake + autowalk stack.
- MUST PROBE FIRST: are `TeleportTo`/`MoveXZ` usable for player-initiated movement (respect collision, don't fight
  the input controller, not cutscene-only)? A small keypress experiment settles this before any design.
- If usable: "go to" a room `StartPoint` or an interactable's stand position is a thin wrapper. If not: decide
  whether free-roam autowalk is worth a Date-Everything-scale effort, or whether C (orientation) is enough.

### E. Threat / horror layer  — genre-critical, partly rides notifications
Knowing when something dangerous is present is core to a horror game and currently invisible.
- `PlayerBodyeaterstepSoundPlayer` suggests the body-eater already has distinct audio — confirm it's audible/legible
  without sight, or add a cue. The murder mechanic + `EInfoMessageType.{SomebodyWasMurdered,BodyEaterVisit}` ride
  the deferred notification path. Revisit after notifications are settled.

## Dependencies / sequencing
- A and B are independent, cheap, read-only — do them first, in either order.
- C depends on nothing but benefits from A (room-scoped enumeration).
- D is gated on the teleport/move probe; don't design around it until proven.
- E is partly gated on the deferred notification investigation.

## Deferred (tracked elsewhere)
- **Notifications / state popups** — `DialogManager.ShowSubtitlePopup(EInfoMessageType)`; may already speak via the
  existing `UpdateText` hook (confirm by ear). Deferred by user 2026-06-01.
- **Energy-cost suffix on prompts** — DONE (built 2026-06-01), pending ear-confirm on first energy-costing action.
