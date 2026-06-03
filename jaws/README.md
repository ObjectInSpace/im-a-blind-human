# JAWS arrow-key support

## The problem

With **JAWS** running, the arrow keys don't navigate menus or dialogue in *No, I'm not a
Human* — but they work fine under **NVDA**. This is JAWS-specific: JAWS installs a global
keyboard hook and consumes the arrow keys for its own reading commands *before* they reach the
game, so the game's menu/dialogue navigation never sees them. (Free-roam 3D is unaffected,
because there the mod reads the arrows itself.)

No JAWS configuration fixes this. We verified, in-game, that:
- An application keymap can't override JAWS's default arrow bindings (it's additive only).
- The virtual cursor / forms mode don't apply to a game window.
- "Navigation Quick Keys" and "unified keyboard processing" toggles don't release the arrows.
- Sleep mode releases the arrows but **silences the mod's speech** — so it's useless here.

## The fix (built into the mod)

The mod installs its own keyboard hook that sits ahead of JAWS's. When an arrow key is pressed
and the game is focused, it intercepts the key (so JAWS doesn't eat it) and re-sends it to the
game as a raw hardware keystroke — which Unity reads as a genuine press. The game then navigates
itself, exactly as it does under NVDA. **The mod does not interpret the arrows; it only relays
them.** JAWS keeps speaking the focused control the whole time (this is *not* sleep mode).

The relay is **off by default** and is an **opt-in for JAWS users**: the keyboard hook isn't even
installed until you turn it on. It only acts while the game window is in the foreground, so other
applications are never affected.

## Turning it on (JAWS users): press F11

Press **F11** to turn the arrow relay **on**. JAWS announces the state when you press it. Press
F11 again to turn it back off.

Because Unity only reads *raw* input, the relay has to re-inject the arrow keys. A side effect is
that **while the relay is on, JAWS can't interrupt its own speech** — pressing **Ctrl** won't
silence it, and arrowing quickly queues announcements instead of cutting to the latest. This is a
known limitation of co-existing with JAWS's keyboard processing; there is no way around it on this
game while still delivering the arrows.

| State | Arrow keys in menus/dialogue | JAWS Ctrl-to-silence / interrupt |
| --- | --- | --- |
| **Off** (default) | JAWS-captured (don't navigate) | Normal |
| **On** (press F11) | Work | Limited |

So: turn it **on** when you want to navigate menus/dialogue with the arrows; turn it **off** if you
want JAWS's normal interrupt back for a stretch of heavy reading.

## NVDA users

You don't need this. NVDA already passes the arrows through to the game, so menus and dialogue work
out of the box — and because the relay is off by default, the hook is never installed under NVDA
and your normal speech-interrupt behavior is completely untouched. Leave F11 alone.
