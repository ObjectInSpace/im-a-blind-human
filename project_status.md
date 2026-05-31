# Project Status: No I'm Not a Human Access

## Project Info
- **Game:** No, I'm not a Human (publisher Trioskaz)
- **Engine:** Unity 6000.3.10f1 (Unity 6.3 LTS), 64-bit
- **Scripting backend:** IL2CPP (GameAssembly.dll, metadata v39)
- **Mod loader:** MelonLoader v0.7.3 (net6)
- **Game dir:** `D:\SteamLibrary\steamapps\Common\No, I'm not a Human`
- **User:** blind screen-reader user, experienced accessibility modder. Output: no `|` tables; use lists.

## Approach (decided)
- **Native-or-nothing:** use Unity 6.3 native `AssistiveSupport` for output. If the Phase-0 proof fails,
  fall back to UnityAccessibilityLib (UAL / UniversalSpeech) via the `ISpeechOutput` seam — a config swap,
  not a rewrite. Not a hybrid; one channel live at a time.
- **Scope = build-and-bind:** game already has uGUI EventSystem focus + full keyboard nav
  (arrows/WASD move, Enter/Space submit, Escape cancel, Tab change-tab, Page/Home/End). We build the
  `AccessibilityNode` hierarchy and bind it to live controls + focus events so Narrator can navigate menus.
- See `PLAN.md`, `docs/feasibility-findings.md`, `docs/native-vs-ual-menus.md`, `docs/input-and-keyboard.md`.

## Key hook points (verified in decompiled code)
- Dialogue/narration: `SubtitlesView.UpdateText(string)`, `ShowSubtitle(...)`, `event Action SubtitleStarted`,
  `IsOpenedSubtitle`; Yarn Spinner `DialogueViewBase.RunLine(LocalizedLine, ...)` with
  `LocalizedLine.{RawText, TextWithoutCharacterName, CharacterName}`.
- Menus: uGUI `ISelectHandler.OnSelect` on `UISelectable`/`HoverableButton`/`PhoneButtonView`;
  EventSystem `currentSelectedGameObject`; settings tree `ASettingsInstance` (+ button/dropdown/slider).
- Native API (interop-verified): `AccessibilityManager.SendAnnouncementNotification(string)`,
  `SendScreenChangedNotification(int)`, `SendLayoutChangedNotification(int)`, `IsScreenReaderEnabled()`;
  `AssistiveSupport.screenReaderStatusOverride`; `AccessibilityNodeManager.{SetRole,SetState,SetParent,
  GetIsFocused,SetIsActive}`; roles incl. Button/Slider/Toggle/Dropdown/TabButton/TabBar.

## Toolchain
- Decompile: `C:\Users\amock\tools\cpp2il\Cpp2IL-2022.1.0-pre-release.21-Windows.exe` (only build that reads
  metadata v39) → ilspycmd → `decompiled/`. dummydll output = signatures only (fine for Harmony hooks).
- Loader: MelonLoader v0.7.3 manual-installed; interop assemblies at
  `<game>\MelonLoader\Il2CppAssemblies\` (141 dlls; game = `Assembly-CSharp.dll`, no Il2Cpp prefix).
- Build: .NET SDK 10 (`dotnet build NoImNotAHumanAccess.csproj`). Output DLL → `<game>\Mods\`.

## Current Phase
**Phase 0 — native output proof (awaiting ear-confirmation).** Built + deployed; the native announcement
code path runs clean in-process. Log confirms: `Resolved SendAnnouncementNotification_Injected ICall`,
`available=True`, `Speak: ...` with NO exception. User must confirm Narrator actually voices it.

### Native announcement: how it works (important, hard-won)
- Entry point = `UnityEngine.Accessibility.AccessibilityManager.SendAnnouncementNotification(string)` (internal).
- Il2CppInterop's PUBLIC string wrapper is BROKEN in this build (v9.1.0 era): it marshals via
  `Il2CppSystem.ReadOnlySpan<char>.GetPinnableReference()`, which is missing → runtime "Method not found".
- FIX (in `NativeAnnouncer.cs`): bypass the wrapper. Resolve the ICall ourselves via
  `IL2CPP.ResolveICall<Delegate>("UnityEngine.Accessibility.AccessibilityManager::SendAnnouncementNotification_Injected")`
  and call it with a hand-built `{char* begin; int length}` span over a `fixed` managed string. Stable native ABI.
- `screenReaderStatusOverride` property setter isn't exposed either → set static field `s_ScreenReaderStatusOverride=2`
  (On) via `il2cpp_class_get_field_from_name` + `il2cpp_field_static_set_value`. Best-effort; not required when
  Narrator is actually running (isScreenReaderEnabled true).
- `IL2CPP.GetIl2CppMethodByName` does NOT exist in this interop version; use `il2cpp_class_get_method_from_name`
  (raw) or ResolveICall. csproj needs `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`.

### Build/deploy/test loop (verified working)
- Build: `dotnet build NoImNotAHumanAccess.csproj -c Release`. Deploy: copy `bin\Release\NoImNotAHumanAccess.dll`
  → `<game>\Mods\`. Test: rotate `MelonLoader\Latest.log` (move aside) BEFORE launch so you read only the new run;
  the log keeps a stale tail otherwise (burned ~20 min on this — always rotate + read by this-run marker).
- MelonLoader remote-API 502s at startup are harmless (offline game-id lookup); interop already generated locally.

## Menu roles + values — WORKING (confirmed in-game 2026-05-31)
- Focused control speaks label + role + value, and in-place value changes (slider drag / toggle / dropdown
  cycle) re-announce the new value (MenuNarrator tracks last value while focus stays put).
- Dropdowns (fullscreen/resolution/language): name from GameObject name (Humanized), value from TMP_Dropdown
  caption. Detect dropdown BEFORE toggle (these rows carry a stray Toggle).
- Sound sliders: name from SoundSettingsVolumeSlider._groupNameText (LocalizeStringEvent; read its driven
  TMP_Text since GetLocalizedString() didn't bind), value from _valueText. Component found via parent search.
- Sensitivity sliders: FakeSlider — name from row-label scan (first non-numeric TMP in the row), value from
  FakeSlider._valueText. Both classes in ns `_Code.Infrastructure.Settings.Sound`.
- ControlDescriber + Il2CppRaw (reusable raw-IL2CPP helpers: GetClass/GetMethod/GetComponent[InChildren|InParent]
  /ReadObjectField/InvokeStringGetter). All component-by-type + TMP reads go through raw IL2CPP.

## Menu narration (label-only) — WORKING (confirmed in-game 2026-05-31)
- Main menu speaks: "new game, settings, quit, collections, ...". Settings speaks live labels:
  "volume slider, english, typing effect, fullscreen, windows, vsync, back".
- Focus tracking: poll `EventSystem.current.currentSelectedGameObject` in OnUpdate (MenuNarrator.Tick).
- Label reading: RAW IL2CPP (bypasses the interop dual-naming wall — see below). Resolve TMP_Text class by
  NATIVE name ("TMPro","TMP_Text"), resolve GameObject.GetComponentInChildren(Type,bool) + TMP_Text.get_text
  via il2cpp_class_get_method_from_name, invoke via il2cpp_runtime_invoke, marshal string back. Fallback to
  GameObject name for image-only controls (main menu). `raw resolve: tmpClass=True gcic=True getText=True`.
- NOTE: MenuNarrator.Tick calls _speech.Speak directly, so spoken labels do NOT appear under the AccessMod
  `Speak:` log line — verify menu narration BY EAR, not by log.

## Menu narration — earlier findings / dead ends (kept for the reuse pipeline)
- Focus tracking CONFIRMED working: poll `EventSystem.current.currentSelectedGameObject` in OnUpdate; it changes
  as the user arrows (logged NewGame -> Settings -> Exit). The polling approach is correct.
- IL2CPP JIT WALL (important, recurring): generic component lookups DO NOT JIT in this Il2CppInterop build —
  `GetComponentsInChildren<Component>(bool)` (open OR closed generic) and the `(Il2CppSystem.Type, bool)` overload
  (compiler picks a System.Type overload, won't bind) both fail. Errors seen: "Method not found:
  GameObject.GetComponentsInChildren(Boolean)". Do NOT keep retrying generic GetComponents variants.
- Strongly-typed `GetComponent<TMP_Text>()` also fails: "Could not load type 'TMPro.TMP_Text' from assembly
  'Unity.TextMeshPro'" at JIT. Avoid compile-time TMP_Text too.
- MAIN MENU items are IMAGE/SPRITE based: `MainMenuSignLineElement` holds Image/AnimatedImage, NO text field.
  So their accessible name is NOT a text component — it must come from the GameObject name (NewGame/Settings/Exit)
  mapped to a localization key. This is the bespoke-control case the plan flagged. Settings-screen controls likely
  DO have RTLTextMeshPro labels (different path) — not yet confirmed live.
- NEXT approach (decided after the above): (a) get the single text component via the NON-generic
  `GetComponentInChildren(Il2CppSystem.Type)` if it binds, else read the GO-name->Loc mapping; (b) for the main
  menu specifically, map GO names to localized strings. Need to read the game's Localization tables for the menu
  keys. Stop using GetComponents enumeration.

## Pending proof (USER)
- Narrator ON (Ctrl+Win+Enter). On launch expect spoken "...mod loaded. Press F8 to test...". Press F8 → "Screen
  reader test...". If voiced → Phase 0 GO (native confirmed). If silent despite clean log → NO-GO, switch
  `ISpeechOutput` to a UAL/UniversalSpeech implementation (seam already in place; one new class).
