using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Announces the outcome of a shooting — whether the person the player just shot was a human or a visitor
    /// (imposter). After a shot the game plays a cutscene that visually reveals the truth; a blind player can't read
    /// it, so we speak it.
    ///
    /// Fed by <see cref="WorldPatches"/>, which postfixes ONLY the player's deliberate gun-shot command on
    /// <c>DialogCommandsInstance</c> — <c>KillCharacter(string)</c>. This is the gun path (paired with the explicit
    /// <c>KillCharacterWithNoGun</c> for non-gun deaths) and fires at the dialogue beat that triggers the
    /// kill/cutscene, which is the right moment. The other kill commands are deliberately NOT a source here: they are
    /// not the player choosing to shoot — <c>KillCharacterWithNoGun</c> (no gun), <c>KillRoom</c> (a visitor wiping a
    /// room), <c>KillTomorrow</c> (an off-screen overnight death), <c>FakeShot</c> (a trigger-pull with no kill). (The
    /// meta <c>KillImposter</c>/<c>KillInnocent</c> trackers were also rejected as the source: they live on the
    /// persisted <c>MetaPrefsData</c> stats model and are reconciled for achievements, NOT necessarily at the shot —
    /// wrong timing.)
    ///
    /// The command carries the target as a RAW NAME string (e.g. "Doc"); we map it to the <c>ECharacterType</c>
    /// underlying int via a managed table built from the decompiled enum, resolve the live
    /// <c>ICharactersManager</c> from Zenject, call <c>GetCharacter(type)</c> for the guest's <c>CharacterSOData</c>,
    /// and read its <c>_isImposter</c> flag. Imposter → "visitor"; otherwise → "human".
    ///
    /// Unlike the inspection sign (which must NOT leak the answer), the post-shot reveal SHOULD state it plainly: the
    /// player has already committed, and the cutscene tells a sighted player outright. Never throws.
    /// </summary>
    public sealed class ShotNarrator
    {
        private const string GameAsm = "Assembly-CSharp.dll";
        private const string CharactersNs = "_Code.Characters";

        private readonly ISpeechOutput _speech;

        private IntPtr _charactersManagerClass; // ICharactersManager type, resolved via Zenject each use (cached class)
        private IntPtr _getCharacterMethod;     // ICharactersManager.GetCharacter(ECharacterType)
        private IntPtr _characterSoDataClass;   // for reading _isImposter

        public ShotNarrator(ISpeechOutput speech) => _speech = speech;

        /// <summary>A shoot command fired for <paramref name="characterRaw"/> (the Yarn raw name). Resolve the guest,
        /// read whether they were an imposter, and speak the outcome. Logs and stays silent on any resolution miss
        /// rather than guessing.</summary>
        public void OnShot(string? characterRaw)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(characterRaw))
                {
                    MelonLogger.Warning("[ShotNarrator] shoot command with empty character name; skipped.");
                    return;
                }

                if (!TryCharacterType(characterRaw!, out int characterType))
                {
                    // Token mismatch between the Yarn raw name and the ECharacterType enum — log so we catch it in
                    // testing and extend the alias table. Don't announce a guessed outcome.
                    MelonLogger.Warning($"[ShotNarrator] no ECharacterType for shot target '{characterRaw}'; outcome not spoken.");
                    return;
                }

                IntPtr soData = ResolveCharacterData(characterType);
                if (soData == IntPtr.Zero)
                {
                    MelonLogger.Warning($"[ShotNarrator] could not resolve CharacterSOData for '{characterRaw}'; outcome not spoken.");
                    return;
                }

                if (_characterSoDataClass == IntPtr.Zero)
                    _characterSoDataClass = Il2CppRaw.GetClass(GameAsm, CharactersNs, "CharacterSOData");
                IntPtr cls = _characterSoDataClass != IntPtr.Zero
                    ? _characterSoDataClass
                    : IL2CPP.il2cpp_object_get_class(soData);

                bool isImposter = Il2CppRaw.ReadBoolField(soData, cls, "_isImposter");
                _speech.Speak(isImposter ? "You shot a visitor." : "You shot a human.", interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ShotNarrator] OnShot threw: {e.Message}");
            }
        }

        /// <summary>Resolve the guest's <c>CharacterSOData</c> through the live <c>ICharactersManager</c>
        /// (<c>GetCharacter(ECharacterType)</c>), Zenject-resolved. Zero on any miss.</summary>
        private IntPtr ResolveCharacterData(int characterType)
        {
            IntPtr manager = ZenjectResolver.Resolve(CharactersNs, "ICharactersManager");
            if (manager == IntPtr.Zero) return IntPtr.Zero;

            // GetCharacter is declared on the interface; resolve the method off the resolved instance's class.
            if (_getCharacterMethod == IntPtr.Zero || _charactersManagerClass != IL2CPP.il2cpp_object_get_class(manager))
            {
                _charactersManagerClass = IL2CPP.il2cpp_object_get_class(manager);
                _getCharacterMethod = Il2CppRaw.GetMethod(_charactersManagerClass, "GetCharacter", 1);
            }
            if (_getCharacterMethod == IntPtr.Zero) return IntPtr.Zero;

            return Il2CppRaw.InvokeObjectMethodWithEnum(manager, _getCharacterMethod, characterType);
        }

        /// <summary>Map a Yarn raw character name to its <c>ECharacterType</c> underlying int, case-insensitively.
        /// False if the name isn't a known character token.</summary>
        private static bool TryCharacterType(string raw, out int value) =>
            CharacterTypeByName.TryGetValue(raw.Trim(), out value);

        // ECharacterType enum, in declaration order (the underlying int = the index). Transcribed from the decompile
        // (_Code.Characters). The Yarn raw names match these tokens; the lookup is case-insensitive to tolerate the
        // script's casing. If a script uses a different token for some character, add an alias entry here (caught by
        // the "no ECharacterType for shot target" warning during testing).
        private static readonly string[] CharacterTypeNames =
        {
            "Sanya", "TV", "Courier", "Neighbour", "Esenin", "Anxiety", "Daughter", "Cold", "Fan", "Prophet",
            "SuperFake", "Widow", "Scammer", "Doc", "Gasmask", "WolfHound", "Hunter", "NotTrue", "Greatmother",
            "Sexy", "Phone", "Anger", "Luka", "FormerFema", "Foreigner", "Alkonost", "Blind", "Marauder", "Nun",
            "TaxiDriver", "Firefighter", "Fugitive", "Teacher", "Edgar", "Raskolnikov", "GraveDigger", "Provocateur",
            "Mother", "Wifefema", "Vigilante", "Theorist", "Buddy", "FortuneTeller", "Dude", "Bestson", "BigLebowski",
            "Couple", "Fatman", "Wheelchair", "CultistOne", "CultistTwo", "CultistThree", "CultistPriest", "Ballerina",
            "Intruder", "MushroomEater", "Miner", "Sirin", "Empty", "Twins", "Player", "Jacob", "StaticBoy", "Tourist",
            "Unstable", "Rocker", "Experienced", "Jacket", "Tough", "Bald", "Nervous", "Leper", "Fairytaller",
            "FunnyGuy", "Alt",
        };

        private static readonly Dictionary<string, int> CharacterTypeByName = BuildNameTable();

        private static Dictionary<string, int> BuildNameTable()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < CharacterTypeNames.Length; i++)
                d[CharacterTypeNames[i]] = i;
            return d;
        }
    }
}
