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

**IN PROGRESS — Phase: world orientation (re-planned 2026-06-01 on corrected gameplay model).** Dialogue + menus done;
interaction prompt CONFIRMED. **F9 (status) and F10 (orientation) both FAILED in-game** — root cause: the Zenject
`SceneContext` lookup returned zero, so IPlayerService + the status controllers never resolved (the game-assembly
FindObjectOfType path itself works — OrientationNarrator logged provider=True). See the reconsidered plan below.

### CORRECTED GAMEPLAY MODEL + reconsidered plan (2026-06-01)
The game is a HYBRID: (1) first-person walk/aim between DOOR/world objects, then (2) interacting opens a STILL PHOTO
close-up with highlightable objects. Three systems needed, re-prioritized:
- **P0 — unbreak service access.** `FindObjectOfType(Zenject.SceneContext)` failed. Either verify the SceneContext
  type identity (image/ns/name) or capture `IPlayerService` via a Harmony hook on a view `Init(...)` that receives it
  (many do). Unblocks F9 status AND nav-pose reads. The whole Zenject-resolve assumption is the suspect.
- **SYS-B — read the highlighted object (do first after P0; highest value, no Zenject dep).** Close-up views fire
  `OnPointerEntered(name, narrativeDescription, gameplayDescription, EConsumable)` (FridgeCloseUpView ~22790) and hold
  on-screen description TMPs (`_narrativeDescription`/`_gameplayDescription` on Consumable/Mushroomlist close-ups).
  Hook/read these → speak the highlighted object + description. Dialogue-sink + ControlDescriber patterns.
- **SYS-C — describe the view.** List the objects in the active close-up; descriptions already authored (narrative +
  gameplay), so likely NO image descriptions needed for v1.
- **SYS-A — lead the player to world objects (nav steering).** The original F10 idea; needs P0 (player pose) + a
  MoveXZ/TeleportTo steering probe. Lower priority — the real object interaction happens in the close-up (SYS-B/C).
- `ACloseUpView` family: Fridge/Phone/Radio/Consumable/Mushroomlist (object-specific photos), `ICloseUpsController.
  IsAnyCloseUpActive`. Detail in the navigation memo.

### (Below: the F9/F10 builds as-shipped — both currently non-functional pending P0.)

### Orientation "what's around me" (F10) — BUILT + DEPLOYED 2026-06-01, awaiting in-game confirmation
What the user actually wants from F10: the interactables in the current room and WHERE they are relative to the
player. Game is first-person aim-to-interact (user-confirmed), so "relative" = a WORLD BEARING (which way to turn).
"Selectable now" = the game's current interactable set, no seen/unseen tracking needed (user-confirmed). Files:
`src/World/OrientationNarrator.cs`, `src/World/ZenjectResolver.cs` (shared resolve, extracted from GameStateAccess),
new `Il2CppRaw` helpers. Wired F10 in AccessMod. Build green 0/0.
- SOURCE: `ActionableObjectsViewProvider.ActionableObjectViews` (AActionableObjectView[]; provider is a MonoBehaviour →
  FindObjectOfType). ns `_Code.Infrastructure.ActionableObjects`. Filter each by `get_CanShowHint` (the game's own
  "offer this now" signal). Name = GameObject name humanized (NOT the LocalizedString subject — robust; upgrade later
  if names read poorly). Position = Component.get_transform→get_position (raw). Player pose = IPlayerService
  (Zenject-resolved, ns `_Code.Infrastructure.Player`) get_Position + get_LookDirection (Vector3 getters).
- BEARING: XZ-plane; forward dot → ahead/behind, right dot (cross(up,fwd)) → left/right, +near/far by distance.
  Speaks e.g. "Fridge, to your left, close. Television, ahead, far."
- NEW Il2CppRaw helpers: ReadObjectArray (via Il2CppReferenceArray<Il2CppObjectBase>), InvokeBoolGetter,
  InvokeVector3Getter, GetUnityObjectName, GetComponentWorldPosition. (`il2cpp_array_addr_with_size` does NOT exist in
  this interop build; use the Il2CppReferenceArray wrapper. UnityEngine.Component has no public (IntPtr) ctor; read
  name/position via raw getters instead of wrapping.)
- ON NEXT TEST RUN: first F10 logs `[OrientationNarrator] resolved: provider=True getViews=True canShowHint=True
  player=True getPos=True`. Then in a room, press F10, confirm by ear: are the right things listed, names sensible,
  and directions correct (turn and re-press — bearings should shift)? Tune: if names are junk GameObject names →
  switch to resolving the RaycastTargetHint subject; if directions inverted → flip the rightDot sign or look-vector.
- DROPPED from the earlier room-summary build: RoomTracker + the RoomsManager.OnRoomEntered hook were removed (that
  approach was room-name/occupants/exit; user redirected to interactable-bearings). The OnRoomEntered hook + ERoom/
  ECharacterType maps are documented in the navigation memo for if/when the deferred status-key "current room" field
  is built.

### Status key (F9) — BUILT + DEPLOYED 2026-06-01, awaiting in-game confirmation
On-demand readout of day / time-of-day / energy / held items. Files: `src/World/GameStateAccess.cs` (Zenject-resolve
+ raw reads), `src/World/StatusNarrator.cs` (compose + speak), wired to F9 in `AccessMod.OnUpdate`. Build green 0/0.
- DATA SOURCES (verified): `IDayNightController.{Day, CurrentTimeOfDay(=ETimeOfDay Day0/Night1), DayActions,
  MaxDayActions}` — all plain getter props. **Energy IS day-actions** (no separate energy meter exists; spoken as
  "N of M energy"). Items: `IConsumablesController.Count(EConsumable)` over the EConsumable enum.
- INSTANCE ACCESS — the unproven part (user chose to try this first): controllers are Zenject-injected non-MonoBehaviour
  interfaces (`FindObjectOfType` can't reach them). We `FindObjectOfType(Zenject.SceneContext)` → `get_Container` →
  non-generic `DiContainer.Resolve(System.Type)` with the interface's type object. Generic `Resolve<T>` would not JIT
  (open-generic wall), so non-generic only. ALL via raw IL2CPP (new `Il2CppRaw` helpers: InvokeInt32Getter,
  InvokeInt32MethodWithEnum, InvokeObjectMethodWithObject, InvokeObjectGetter, FindObjectOfType).
- ON NEXT TEST RUN (in a gameplay scene, not main menu): press F9. Log line `[GameStateAccess] resolved: dayNight=True
  consumables=True getDay=True count=True` = the Zenject path works. If any `False`: the resolve path failed →
  FALLBACK is capture-on-init Harmony hooks on DayNightController/ConsumablesController ctor/Init (see movement-model
  memo). Then confirm the spoken status is correct by ear. Image/ns guesses to re-check on miss: SceneContext image
  `Zenject.dll` ns `Zenject`; controllers image `Assembly-CSharp.dll` ns `_Code.Infrastructure.{DayNight,Consumables}`.
- NOT YET BUILT: "current room + who's here" (4th status field the user wanted). RoomsManager has NO CurrentRoom
  property — needs a Harmony postfix on `RoomsManager.OnRoomEntered(ARoom)` to stash current ERoom + read
  `ARoom.AliveCharactersInside` (List<ECharacterType>). Deferred to after the Zenject path is confirmed, since it's a
  different mechanism (hook, not resolve). ERoom={Kitchen,Office,BigRoom,Bathroom,Pantry,Entrance,Bedroom};
  ECharacterType=large NPC enum.

### Interaction prompts & world HUD — investigation + first hook (history below)
READ-ONLY decompile investigation COMPLETE (2026-06-01); surfaces mapped below.

### Investigation findings (2026-06-01, read-only — decompile bodies are Cpp2IL stubs, signatures only)

**The interaction-prompt model (the core loop) — fully mapped:**
- `RaycastSource : MonoBehaviour` (camera-mounted) has `ARaycastTarget Target { get; set; }` updated every frame in
  `Update()` — this is "what the player is looking at." (`_Code...`, ~line 3049 in the decompile.)
- `ARaycastTarget : MonoBehaviour` (abstract) is the per-object focus target: `OnFocused`/`OnLostFocus`/
  `OnTargetedCorrectConditions`/`OnTargetedWrongConditions`, holds `IHUDPresenter HUDPresenter` + a
  `LinkedActionableObject`. Subclass `RaycastTargetHint` carries the prompt data: `LocalizedString _subjectLocalizationKey`,
  `LocalizedString _actionLocalizationKey`, `ERaycastHintIcon _icon`. (~line 4593.)
- `AInteractableObject : MonoBehaviour, IInteractable` (abstract, ~line 10739) is the interactable base: holds a
  `RaycastTargetHint _raycastTarget`, abstract `Interact()`, `HardConditions`/`SoftConditions`, `EnergyCost`. Many
  concrete subclasses (CatInteractable, CigaretteInteractable, …).
- **THE CHOKE POINT:** `HUDPresenter : IHUDPresenter` (~line 5390, a plain class — NOT a MonoBehaviour) is where the
  prompt is actually shown: `UniTask ShowHint(string subject, string action, Transform target, ERaycastHintIcon icon)`
  and `UniTask HideHint()` (~line 5839). By the time `ShowHint` is called, `subject`/`action` are ALREADY resolved
  display strings (the LocalizedStrings on RaycastTargetHint are evaluated upstream) — so a hook here gets clean text
  with NO localization work needed. This is the dialogue-`UpdateText` pattern repeating: one resolved-string sink.
  `ERaycastHintIcon` = { None, Energy, EnergyX2, AllEnergy, Save } — energy-cost hint for the action.
- `ActionableObjectsManager : IActionableObjectsManager` (~line 24883) orchestrates the `IActionableObjectView[]` but
  does NOT expose the current target; targeting lives on `RaycastSource.Target`. No need to hook the manager.

**State-change / notification popups:**
- Production path: `DialogManager.ShowSubtitlePopup(EInfoMessageType, float)` and `.ShowSubtitlePopup(string)`
  (~line 30483; `DialogManager` is the real impl — the other `ShowSubtitlePopup` overloads at 31387/31753/32036 belong
  to `MockDialogManager`/`DialogView`/`FakeDialogRunner` test doubles, ignore them).
- `EInfoMessageType` (~line 35034) is a fixed enum: DayEnd, NightEnd, Fridge, CantSleepDay/Night, SomebodyWasMurdered,
  WindowNailedUp1/2, FoundMushroom, Apple, INeedToNailUpWindows, NoMoreRadioToday, NoMoreBeer, BodyEaterVisit, Clocks.
- These resolve through `_Code.DialogSystem.SubtitlesView.ShowDialogForTime(EInfoMessageType, TimeSpan)` /
  `ShowDialogForTime(string, TimeSpan)` which queue `(string, TimeSpan)` and (very likely) reach the EXISTING Phase-2
  `SubtitlesView.UpdateText(string)` hook. **OPEN QUESTION (runtime-only, bodies are stubs):** confirm by ear whether
  a triggered popup (e.g. DayEnd) already speaks via the Phase-2 UpdateText hook. If yes → notifications are FREE,
  no new hook. If the enum path bypasses UpdateText, hook `DialogManager.ShowSubtitlePopup(EInfoMessageType)` and map
  the enum to a phrase (the localized text is internal, but the enum names are self-describing).

**On-screen control prompts (persistent key-hint row):**
- `ControlsListView : MonoBehaviour` holds `ControlView[] _controls` (~line 28730). Each `ControlView` (~line 28904)
  has `InitKey(string key, EControl control)` (glyph) + `SetDescription(string description)` (label) + `SetAvailability`.
- HUD orchestration via `IHUDPresenter`: `SetupAndShowControlsView(EControlsList)`, `SetControlsAvailability(EControl,
  bool)`, `HideControlsView()`, `SetHintAvailability(bool)`. Lower priority than the interaction prompt.

### FIRST HOOK — CONFIRMED IN-GAME 2026-06-01
Both hooks resolved + patched (log: `[WorldPatches] Patched Il2Cpp_Code.Menues.HUD.HUDPresenter.ShowHint(...)` and
`.HideHint()`). Heard "view entrance" by ear approaching a door — chain confirmed end-to-end: interop name (incl.
"Menues" misspelling) correct, resolve-by-arity worked, strings are resolved DISPLAY TEXT (not loc keys), phrasing
order right (verb+noun: action="view", subject="entrance"). The door is a look-closer interactable (verb "view"),
faithful to the on-screen HUD prompt — not a bug. Below is the as-built record.
Harmony-postfix `HUDPresenter.ShowHint(string subject, string action, Transform target, ERaycastHintIcon icon)` →
speak `"{action} {subject}"` (e.g. "take cigarettes", "open door"). Postfix `HideHint()` resets the dedupe so
re-targeting the same object re-speaks. Highest-value, lowest-effort surface, the core gameplay loop. Same
reflection-resolved patching pattern as `DialoguePatches`; `HUDPresenter` is a plain managed class so the MethodInfo
resolves cleanly. De-dupes consecutive identical hints (prompt likely re-fires while looking at the same object) like
`DialogueNarrator` dedupes typewriter re-entry.
- NEW FILES: `src/World/HudNarrator.cs` (text sink + dedupe) and `src/World/WorldPatches.cs` (ShowHint + HideHint
  hooks, resolve-by-arity 4/0). Wired in `AccessMod.OnInitializeMelon` after the dialogue hooks. Build green 0/0;
  DLL deployed to `<game>\Mods\`.
- UNVERIFIED INFERENCE (the one runtime risk): interop type name `Il2Cpp_Code.Menues.HUD.HUDPresenter` (note the
  game's spelling "Menues"), built by the same `Il2Cpp`+decompiled-namespace rule the working DialoguePatches uses.
  IL2CPP interop DLLs don't introspect via plain reflection outside the MelonLoader runtime (GetTypes returns 0), so
  this could only be confirmed at runtime. ON NEXT TEST RUN: rotate `MelonLoader\Latest.log`, launch, look for
  `[WorldPatches] Patched ...HUDPresenter.ShowHint(...)` (success) vs `Could not resolve ...` (name miss → check the
  real interop namespace; candidates if "Menues" is wrong: `Menus`, or HUDPresenter living under a different ns).
  Then look at an interactable in-game and confirm the prompt speaks by ear.
- DEDUPE-PHRASING NOTE TO RE-CHECK BY EAR: assumed `action`=verb, `subject`=noun → "{action} {subject}". If it reads
  backwards in-game (e.g. "cigarettes take"), swap the order in `HudNarrator.OnHint`. Also confirm the strings are
  resolved display text, not localization KEYS (if keys leak through, resolve upstream from RaycastTargetHint's
  LocalizedStrings instead).
- ENERGY-COST SUFFIX — BUILT + DEPLOYED 2026-06-01 (not yet ear-confirmed). `ShowHintPostfix` now takes `int icon`
  (ERaycastHintIcon as underlying int, no interop-enum bind); `HudNarrator.EnergyCostSuffix` appends ", 1 energy" /
  ", 2 energy" / ", all energy" for Energy/EnergyX2/AllEnergy; None+Save+unknown = no suffix (Save isn't a cost).
  Suffix is part of the deduped phrase so a cost change on the same target re-speaks. The "view entrance" door was
  a None/Save prompt, so confirm the suffix by ear on the first energy-costing action.

- Reusable infra already in place: raw-IL2CPP helpers (`Il2CppRaw` incl. `ReadStringField`/`ReadInt32Field`),
  reflection-resolved Harmony patching pattern (`DialoguePatches`), `ISpeechOutput` channel, `ControlDescriber`.
  REMEMBER the IL2CPP image-name rule: `GetClass` takes the ORIGINAL assembly name + ".dll" and the RUNTIME
  namespace (no `Il2Cpp` prefix) — verify each type's namespace against the interop assembly before use.
- Build/deploy loop: `dotnet build -c Release` → copy DLL to `<game>\Mods\`; rotate `MelonLoader\Latest.log`
  aside before each test run; verify by ear AND log.

**Phase 2 — dialogue narration (CONFIRMED in-game 2026-06-01).** All three dialogue/narration surfaces are
hooked and confirmed working by ear (dialogue, speaker attribution, intro cutscene narration). Diagnostic
per-line logging has been stripped; only one-time confirmations remain. New files: `src/Dialogue/DialogueNarrator.cs`
and `src/Dialogue/DialoguePatches.cs`. Wired in `AccessMod.OnInitializeMelon` via `HarmonyInstance`; added
`0Harmony.dll` reference. `Il2CppRaw.ReadStringField` added for reading IL2CPP string FIELDS by offset.

- **Three hooked surfaces (all Harmony postfixes, MethodInfo resolved by reflection from the loaded interop
  assemblies — NOT typed `[HarmonyPatch(typeof(...))]`, to dodge the interop dual-naming wall):**
  1. `Il2Cpp_Code.DialogSystem.SubtitlesView.UpdateText(string)` — subtitles / info popups.
  2. `Il2CppYarn.Unity.LineView.RunLine(LocalizedLine, Action)` — character dialogue. Reads `LocalizedLine.RawText`
     (a FIELD, read by offset) + `CharacterName` (a GETTER) via raw IL2CPP; speaks `"Speaker: line"`, not
     double-prefixing when RawText already leads with the name.
  3. `Il2Cpp_Code.Utils.CustomYarnReading.CustomYarnReader.GetNodeContent(string) -> Il2CppStringArray` —
     intro/ending cutscene narration. `EndingView` (reused for the opening; see `EEnding.Intro`) is Timeline-driven
     with no method choke point, so we hook the node-content reader and join+speak its returned lines.
- **DialogueNarrator:** cleans TMP rich-text via `ControlDescriber.Clean`, optional speaker prefix, dedupes
  consecutive identical lines (absorbs typewriter re-entry), speaks with `interrupt: true`.
- **KEY LESSON (cost us the empty-read bug):** `Il2CppRaw.GetClass` needs the ORIGINAL IL2CPP image name + `.dll`
  (`YarnSpinner.Unity.dll`, ns `Yarn.Unity`), NOT the `Il2Cpp`-prefixed interop filename. Wrong name → zero class
  handle → silent null/empty field+getter reads. The interop type's own static ctor shows the correct image name.
- **KNOWN TRADEOFF:** intro narration is spoken as one block when the node resolves, not paced to the on-screen
  timeline reveal (no per-line choke point exists). Acceptable; revisit with a per-frame `_subtitlesText` TMP watch
  only if the block-ahead timing feels wrong.
- **Build:** `dotnet build -c Release` green, 0/0. Deployed to `<game>\Mods\`.

**Position-in-list + category announcement (CONFIRMED in-game 2026-06-01).** Shared primitive in
`ControlDescriber`: `ResolveGroup(go)` climbs to the nearest ancestor holding 2+ selectable controls (the group),
counts active selectables and the focused ordinal. `DescribePosition` appends ", N of M"; `DescribeGroupLabel`
scans the group for a header TMP (skipping members' own captions) for the category name. `MenuNarrator` appends
position on every focus change and prepends the category name when focus enters a NEW group. Helps BOTH dialogue
choices (HoverableButton) and menus (UISelectable) — neither is a uGUI Selectable subclass, so there is no
Navigation index; position is derived from the transform tree.
- Selectable detection (raw IL2CPP, verified namespaces): HoverableButton = `_Code.Characters.DialogSystem`,
  UISelectable = `_Code.Utils.UI`; plus typed uGUI Slider/Toggle and raw TMP_Dropdown.
- Settings are laid out per category, so counts reset per group ("Sound, 1 of 3" then "Vibration, 1 of 4") — this
  is desired, confirmed by user.
- KNOWN GAP: main menu reads labels but NOT position. Its items are `MainMenuSignLineElement` (sprite-only, only
  IPointer* handlers, NO ISelectHandler), laid out in a circle — they don't flow through EventSystem selection like
  real controls. Position there is a separate investigation (how keyboard focus moves on the circle menu). Deferred.
- `EDialogButtonStyle` choice-style readout — PROTOTYPED THEN PARKED (2026-06-01). Speaking ", energy"/", gun"/
  ", take item"/", give item" after a choice read as redundant: the choice TEXT usually already conveys it
  ("Shoot him" = gun). Reverted the spoken suffix; kept `ControlDescriber.NoteDialogButtonStyle` which passively
  logs each distinct style int once per session (`dialog button style=N on '...'`) so normal play collects evidence.
  Revisit only if play reveals choices where the styling carries info the text omits (e.g. a neutral line that
  secretly costs energy). `Il2CppRaw.ReadInt32Field` (generic int-field-by-offset reader) added and kept.
  EDialogButtonStyle order: 0 Default, 1 Default_UI, 2 Energy, 3 ConsumablesGet, 4 ConsumablesGive, 5 Gun,
  6 Pause_UI, 7 Skip.

**Phase 0 — native output proof (CONFIRMED; menu narration also working).** The native announcement path runs
clean and was confirmed in-game (NVDA via Unity AssistiveSupport). Menu roles/values working.

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
