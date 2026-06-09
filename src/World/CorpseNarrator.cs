using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Menus;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Names the CORPSES present in the current scene — the dead characters that appear in a room (and in the 2D
    /// room photo a door opens) but carry no highlight/hover, so the existing room-view narration and the 2D stepper
    /// (<see cref="RoomViewNarrator"/>, <see cref="TwoDProbe"/>) never speak them: corpses are de-buttoned
    /// (<c>CharacterRoomObjectView.IsButtonActive == false</c>, <c>DisableButton()</c>), so they're not hoverable and
    /// not steppable. A sighted player still SEES a body on the floor; a blind player needs to be told it's there.
    ///
    /// What we read (verified in the decompile, <c>artifacts/decompiled/Assembly-CSharp.decompiled.cs</c>):
    /// a person in a room is a <c>_Code...CharacterRoomObjectView : ARoomObjectView&lt;ERoomPeopleState&gt;</c>. Its
    /// current pose is the base class's <c>&lt;SelectedState&gt;k__BackingField</c> (an <c>ERoomPeopleState</c> int);
    /// the death poses are <c>Corpse</c>/<c>LostChildCorpse</c> (+ the violent-death variants Hanged/Burned/Twisted).
    /// Its identity is <c>Data</c> (<c>&lt;Data&gt;k__BackingField</c>, a <c>CharacterSOData</c>), which carries
    /// <c>_characterType</c> (→ the character's name) and <c>_isImposter</c> (human vs. visitor). <c>_isImposter</c> is
    /// the same field <see cref="SignNarrator"/> reads.
    ///
    /// HUMAN vs. VISITOR (user 2026-06-09): unlike a LIVE inspection — where we never voice a verdict, because the
    /// judgement is the player's — a corpse is the RESULT of a kill already committed, so the detection mini-game for
    /// that character is over and stating its nature spoils nothing. There is no per-corpse "revealed this run" flag in
    /// the game to gate on more finely (only gallery-unlock meta-data), so a corpse is treated as resolved: we name it
    /// and say whether it was a human or a visitor.
    ///
    /// Source is a plain scene scan (<c>FindObjectsByType(CharacterRoomObjectView)</c>) — no Zenject, the same approach
    /// <see cref="TwoDProbe"/> uses for the photo's <c>UIButton</c>s. Works in both the 3D room and the 2D photo (the
    /// views are the same objects). Read-only; never throws.
    /// </summary>
    public sealed class CorpseNarrator
    {
        private const string GameAsm = "Assembly-CSharp.dll";

        // ERoomPeopleState death poses (enum order verified in the decompile). A character in any of these is a body.
        // Confirmed in-game from the diagnostic log: a killed character can sit in Corpse=4 OR Bags=9 (a bagged body) —
        // the human corpse I killed showed as Bags=9 and was wrongly skipped before this was added. The violent-death
        // and lost-child variants are included by enum semantics (not all yet observed live). Kept as ints so we never
        // bind the interop enum type.
        private static readonly HashSet<int> DeathStates = new()
        {
            4,  // Corpse
            9,  // Bags (body bag) — confirmed a death pose in-game
            10, // Twisted
            11, // Hanged
            12, // Burned
            14, // LostChildCorpse
        };

        private bool _resolved;
        private IntPtr _viewClass;          // CharacterRoomObjectView
        private IntPtr _characterSoDataClass; // CharacterSOData (for reading _characterType / _isImposter off Data)

        /// <summary>
        /// A sentence naming the corpses in the current scene, or null if there are none (so the caller can append it
        /// to a larger readout or skip it). E.g. "One corpse: Esenin, a visitor." / "Two corpses: Doc, a human; and
        /// Esenin, a visitor."
        /// </summary>
        public string? Describe()
        {
            try
            {
                EnsureResolved();
                if (_viewClass == IntPtr.Zero) return null;

                var corpses = new List<string>();
                IntPtr[] views = Il2CppRaw.FindObjectsByType(_viewClass, includeInactive: false);
                MelonLogger.Msg($"[CorpseNarrator] scanning {views.Length} CharacterRoomObjectView(s).");
                foreach (IntPtr view in views)
                {
                    if (view == IntPtr.Zero) continue;
                    bool active = Il2CppRaw.GetComponentGameObjectActive(view);

                    // Read the pose off the instance's actual class (the field is on the generic base; the lookup
                    // walks parents).
                    IntPtr cls = IL2CPP.il2cpp_object_get_class(view);
                    int state = Il2CppRaw.ReadInt32Field(view, cls, "<SelectedState>k__BackingField", fallback: -1);
                    IntPtr data = Il2CppRaw.ReadObjectField(view, cls, "<Data>k__BackingField");
                    string dbgName = data != IntPtr.Zero ? CharacterName(data) : "<no data>";

                    // Diagnostic (temporary): log every view's active flag + pose state + identity so we can see whether
                    // corpses are found at all and which ERoomPeopleState the game uses for a body. Remove once verified.
                    MelonLogger.Msg($"[CorpseNarrator]   view active={active} state={state} " +
                                    $"isDeath={DeathStates.Contains(state)} name={dbgName}.");

                    if (!active) continue;                       // only bodies in THIS visible scene
                    if (!DeathStates.Contains(state)) continue;  // not a death pose → living/other character
                    if (data == IntPtr.Zero) continue;

                    bool isImposter = ReadIsImposter(data);
                    corpses.Add($"{dbgName}, {(isImposter ? "a visitor" : "a human")}");
                }

                if (corpses.Count == 0) return null;
                if (corpses.Count == 1) return $"One corpse: {corpses[0]}.";
                return $"{Count(corpses.Count)} corpses: {string.Join("; and ", corpses)}.";
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[CorpseNarrator] Describe threw: {e.Message}");
                return null;
            }
        }

        /// <summary>The character's name from <c>CharacterSOData._characterType</c>, mapped via the embedded
        /// ECharacterType table. Falls back to the data object's humanized Unity name, then "Someone".</summary>
        private string CharacterName(IntPtr data)
        {
            int type = Il2CppRaw.ReadInt32Field(data, _characterSoDataClass, "_characterType", fallback: -1);
            if (type >= 0 && type < CharacterTypeNames.Length) return CharacterTypeNames[type];

            string? raw = Il2CppRaw.GetUnityObjectName(data);
            string humanized = MenuStepUtil.Humanize(raw);
            return string.IsNullOrWhiteSpace(humanized) ? "Someone" : humanized;
        }

        /// <summary>Read <c>CharacterSOData._isImposter</c> (true = visitor/imposter). Defaults to false on any miss.</summary>
        private bool ReadIsImposter(IntPtr data) =>
            Il2CppRaw.ReadBoolField(data, _characterSoDataClass, "_isImposter");

        private static string Count(int n) => n switch
        {
            2 => "Two",
            3 => "Three",
            4 => "Four",
            5 => "Five",
            _ => n.ToString(),
        };

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _viewClass = Il2CppRaw.GetClass(GameAsm, "_Code.Rooms", "CharacterRoomObjectView");
                _characterSoDataClass = Il2CppRaw.GetClass(GameAsm, "_Code.Characters", "CharacterSOData");
                MelonLogger.Msg($"[CorpseNarrator] resolved: view={_viewClass != IntPtr.Zero} " +
                                $"characterSoData={_characterSoDataClass != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[CorpseNarrator] EnsureResolved threw: {e.Message}");
            }
        }

        // ECharacterType value → name (enum order verified in the decompile). These are the game's internal codenames;
        // most read fine spoken (Doc, Firefighter, Neighbour). Indexed by the enum's underlying int.
        private static readonly string[] CharacterTypeNames =
        {
            "Sanya", "TV", "Courier", "Neighbour", "Esenin", "Anxiety", "Daughter", "Cold", "Fan", "Prophet",
            "Super Fake", "Widow", "Scammer", "Doc", "Gasmask", "Wolfhound", "Hunter", "Not True", "Greatmother",
            "Sexy", "Phone", "Anger", "Luka", "Former Fema", "Foreigner", "Alkonost", "Blind", "Marauder", "Nun",
            "Taxi Driver", "Firefighter", "Fugitive", "Teacher", "Edgar", "Raskolnikov", "Grave Digger", "Provocateur",
            "Mother", "Wife Fema", "Vigilante", "Theorist", "Buddy", "Fortune Teller", "Dude", "Best Son",
            "Big Lebowski", "Couple", "Fatman", "Wheelchair", "Cultist One", "Cultist Two", "Cultist Three",
            "Cultist Priest", "Ballerina", "Intruder", "Mushroom Eater", "Miner", "Sirin", "Empty", "Twins", "Player",
            "Jacob", "Static Boy", "Tourist", "Unstable", "Rocker", "Experienced", "Jacket", "Tough", "Bald",
            "Nervous", "Leper", "Fairytaller", "Funny Guy", "Alt",
        };
    }
}
