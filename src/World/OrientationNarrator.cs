using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Menus;
using NoImNotAHumanAccess.Speech;
using UnityEngine;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// "What can I interact with, and where is it relative to me" readout (bound to F10 in <see cref="AccessMod"/>).
    /// The game is first-person aim-to-interact, so the player's spatial question is a world bearing: which way to
    /// turn to face each currently-selectable thing. We enumerate the game's live interactable set and, for each one
    /// the game currently offers (its <c>CanShowHint</c> is true — i.e. selectable right now, respecting time-of-day/
    /// lock state), speak its name and a coarse direction + distance relative to where the player is facing.
    ///
    /// Sources:
    /// - Interactables: <c>ActionableObjectsViewProvider.ActionableObjectViews</c> (an <c>AActionableObjectView[]</c>;
    ///   the provider is a MonoBehaviour, so found via <c>FindObjectOfType</c>). Each view's world position comes from
    ///   its managed <c>transform.position</c>; its name from the GameObject name (humanized) — robust without
    ///   resolving the LocalizedString subject; upgrade later if names read poorly by ear.
    /// - "Selectable now": the view's <c>CanShowHint</c> bool getter — the game's own "show a hint for this" signal.
    /// - Player pose: <c>IPlayerService.Position</c> + <c>LookDirection</c>, resolved from the Zenject container (same
    ///   path <see cref="GameStateAccess"/> uses; controllers aren't MonoBehaviours so FindObjectOfType can't reach them).
    ///
    /// Bearing is coarse (ahead / behind / to your left / to your right) + near/far, matching sparse rooms (1–2 things)
    /// where a blind player needs "which way to turn", not survey-grade precision.
    ///
    /// Also appends any CORPSES present (via <see cref="CorpseNarrator"/>) — dead characters are de-buttoned, so the
    /// hover/stepper narration never speaks them, yet a blind player still wants to know a body is in the room and
    /// whether it was a human or a visitor. Corpses are reported even when there are no interactables. Never throws.
    /// </summary>
    public sealed class OrientationNarrator
    {
        private const string GameAsm = "Assembly-CSharp.dll";

        private readonly ISpeechOutput _speech;
        private readonly CorpseNarrator _corpses;

        // Lazily-resolved handles.
        private bool _resolved;
        private IntPtr _viewProviderClass, _getViews;       // ActionableObjectsViewProvider + get_ActionableObjectViews
        private IntPtr _playerService, _getPosition, _getLookDirection;

        public OrientationNarrator(ISpeechOutput speech, CorpseNarrator corpses)
        {
            _speech = speech;
            _corpses = corpses;
        }

        /// <summary>
        /// Speak the orientation readout, interrupting so a repeat re-reads. In a 2D room photo
        /// (<paramref name="corpsesOnly"/>) the 3D interactable bearings are meaningless, so we speak ONLY the corpses
        /// present (and say so if there are none). Otherwise we speak the selectable interactables with bearings, plus
        /// any corpses.
        /// </summary>
        public void Announce(bool corpsesOnly = false)
        {
            try
            {
                string? corpses = _corpses.Describe();

                if (corpsesOnly)
                {
                    _speech.Speak(corpses ?? "No corpses here.", interrupt: true);
                    return;
                }

                // Corpses are appended even when there's nothing to interact with — a body in the room is worth saying
                // on its own. Only "nothing to interact with" when BOTH are empty.
                string? interactables = DescribeInteractables();
                string text =
                    (interactables, corpses) switch
                    {
                        (not null, not null) => $"{interactables} {corpses}",
                        (not null, null) => interactables!,
                        (null, not null) => corpses!,
                        _ => "Nothing to interact with right now.",
                    };
                _speech.Speak(text, interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[OrientationNarrator] Announce threw: {e.Message}");
            }
        }

        private string? DescribeInteractables()
        {
            EnsureResolved();

            // Player pose for bearings.
            Vector3 pos = _playerService != IntPtr.Zero ? Il2CppRaw.InvokeVector3Getter(_playerService, _getPosition) : Vector3.zero;
            Vector3 look = _playerService != IntPtr.Zero ? Il2CppRaw.InvokeVector3Getter(_playerService, _getLookDirection) : Vector3.forward;

            // Live interactable set. The provider's GameObject may be inactive, so this find includes inactive objects.
            IntPtr provider = Il2CppRaw.FindObjectIncludingInactive(_viewProviderClass);
            if (provider == IntPtr.Zero) return null;
            IntPtr arrayPtr = Il2CppRaw.InvokeObjectGetter(provider, _getViews);
            IntPtr[] views = Il2CppRaw.ReadObjectArray(arrayPtr);
            if (views.Length == 0) return null;

            var parts = new List<string>();
            foreach (IntPtr v in views)
            {
                if (v == IntPtr.Zero) continue;
                // NOTE: we do NOT gate on CanShowHint — ActionMenu found it returns false for EVERY view in a live
                // scene (the decompiled `CanShowHint => false` is a stripped placeholder, not the real body), which is
                // exactly why F10 wrongly said "nothing to interact with". List every live view instead.

                // Shared humanizer: strips the "…ObjectTrigger" boilerplate and reorders "Door Kitchen" → "kitchen
                // door", so F10 reads "kitchen door, to your left" like the action menu (not "door kitchen object trigger").
                string name = MenuStepUtil.Humanize(Il2CppRaw.GetUnityObjectName(v));
                Vector3 target = Il2CppRaw.GetComponentWorldPosition(v);
                string bearing = Bearing(pos, look, target);
                parts.Add(bearing.Length > 0 ? $"{name}, {bearing}" : name);
            }

            if (parts.Count == 0) return null;
            return string.Join(". ", parts) + ".";
        }

        /// <summary>
        /// Coarse direction + distance of <paramref name="target"/> relative to a player at <paramref name="pos"/>
        /// facing <paramref name="look"/>. Uses the horizontal plane only (XZ); "left/right" from the signed angle,
        /// "ahead/behind" from the forward dot, plus near/far by distance. Returns "" if essentially on top of it.
        /// </summary>
        private static string Bearing(Vector3 pos, Vector3 look, Vector3 target)
        {
            Vector3 to = target - pos;
            to.y = 0f;
            float dist = to.magnitude;
            if (dist < 0.5f) return "right here";

            Vector3 fwd = look; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 dir = to / dist;

            float forwardDot = Vector3.Dot(fwd, dir);                         // 1 ahead, -1 behind
            float rightDot = Vector3.Dot(Vector3.Cross(Vector3.up, fwd), dir); // >0 right, <0 left

            string facing;
            if (forwardDot > 0.5f) facing = "ahead";
            else if (forwardDot < -0.5f) facing = "behind you";
            else facing = rightDot >= 0f ? "to your right" : "to your left";
            // For the ahead/behind cone, add the side so the player knows which way to turn.
            if ((facing == "ahead" || facing == "behind you") && Mathf.Abs(rightDot) > 0.25f)
                facing += rightDot >= 0f ? " and right" : " and left";

            string range = dist < 2.5f ? "close" : dist < 6f ? "" : "far";
            return range.Length > 0 ? $"{facing}, {range}" : facing;
        }


        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _viewProviderClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure.ActionableObjects", "ActionableObjectsViewProvider");
                if (_viewProviderClass != IntPtr.Zero)
                    _getViews = Il2CppRaw.GetMethod(_viewProviderClass, "get_ActionableObjectViews", 0);

                _playerService = ZenjectResolver.Resolve("_Code.Infrastructure.Player", "IPlayerService");
                if (_playerService != IntPtr.Zero)
                {
                    IntPtr psClass = IL2CPP.il2cpp_object_get_class(_playerService);
                    _getPosition = Il2CppRaw.GetMethod(psClass, "get_Position", 0);
                    _getLookDirection = Il2CppRaw.GetMethod(psClass, "get_LookDirection", 0);
                }

                MelonLogger.Msg($"[OrientationNarrator] resolved: provider={_viewProviderClass != IntPtr.Zero} " +
                                $"getViews={_getViews != IntPtr.Zero} " +
                                $"player={_playerService != IntPtr.Zero} getPos={_getPosition != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[OrientationNarrator] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
