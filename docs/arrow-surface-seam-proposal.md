# Proposal: `IArrowSurface` — one seam for arrow-driven surfaces (NO behavior change)

Status: PROPOSAL only (2026-06-03). Nothing below is implemented. Apply only AFTER the fridge/radio/cartoon
steppers are ear-confirmed in-game, so we unify on real behavior, not assumed behavior.

## Why

`AccessMod.OnUpdate` now hooks arrows in four contexts (RoomPhoto, ThreeD, Fridge, Radio) plus a MainMenu Enter
intercept, each a hand-written `case`. They all implement one contract — *traverse available options → announce the
current one → activate on Enter* — but the consistency is maintained by hand, and every new mouse-only surface adds
another `case` + another `Reset()` line. The goal of this seam is to make that contract **structural**: a new surface
plugs in by implementing one small interface, and `OnUpdate` stops growing.

This does NOT reduce mod-driven input — that is unavoidable where the game exposes only `IPointer*` and no
`ISelect`/`ISubmit` (see the mouse-hover-gaps audit). It reduces the ROUTING complexity around it.

## The honest constraint: not all four are the same shape

- RoomPhoto / Fridge / ThreeD are **discrete steppers** (next/prev/activate). Good fit.
- RoomPhoto **defers Enter to the game**; Fridge/ThreeD **activate themselves**. Differing activation.
- **Radio is the outlier**: it is CONTINUOUS (held Left/Right analog sweep via `RotateKnob` every frame) + ambient
  closeness narration + a band toggle on Up/Down. It is NOT "traverse discrete options."

So the seam unifies the three discrete steppers and the MainMenu-Enter case; **Radio stays a special case**. Forcing
Radio under a discrete-stepper interface would require `if (continuous)` escape hatches — more complexity disguised as
less. Keep it explicit.

## The interface

```csharp
namespace NoImNotAHumanAccess.World
{
    /// <summary>An arrow-navigable surface the mod owns because the game exposes no keyboard path for it. The mod
    /// asks the ACTIVE surface to handle a key; the surface decides what (if anything) it does. This keeps the
    /// "arrows traverse, Enter selects" invariant in one place instead of per-case in OnUpdate.</summary>
    public interface IArrowSurface
    {
        /// <summary>The InputContextKind this surface owns (the Classify() result that routes keys here).</summary>
        InputContextKind Context { get; }

        /// <summary>Move the highlight forward (false) or back (true) and announce the now-current option.</summary>
        void Step(bool backwards);

        /// <summary>Activate the current option. Return true if the surface HANDLED activation; false to let the
        /// game's own Enter run (RoomPhoto returns false — the game's native select is correct there).</summary>
        bool Activate();

        /// <summary>Forget any selection/highlight state (called when this surface stops being the active context),
        /// so re-entering it starts fresh.</summary>
        void Reset();
    }
}
```

Notes:
- `Activate()` returning a bool resolves the "defers to native Enter vs handles it itself" split cleanly, with no
  per-surface branch in the caller. RoomPhoto: `return false`. Fridge/ThreeD: do the work, `return true`.
- Up/Down vs Left/Right collapse to one axis here (Step forward/back) — same as today's `next`/`prev`, which already
  fold both arrow pairs. Radio's Up/Down-means-band distinction is exactly why Radio is NOT an `IArrowSurface`.

## How the existing steppers adopt it (thin wrappers, no logic change)

- `TwoDProbe`  → `Step` calls existing `Step(backwards)`; `Activate` returns `false` (native Enter); `Reset` clears `_selected`.
- `ActionMenu` → `Step` = `Cycle(backwards)`; `Activate` = `Activate(); return true`; `Reset` clears `_selected`.
- `FridgeMenu` → `Step` = `Cycle(backwards)`; `Activate` = `Activate(); return true`; `Reset` already exists.
- MainMenu cartoon button is NOT a full surface (arrows stay native; only Enter is intercepted when available). Keep
  it as a tiny explicit check, OR model it as an `IArrowSurface` whose `Step` is a no-op and `Activate` =
  `if (IsAvailable()) { Activate(); return true; } return false;`. Either is fine; the no-op-Step modeling keeps the
  caller uniform.

## What `OnUpdate` collapses to

```csharp
InputContextKind ctx = _inputContext?.Classify() ?? InputContextKind.None;

if (ctx == InputContextKind.MainMenu || ctx == InputContextKind.Pause)
    _uguiFocus?.EnsureSelection();

// Radio stays special-cased: continuous held sweep + closeness narration + band toggle.
if (ctx == InputContextKind.Radio) { /* exactly today's held-tune + Tick + bandToggle block */ }
else _radioMenu?.Reset();

// Discrete arrow surfaces: one lookup, one dispatch. Reset every surface that ISN'T active.
IArrowSurface? active = _surfaces.FirstOrDefault(s => s.Context == ctx);
foreach (var s in _surfaces) if (s != active) s.Reset();   // replaces the per-surface "if (ctx != X) Reset()" lines

if (active != null && (next || prev || enter))
{
    if (next)  active.Step(backwards: false);
    if (prev)  active.Step(backwards: true);
    if (enter) active.Activate();   // surface decides whether it handled it; false = game's Enter still runs
}
```

`_surfaces` is a fixed `IArrowSurface[]` built once in `OnInitializeMelon` ( `{ twoDProbe, actionMenu, fridgeMenu,
cartoonSurface }` ). Adding a fifth mouse-only surface = implement the interface + add it to that array. No new
`case`, no new `Reset()` line, no `OnUpdate` edit.

## Cost / risk

- Pure refactor — behavior identical if the wrappers forward 1:1. Verify by re-running the same ear tests.
- The one judgment call is the MainMenu/cartoon modeling (full surface vs explicit check). Lean to whichever reads
  clearer once the cartoon button is confirmed working.
- Do NOT generalize Radio into the interface. If a SECOND continuous/analog surface ever appears, design an
  `IAnalogSurface` then — not preemptively.

## Decision

Hold until the steppers are ear-confirmed. Then apply this as a no-behavior-change refactor. Tracked as the open
architecture question in `project_status.md`.
