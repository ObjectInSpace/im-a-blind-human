# I'm a Blind Human

A screen reader accessibility mod for the horror game **[No, I'm not a Human](https://store.steampowered.com/app/2371310/No_Im_not_a_Human/)** (Trioskaz).

The game is a narrative, visually-driven experience with no built-in screen reader support. This mod speaks the game's text — dialogue, menus, prompts, inspection signs, and a status readout — directly to your screen reader, and makes the mouse-and-look interactions (menus, the room photo, the fridge, the radio dial, the phone) navigable from the keyboard.

Blind and low-vision players can play with NVDA, JAWS, or Windows Narrator running. With no screen reader running, speech falls back to Windows SAPI.

## Requirements

- **No, I'm not a Human** (Steam, Windows 64-bit)
- **[MelonLoader](https://github.com/LavaGang/MelonLoader/releases/latest)** v0.7+ installed into the game
- A screen reader (NVDA, JAWS, or Narrator) — optional; without one, speech uses Windows SAPI

## Installation

1. Install **MelonLoader** into the game (run its installer and point it at `NoImNotAHuman.exe`). One-time step; not bundled here.
2. Download the latest release zip from the [Releases page](https://github.com/ObjectInSpace/im-a-blind-human/releases).
3. Extract the zip into your game folder (`...\steamapps\common\No, I'm not a Human\`), keeping the folder structure. This places:
   - `Mods\NoImNotAHumanAccess.dll` and `Mods\UnityAccessibilityLib.dll`
   - `UniversalSpeech.dll`, `nvdaControllerClient.dll`, `ZDSRAPI.dll` in the **game root** (next to `NoImNotAHuman.exe`)
4. Launch the game. You should hear *"I'm a Blind Human accessibility mod loaded."*

> **The native speech DLLs must sit in the game root next to the exe.** UniversalSpeech loads `nvdaControllerClient.dll` from there for NVDA. If they're missing or misplaced, speech silently falls back to SAPI — or goes silent.

## Keyboard

| Key | Action |
| --- | --- |
| Arrow keys / Enter | Navigate menus, the room photo, fridge, radio dial, and phone pad |
| `` ` `` (backtick) | Repeat the last spoken line |
| F7 | Read the controls available in the current context ("what can I do here") |
| F8 | Repeat / diagnostic trigger (also used for bug reports) |
| F9 | Status readout — day/phase/energy/items and any corpses present; in a conversation, the last inspection result; on the phone, your known numbers |

The mod claims the arrow keys and Enter **only** in the four contexts where the game itself doesn't use them (room photo, fridge, radio, and 3D scene stepping) — never in the main menu, the pause menu, or dialogue, where the game's own navigation owns those keys.

## Screen readers

- **NVDA** — works out of the box; arrow keys pass through to the game.
- **Narrator / SAPI** — works.
- **JAWS** — speech works via FSAPI. JAWS swallows arrow keys, so menu navigation under JAWS needs the separate pass-through script (see `jaws/`). If arrows don't navigate for you, please file an issue.

## How it works

The game runs on Unity 6.3 with the **IL2CPP** scripting backend. The mod is a **[MelonLoader](https://github.com/LavaGang/MelonLoader/)** mod (MelonLoader injects it into the IL2CPP process). Speech is emitted through **[UnityAccessibilityLib](https://www.nuget.org/packages/UnityAccessibilityLib) / UniversalSpeech**, which talks to NVDA/JAWS/SAPI directly rather than through Unity's slower UIA notification channel.

Text is captured by hooking the game's own systems with Harmony:

- **Dialogue** is a Yarn Spinner pipeline with a central subtitle sink; a hook there speaks each rendered line, expanding localization placeholders and reading the resolved localized string so speech follows the game's language setting.
- **Menus** are narrated by tracking uGUI focus and speaking the focused control's label and state.
- **World interactions** (room photo, fridge, radio, phone, inspection signs, corpses, HUD/status) each have a dedicated narrator that reads the relevant localized game strings.

Mod-authored strings pass through a small localization table; the game's own content is passed through verbatim in whatever language the game is set to.

## Building from source

Requires the .NET 6 SDK and a MelonLoader-patched copy of the game (running the game once after installing MelonLoader generates the IL2CPP interop assemblies the build references).

```sh
dotnet build -c Release
```

By default the build references the game at `D:\SteamLibrary\steamapps\Common\No, I'm not a Human` and, if a `Mods` folder is present there, **deploys the built DLL straight into the game** so changes can be tested immediately. Override the game location if your install differs:

```sh
dotnet build -c Release -p:GameDir="C:\Path\To\No, I'm not a Human"
```

The native UniversalSpeech x64 DLLs are not on NuGet; place them in `libs\` (from <http://vrac.quentinc.net/UniversalSpeech.zip>, `bin/64/`) so the deploy target can ship them into the game root. See [`NoImNotAHumanAccess.csproj`](NoImNotAHumanAccess.csproj) for the full reference and deploy setup.

## Project layout

| Path | Contents |
| --- | --- |
| `src/` | Mod source — the only code that is compiled |
| `src/Dialogue/` | Yarn Spinner subtitle/dialogue hooks |
| `src/Menus/` | Menu focus tracking and control narration |
| `src/World/` | World interaction narrators (photo, fridge, radio, phone, signs, corpses, status, HUD) |
| `src/Speech/` | Speech output abstraction over UniversalSpeech |
| `src/Interop/` | IL2CPP raw-interop helpers |
| `docs/` | Research notes, feasibility findings, and the accessibility roadmap |
| `release/` | Packaging script and staged release zips |

## License

The mod and UnityAccessibilityLib are separate works. UniversalSpeech and its client DLLs are redistributed under their own licenses (included in release zips under `licenses\`).
