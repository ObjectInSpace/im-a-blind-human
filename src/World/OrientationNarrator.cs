using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// "Where am I" readout (bound to a key in <see cref="AccessMod"/>). The game is dialog-driven with sparse rooms
    /// (1–2 interactables each), so a blind player's main spatial need is simply: which room am I in, who's here, and
    /// where's the way out. This reads the current <c>ARoom</c> (captured by <see cref="WorldPatches"/>'s
    /// <c>OnRoomEntered</c> hook into <see cref="RoomTracker"/>) and speaks:
    /// room name + living occupants + the room's door and where it leads.
    ///
    /// All reads are raw IL2CPP off the live <c>ARoom</c> pointer:
    /// - room name: <c>get_RoomType</c> → <c>ERoom</c> (int), mapped to a label;
    /// - occupants: the protected <c>AliveCharactersInside</c> <c>List&lt;ECharacterType&gt;</c>, read via
    ///   <c>get_Count</c>/<c>get_Item</c>, each int mapped to a character label;
    /// - exit: the room's <c>View</c> (an <c>ARoomView</c>) field → its <c>_doorTrigger</c> (a <c>DoorTrigger</c>)
    ///   field → <c>_linkedRoom</c> <c>ERoom</c> field, mapped to a label.
    ///
    /// Hub-and-spoke layout: each room view holds a single <c>_doorTrigger</c>, so "the exit" is one linked room.
    /// Degrades to a spoken fallback if nothing is captured yet (e.g. pressed before entering a room). Never throws.
    /// </summary>
    public sealed class OrientationNarrator
    {
        private const string RoomsNs = "_Code.Infrastructure.Rooms";
        private const string GameAsm = "Assembly-CSharp.dll";

        private readonly ISpeechOutput _speech;

        // Lazily-resolved handles (cached in Il2CppRaw; we cache the resolved bool to avoid re-resolving each press).
        // ARoomView/DoorTrigger/List classes are taken from each instance at read time (il2cpp_object_get_class), so
        // only ARoom + its RoomType getter need pre-resolving here.
        private bool _resolved;
        private IntPtr _aRoomClass;
        private IntPtr _getRoomType;

        public OrientationNarrator(ISpeechOutput speech) => _speech = speech;

        /// <summary>Speak the current-room orientation, interrupting so a repeat press re-reads.</summary>
        public void Announce()
        {
            try
            {
                string? text = Describe();
                _speech.Speak(text ?? "Location not available right now.", interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[OrientationNarrator] Announce threw: {e.Message}");
            }
        }

        private string? Describe()
        {
            IntPtr room = RoomTracker.CurrentRoom;
            if (room == IntPtr.Zero) return null;
            EnsureResolved();

            var sb = new StringBuilder();

            // Room name.
            int roomType = Il2CppRaw.InvokeInt32Getter(room, _getRoomType);
            sb.Append(RoomName(roomType));

            // Occupants (living characters in the room).
            string occupants = DescribeOccupants(room);
            if (occupants.Length > 0) sb.Append(". ").Append(occupants);

            // Exit: room.View._doorTrigger._linkedRoom.
            string exit = DescribeExit(room);
            if (exit.Length > 0) sb.Append(". ").Append(exit);

            sb.Append('.');
            return sb.ToString();
        }

        private string DescribeOccupants(IntPtr room)
        {
            // AliveCharactersInside is a protected List<ECharacterType> field on ARoom.
            IntPtr list = Il2CppRaw.ReadObjectField(room, _aRoomClass, "AliveCharactersInside");
            if (list == IntPtr.Zero) return string.Empty;

            IntPtr listClass = IL2CPP.il2cpp_object_get_class(list);
            IntPtr getCount = Il2CppRaw.GetMethod(listClass, "get_Count", 0);
            IntPtr getItem = Il2CppRaw.GetMethod(listClass, "get_Item", 1);
            int count = Il2CppRaw.InvokeInt32Getter(list, getCount, fallback: 0);
            if (count <= 0) return string.Empty;

            var names = new List<string>();
            for (int i = 0; i < count; i++)
            {
                int ch = Il2CppRaw.InvokeInt32MethodWithEnum(list, getItem, i, fallback: -1);
                if (ch >= 0) names.Add(CharacterName(ch));
            }
            if (names.Count == 0) return string.Empty;
            return names.Count == 1 ? $"{names[0]} is here" : string.Join(", ", names) + " are here";
        }

        private string DescribeExit(IntPtr room)
        {
            // room.View (protected IRoomView field, concretely an ARoomView) -> _doorTrigger -> _linkedRoom.
            IntPtr view = Il2CppRaw.ReadObjectField(room, _aRoomClass, "View");
            if (view == IntPtr.Zero) return string.Empty;
            IntPtr viewClass = IL2CPP.il2cpp_object_get_class(view);
            IntPtr door = Il2CppRaw.ReadObjectField(view, viewClass, "_doorTrigger");
            if (door == IntPtr.Zero) return string.Empty;
            IntPtr doorClass = IL2CPP.il2cpp_object_get_class(door);
            int linked = Il2CppRaw.ReadInt32Field(door, doorClass, "_linkedRoom", fallback: -1);
            if (linked < 0) return string.Empty;
            return $"The door leads to the {RoomName(linked).ToLowerInvariant()}";
        }

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _aRoomClass = Il2CppRaw.GetClass(GameAsm, RoomsNs, "ARoom");
                _getRoomType = Il2CppRaw.GetMethod(_aRoomClass, "get_RoomType", 0);
                MelonLogger.Msg($"[OrientationNarrator] resolved: aRoom={_aRoomClass != IntPtr.Zero} " +
                                $"getRoomType={_getRoomType != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[OrientationNarrator] EnsureResolved threw: {e.Message}");
            }
        }

        // ERoom: Kitchen, Office, BigRoom, Bathroom, Pantry, Entrance, Bedroom.
        private static string RoomName(int e) => e switch
        {
            0 => "Kitchen",
            1 => "Office",
            2 => "Big room",
            3 => "Bathroom",
            4 => "Pantry",
            5 => "Entrance",
            6 => "Bedroom",
            _ => "Unknown room",
        };

        // ECharacterType — only the names likely to appear as room occupants are mapped to readable labels; anything
        // unmapped falls back to a generic label so an unexpected value never crashes or reads as a raw number.
        private static string CharacterName(int e) => e switch
        {
            0 => "Sanya",
            2 => "the courier",
            3 => "the neighbour",
            4 => "Esenin",
            6 => "the daughter",
            8 => "the fan",
            9 => "the prophet",
            11 => "the widow",
            12 => "the scammer",
            13 => "the doctor",
            16 => "the hunter",
            22 => "Luka",
            26 => "the blind man",
            27 => "the marauder",
            28 => "the nun",
            29 => "the taxi driver",
            30 => "the firefighter",
            32 => "the teacher",
            33 => "Edgar",
            _ => "someone",
        };
    }
}
