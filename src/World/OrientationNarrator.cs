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
    /// where a blind player needs "which way to turn", not survey-grade precision. Never throws.
    /// </summary>
    public sealed class OrientationNarrator
    {
        private const string GameAsm = "Assembly-CSharp.dll";

        private readonly ISpeechOutput _speech;

        // Lazily-resolved handles.
        private bool _resolved;
        private IntPtr _viewProviderClass, _getViews;       // ActionableObjectsViewProvider + get_ActionableObjectViews
        private IntPtr _viewClass, _getCanShowHint;          // AActionableObjectView + get_CanShowHint
        private IntPtr _playerService, _getPosition, _getLookDirection;

        public OrientationNarrator(ISpeechOutput speech) => _speech = speech;

        /// <summary>Speak the currently-selectable interactables with bearings, interrupting so a repeat re-reads.</summary>
        public void Announce()
        {
            try
            {
                string? text = Describe();
                _speech.Speak(text ?? "Nothing to interact with right now.", interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[OrientationNarrator] Announce threw: {e.Message}");
            }
        }

        private string? Describe()
        {
            EnsureResolved();

            // Player pose for bearings.
            Vector3 pos = _playerService != IntPtr.Zero ? Il2CppRaw.InvokeVector3Getter(_playerService, _getPosition) : Vector3.zero;
            Vector3 look = _playerService != IntPtr.Zero ? Il2CppRaw.InvokeVector3Getter(_playerService, _getLookDirection) : Vector3.forward;

            // Live interactable set.
            IntPtr provider = _viewProviderClass != IntPtr.Zero ? Il2CppRaw.FindObjectOfType(_viewProviderClass) : IntPtr.Zero;
            if (provider == IntPtr.Zero) return null;
            IntPtr arrayPtr = Il2CppRaw.InvokeObjectGetter(provider, _getViews);
            IntPtr[] views = Il2CppRaw.ReadObjectArray(arrayPtr);
            if (views.Length == 0) return null;

            var parts = new List<string>();
            foreach (IntPtr v in views)
            {
                if (v == IntPtr.Zero) continue;
                // Only things the game currently offers as interactable.
                if (!Il2CppRaw.InvokeBoolGetter(v, _getCanShowHint)) continue;

                string name = HumanizeName(Il2CppRaw.GetUnityObjectName(v));
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

        /// <summary>Turn a GameObject name into something speakable: drop common suffixes, split underscores.</summary>
        private static string HumanizeName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "object";
            string s = raw;
            // Strip a trailing "(Clone)" and numeric/index suffixes like "_01".
            int clone = s.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
            if (clone >= 0) s = s.Substring(0, clone);
            s = s.Replace('_', ' ').Trim();
            // Reuse the shared cleaner (also collapses whitespace / strips stray markup).
            string cleaned = ControlDescriber.Clean(s);
            return string.IsNullOrWhiteSpace(cleaned) ? "object" : cleaned;
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

                _viewClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure.ActionableObjects", "AActionableObjectView");
                if (_viewClass != IntPtr.Zero)
                    _getCanShowHint = Il2CppRaw.GetMethod(_viewClass, "get_CanShowHint", 0);

                _playerService = ZenjectResolver.Resolve("_Code.Infrastructure.Player", "IPlayerService");
                if (_playerService != IntPtr.Zero)
                {
                    IntPtr psClass = IL2CPP.il2cpp_object_get_class(_playerService);
                    _getPosition = Il2CppRaw.GetMethod(psClass, "get_Position", 0);
                    _getLookDirection = Il2CppRaw.GetMethod(psClass, "get_LookDirection", 0);
                }

                MelonLogger.Msg($"[OrientationNarrator] resolved: provider={_viewProviderClass != IntPtr.Zero} " +
                                $"getViews={_getViews != IntPtr.Zero} canShowHint={_getCanShowHint != IntPtr.Zero} " +
                                $"player={_playerService != IntPtr.Zero} getPos={_getPosition != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[OrientationNarrator] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
