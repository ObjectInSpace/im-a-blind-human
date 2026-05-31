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

## Pending proof (USER)
- Narrator ON (Ctrl+Win+Enter). On launch expect spoken "...mod loaded. Press F8 to test...". Press F8 → "Screen
  reader test...". If voiced → Phase 0 GO (native confirmed). If silent despite clean log → NO-GO, switch
  `ISpeechOutput` to a UAL/UniversalSpeech implementation (seam already in place; one new class).
