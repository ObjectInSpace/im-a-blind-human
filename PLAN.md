# No, I'm Not a Human — Screen Reader Accessibility Mod: Plan

## 1. Game / engine facts (verified)

- **Game:** No, I'm not a Human (publisher Trioskaz). Install: `D:\SteamLibrary\steamapps\Common\No, I'm not a Human`.
- **Engine:** Unity **6000.3.10** (= Unity **6.3 LTS line**), 64-bit.
- **Scripting backend:** **IL2CPP** (`GameAssembly.dll` present; `il2cpp_data/Metadata/global-metadata.dat` ~16 MB). **Not Mono.** This is the key difference from the prior Date Everything mod (Mono/BepInEx5).
- **Storefront / native libs:** Steam (`steam_api64.dll`, `Steamworks.NET`), Unity Burst (`lib_burst_generated.dll`).
- **Content pipeline:** **Addressables** + **Unity Localization** package. String tables ship as asset bundles under `NoImNotAHuman_Data/StreamingAssets/aa/StandaloneWindows64/` for ~18 languages (English present: `localization-string-tables-english(en)_assets_all.bundle`, plus asset tables for voice/sprites).
- **`UnityEngine.AccessibilityModule.dll`** is in the scripting assemblies list — the native accessibility API ships in this build.

## 2. Key research conclusions (verified, not assumed)

1. **Unity 6.3 native screen-reader support is real on Windows (Narrator)** and exposed via `UnityEngine.Accessibility.AssistiveSupport` (assembly `UnityEngine.AccessibilityModule`).
2. **It is scripting-API only — no automatic UI integration.** A game gets *nothing* for free; a developer (or us, the mod) must manually build an `AccessibilityHierarchy` of `AccessibilityNode`s and keep it in sync with the UI. The game almost certainly authored none.
3. **There is a direct announcement path that does NOT require a full hierarchy:** `AssistiveSupport.notificationDispatcher.SendAnnouncement(string)` (interface `IAccessibilityNotificationDispatcher`, also `SendScreenChanged`, `SendLayoutChanged`, `SendPageScrolledAnnouncement`).
4. **`AssistiveSupport.screenReaderStatusOverride`** lets the API function even when Unity can't detect a screen reader — important so our output isn't gated by detection quirks.
5. **Decision (user):** Foundation = **native Unity 6.3 AssistiveSupport**; text strategy = **hook live UI + read resolved Unity Localization strings**.
6. **Feasibility verified empirically (see `docs/feasibility-findings.md`):**
   - **No existing accessibility/screen-reader code in the game** — nothing to extend on that front (confirmed by searching decompiled `Assembly-CSharp`).
   - **But the game has a clean central subtitle/dialogue pipeline** we can build on: `SubtitlesView.UpdateText(string)`, `ShowSubtitle(...)`/`ShowSubtitlePopup(string)`, `event Action SubtitleStarted`, `IsOpenedSubtitle`.
   - **Dialogue engine = Yarn Spinner** (+ Twine, Zenject DI, UniTask async, RTLTextMeshPro). Standard Yarn view hooks present (`DialogueViewBase.RunLine`, `LocalizedLine.{RawText,TextWithoutCharacterName,CharacterName}`).
   - Net: **two independent, clean hook points for dialogue** (the bulk of this narrative game) — no per-frame TMP scraping needed.
   - **Native-vs-DIY conclusion:** for announcements the two are near-equal effort; native's *unique* payoff is Phase 3 menu navigation via a real `AccessibilityNode` hierarchy (speak-only stacks can't do that). Keep a speech abstraction so the announcement channel stays cheap to swap.

### Consequence / risk framing
The native path is the "most correct" output channel but the **newest and least battle-tested from inside an IL2CPP mod**. The hooking work (finding and intercepting text sources) is *identical* regardless of output channel. Therefore we de-risk by ordering the work so output goes through `SendAnnouncement` first (immediate, simple), and the full focusable `AccessibilityNode` hierarchy is layered on afterward where it adds value (menu navigation). **Escape hatch:** if native output proves unworkable from the IL2CPP mod, the same hook layer can be repointed at UniversalSpeech/Tolk with no rework of the game-hooking code — so that choice stays cheap to reverse.

## 3. Toolchain (all locally available)

- **Mod loader:** **MelonLoader** (installer at `C:\Users\amock\Downloads\MelonLoader.Installer.exe`). Best IL2CPP support; on first run it invokes **Cpp2IL** to produce managed proxy assemblies + **Il2CppInterop** to generate interop assemblies we compile against. *(Note: user picked the native API as the output channel, not the loader — MelonLoader is still the loader/injector that gets our C# into the IL2CPP process. We do NOT use UnityAccessibilityLib for output, but MelonLoader remains the injection host.)*
- **Decompiler / reader:** `ilspycmd` (installed, `~/.dotnet/tools/ilspycmd`) to read the Cpp2IL-generated assemblies into C#.
- **Asset / localization export:** **AssetRipper** (`D:\Root\AssetRipper`, also `AssetRipper_win_x64.zip`) and **AssetsTools/UABE** (`D:\Root\64bit\AssetBundleExtractor.exe`) for inspecting the Localization string-table bundles directly if needed.
- **Build:** .NET SDK present (multiple `.dotnet` dirs in prior workspace).

## 4. Workspace

- New, separate workspace: `C:\Users\amock\No Im Not A Human Access\No Im Not A Human Access\` (git initialized), matching the user's existing convention (cf. `Killer Frequency Access`). **Independent of the Date Everything `mod template` repo.**
- The stray `I'm a blind human/` ASP.NET console folder in the old workspace is unrelated boilerplate — leave for user to delete (it's in the other repo; not touching without ask).

## 5. Phased plan

### Phase 0 — Bootstrap & verify the stack (proof before features)
0.1 Install MelonLoader against `NoImNotAHuman.exe`; run the game once so Cpp2IL/Il2CppInterop generate `MelonLoader/Il2CppAssemblies/`.
0.2 Create the mod skeleton: `csproj` targeting **net6.0**, referencing the generated `Il2Cpp*` interop DLLs + `UnityEngine.AccessibilityModule.dll`, with a `MelonMod` entry point.
0.3 **Smoke test the native API end-to-end:** on a hotkey, set `screenReaderStatusOverride` and call `AssistiveSupport.notificationDispatcher.SendAnnouncement("accessibility mod loaded")`. Confirm Windows Narrator speaks it. **This is the make-or-break gate for the native approach** — if it fails from IL2CPP, fall back to UniversalSpeech/Tolk (escape hatch above) before building features.

### Phase 1 — Decompile & map text sources
1.1 Run Cpp2IL output through `ilspycmd` into `decompiled/`.
1.2 Identify the game's UI / text architecture: TMP text components, dialogue/VN system, menu controllers, and how they pull from Unity Localization (`LocalizedString`, `StringTable`, `LocalizeStringEvent`). Document in `docs/game-api.md`.
1.3 Inventory text-bearing surfaces in priority order (this is a narrative horror VN-style game, so dialogue/narration dominates): **(a) main menu & settings, (b) core dialogue/narration text, (c) choices/prompts, (d) HUD/interaction prompts, (e) notifications/popups.**
1.4 Map input: existing key/controller bindings (so mod hotkeys don't collide), and how focus/selection moves in menus.

### Phase 2 — Announcement layer (broad coverage, low risk)
2.1 Build a central `SpeechOutput` wrapper over `SendAnnouncement` (dedup, interrupt/queue policy, repeat-last hotkey) — mirrors the role `ScreenReader.cs`/`SpeechRouter.cs` played in prior mods, but emitting through the native dispatcher.
2.2 Hook dialogue/narration text: when the game sets a localized line, speak it. Read the **resolved localized string** (post-Localization) so language follows the game's setting.
2.3 Hook menu/selection changes: speak focused control label + state.
2.4 Hook prompts, notifications, and interaction text.
2.5 All mod-authored strings go through a `Loc` table from day one (project rule), separate from the game's own localized content which we pass through verbatim.

### Phase 3 — Native hierarchy for navigable menus (where it earns its keep)
3.1 For menu screens, construct an `AccessibilityHierarchy` of `AccessibilityNode`s mapped to the real interactive controls, wire `nodeFocusChanged` ↔ the game's keyboard/controller focus, and use `SendScreenChanged`/`SendLayoutChanged` on screen transitions. This gives proper screen-reader navigation (review/arrow through controls) rather than fire-and-forget announcements.
3.2 Keep gameplay/dialogue on the announcement layer (Phase 2) where a node tree adds little.

### Phase 4 — Gameplay completeness pass
4.1 Identify any non-text accessibility gaps (timed events, QTEs, spatial/visual puzzles, anything requiring sight) and design audio/announce substitutes — match the sighted experience, cheats only if unavoidable (project principle).
4.2 Config (volume/verbosity/hotkeys), README, packaging.

> Concrete, prioritized expansion of 4.1 grounded in verified surfaces: **`docs/world-accessibility-roadmap.md`**
> (room/presence orientation → status key → spatial awareness → navigation probe → threat layer).

## 6. Open questions deferred to implementation
- Exact dialogue/VN driver class names (resolved in Phase 1 decompile).
- Whether native `SendAnnouncement` interrupts or queues, and whether we need our own queue.
- Controller-glyph readout (a known hard problem from the prior project) — only if this game shows glyphs.

## 7. First concrete steps on approval
1. Set up the workspace scaffolding (AGENTS.md/CLAUDE.md, `docs/`, `decompiled/`, .gitignore, `project_status.md`).
2. Run MelonLoader installer against the game; launch once to generate interop assemblies.
3. Build the minimal `MelonMod` + Phase 0.3 `SendAnnouncement` smoke test and confirm Narrator speaks.
