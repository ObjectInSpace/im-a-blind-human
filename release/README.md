# I'm a Blind Human — accessibility mod for *No, I'm not a Human*

A screen reader accessibility mod. Game text (dialogue, menus, prompts, inspection
signs, status) is spoken directly to your screen reader via UniversalSpeech, with a
Windows SAPI fallback if no screen reader is running. Menus, the room photo, fridge,
radio, and phone are navigable with the arrow keys and Enter.

## Requirements

- *No, I'm not a Human* (Steam, 64-bit)
- [MelonLoader](https://github.com/LavaGang/MelonLoader/releases/latest) v0.7+ installed into the game
- A screen reader (NVDA, JAWS, or Narrator) — or none, and it falls back to Windows SAPI

## Installation

This zip is laid out to mirror your game folder, so the simplest install is to extract
it straight into the game directory and let the files land in place.

1. Install MelonLoader into the game first (run MelonLoader's installer, point it at
   `NoImNotAHuman.exe`). This is a one-time step and is **not** bundled here — get it
   from the link above.
2. Extract this zip into your game folder
   (`...\steamapps\common\No, I'm not a Human\`), keeping the folder structure. That
   places:
   - `Mods\NoImNotAHumanAccess.dll` and `Mods\UnityAccessibilityLib.dll`
   - `UniversalSpeech.dll`, `nvdaControllerClient.dll`, `ZDSRAPI.dll` in the game root
     (next to `NoImNotAHuman.exe`)
3. Launch the game. You should hear "I'm a Blind Human accessibility mod loaded."

If you'd rather copy by hand: put the two DLLs from `Mods\` into the game's `Mods\`
folder, and the three native DLLs from the zip root into the game root next to the exe.

> The native speech DLLs **must** sit in the game root next to the exe — UniversalSpeech
> loads `nvdaControllerClient.dll` from there for NVDA. If they're missing or misplaced,
> speech silently falls back to SAPI (or goes silent).

## Keyboard

| Key | Action |
| --- | --- |
| Arrow keys / Enter | Navigate menus, room photo, fridge, radio dial, phone pad |
| ` (backtick) | Repeat the last spoken line |
| F7 | Repeat the controls for the current context |
| F9 | Status (day/phase/energy/items, plus any corpses present) — or, in a conversation, the last inspection result; on the phone, your known numbers |
| F8 | Repeat / diagnostic trigger (for bug reports) |

## Screen readers

- **NVDA** — works out of the box; arrows pass through to the game.
- **Narrator / SAPI** — works.
- **JAWS** — speech works via FSAPI. (JAWS arrow handling was not verified in this build;
  if arrows don't navigate menus under JAWS, please file an issue.)

## Licenses

UniversalSpeech and its client DLLs are redistributed under their own licenses — see
`licenses\` in this zip. The mod itself and UnityAccessibilityLib are separate works.
