using System;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Holds the pointer to the <c>ARoom</c> the player most recently entered. <c>RoomsManager</c> exposes no
    /// "current room" property, so we capture it from a Harmony postfix on <c>RoomsManager.OnRoomEntered(ARoom)</c>
    /// (see <see cref="WorldPatches"/>) and read it on demand from the orientation narrator. Just a shared cell —
    /// the pointer is to a live IL2CPP object the game owns; we only read fields/getters off it, never store it long
    /// term across scene loads (a room change overwrites it; a scene unload leaves a stale pointer that reads as
    /// failure, which the narrator treats as "not available").
    /// </summary>
    public static class RoomTracker
    {
        /// <summary>Pointer to the current <c>ARoom</c>, or zero if none entered yet this session.</summary>
        public static IntPtr CurrentRoom { get; set; } = IntPtr.Zero;
    }
}
