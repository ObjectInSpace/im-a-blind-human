using System;
using System.Collections.Generic;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Menus;
using NoImNotAHumanAccess.Speech;
using UnityEngine;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// 2D room-photo / close-up object stepper. The photo highlights via a FREE CURSOR (mouse raycast onto
    /// <c>UIButton</c> sprites) — useless for a blind player. So we let the player STEP between the real highlightable
    /// objects with arrow keys, by WARPING THE GAME'S OWN MOUSE onto each one in turn: the game's <c>UIRayCaster</c>
    /// then hovers it natively (outline + our existing <c>UIButton.OnHover</c> narration), and the game's own Enter
    /// selects it (confirmed: Enter activates the cursor-highlighted object). We supply discrete motion, nothing else.
    ///
    /// The highlightable set = scene <c>_Code.Rooms.UIButton</c>s with <c>goActive==True</c> (the F8 dump showed ~80
    /// total buttons but only a few active — the inactive ~76 are the whole character/object roster pre-instantiated
    /// off-screen at a shared dummy position; filtering by GameObject-active narrows to THIS photo's objects). Ordered
    /// left→right by screen X so stepping reads spatially.
    ///
    /// Zenject-free: buttons via <c>FindObjectsByType</c>; the hover camera via the static <c>UIRayCaster.Instance</c>;
    /// the warp via Input System <c>Mouse.current.WarpCursorPosition</c>. F8 still dumps the raw set for diagnostics.
    /// </summary>
    public sealed class TwoDProbe
    {
        private const string GameAsm = "Assembly-CSharp.dll";

        private readonly ISpeechOutput _speech;
        private bool _resolved;
        private IntPtr _uiButtonClass, _getIsActive;
        private IntPtr _rayCasterClass, _getInstance;  // UIRayCaster + static get_Instance

        private IntPtr _selected; // last-warped button, to keep selection across rebuilds

        public TwoDProbe(ISpeechOutput speech) => _speech = speech;

        /// <summary>Step to the next (or previous) highlightable object: warp the mouse onto it so the game hovers it.</summary>
        public void Step(bool backwards)
        {
            try
            {
                EnsureResolved();
                List<IntPtr> buttons = ActiveButtonsLeftToRight(out IntPtr camera);
                if (buttons.Count == 0)
                {
                    _speech.Speak("No objects to select here.", interrupt: true);
                    _selected = IntPtr.Zero;
                    return;
                }

                int idx = MenuStepUtil.NextIndex(buttons.IndexOf(_selected), buttons.Count, backwards);

                IntPtr btn = buttons[idx];
                _selected = btn;

                string name = Humanize(Il2CppRaw.GetUnityObjectName(btn));
                Vector3 world = Il2CppRaw.GetComponentWorldPosition(btn);
                Vector3 screen = camera != IntPtr.Zero ? Il2CppRaw.WorldToScreenPoint(camera, world) : Vector3.zero;
                bool warped = Il2CppRaw.WarpMouse(new Vector2(screen.x, screen.y));

                // Do NOT speak the name here: warping the cursor onto the button fires the game's UIButton.OnHover,
                // which RoomViewNarrator already narrates. Speaking here too caused the object to be announced TWICE.
                // The hover hook is the single source of truth (it matches what's actually highlighted).
                MelonLogger.Msg($"[TwoDProbe] step {idx + 1}/{buttons.Count} '{name}' " +
                                $"screen=({screen.x:F0},{screen.y:F0}) warped={warped}.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[TwoDProbe] Step threw: {e.Message}");
            }
        }

        /// <summary>
        /// Whether a 2D room photo / close-up is currently up — i.e. there is at least one goActive
        /// <c>UIButton</c> in the scene. Used to route the shared arrow/Enter keys to the 2D stepper vs. the 3D menu:
        /// in a 3D walking scene no photo buttons are active, so this is false and arrows drive the 3D list instead.
        /// </summary>
        public bool IsPhotoActive()
        {
            try
            {
                EnsureResolved();
                foreach (IntPtr b in Il2CppRaw.FindObjectsByType(_uiButtonClass, includeInactive: false))
                    if (b != IntPtr.Zero && Il2CppRaw.GetComponentGameObjectActive(b)) return true;
            }
            catch { /* treat as not-photo on any failure */ }
            return false;
        }

        /// <summary>
        /// The CHOOSABLE photo buttons, ordered left→right by screen X, plus the hover camera. Filters to buttons that
        /// are BOTH GameObject-active (in this photo) AND <c>UIButton.IsActive</c> — the game's "can be interacted with
        /// now" flag. The F8 dump proved the distinction: choosable objects (Bed/Window) are goActive=True + IsActive=
        /// True, while present-but-unchoosable ones (e.g. the TV unviewable at game start) are goActive=True +
        /// IsActive=False. Skipping the latter stops arrows routing silently to objects that can't be selected.
        /// </summary>
        private List<IntPtr> ActiveButtonsLeftToRight(out IntPtr camera)
        {
            camera = ResolveHoverCamera();
            var list = new List<(IntPtr btn, float x)>();
            foreach (IntPtr b in Il2CppRaw.FindObjectsByType(_uiButtonClass, includeInactive: false))
            {
                if (b == IntPtr.Zero) continue;
                if (!Il2CppRaw.GetComponentGameObjectActive(b)) continue; // only THIS photo's objects
                if (_getIsActive != IntPtr.Zero && !Il2CppRaw.InvokeBoolGetter(b, _getIsActive)) continue; // choosable now
                Vector3 world = Il2CppRaw.GetComponentWorldPosition(b);
                Vector3 screen = camera != IntPtr.Zero ? Il2CppRaw.WorldToScreenPoint(camera, world) : world;
                list.Add((b, screen.x));
            }
            list.Sort((a, c) => a.x.CompareTo(c.x));
            var result = new List<IntPtr>(list.Count);
            foreach (var e in list) result.Add(e.btn);
            return result;
        }

        private IntPtr ResolveHoverCamera()
        {
            if (_rayCasterClass == IntPtr.Zero || _getInstance == IntPtr.Zero) return Il2CppRaw.MainCameraPtr();
            IntPtr exc = IntPtr.Zero;
            IntPtr inst = Il2CppRaw.InvokeStaticObjectGetter(_getInstance);
            if (inst == IntPtr.Zero) return Il2CppRaw.MainCameraPtr();
            IntPtr cam = Il2CppRaw.ReadObjectField(inst, _rayCasterClass, "_camera");
            return cam != IntPtr.Zero ? cam : Il2CppRaw.MainCameraPtr();
        }

        /// <summary>F8: dump the raw UIButton set (diagnostic for the 2D stepper's source).</summary>
        public void Dump()
        {
            try
            {
                EnsureResolved();
                IntPtr[] active = Il2CppRaw.FindObjectsByType(_uiButtonClass, includeInactive: false);
                IntPtr[] all = Il2CppRaw.FindObjectsByType(_uiButtonClass, includeInactive: true);
                MelonLogger.Msg($"[TwoDProbe] active-only={active.Length} total={all.Length}");
                IntPtr cam = ResolveHoverCamera();
                int shown = 0;
                foreach (IntPtr b in all)
                {
                    if (b == IntPtr.Zero) continue;
                    if (!Il2CppRaw.GetComponentGameObjectActive(b)) continue;
                    string name = Humanize(Il2CppRaw.GetUnityObjectName(b));
                    Vector3 world = Il2CppRaw.GetComponentWorldPosition(b);
                    Vector3 screen = cam != IntPtr.Zero ? Il2CppRaw.WorldToScreenPoint(cam, world) : Vector3.zero;
                    MelonLogger.Msg($"[TwoDProbe]   active '{name}' world=({world.x:F1},{world.y:F1},{world.z:F1}) " +
                                    $"screen=({screen.x:F0},{screen.y:F0})");
                    shown++;
                }
                _speech.Speak($"Two D probe: {shown} active objects in this photo. See log.", interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[TwoDProbe] Dump threw: {e.Message}");
            }
        }

        private static string Humanize(string? raw) => MenuStepUtil.Humanize(raw);

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _uiButtonClass = Il2CppRaw.GetClass(GameAsm, "_Code.Rooms", "UIButton");
                if (_uiButtonClass != IntPtr.Zero)
                    _getIsActive = Il2CppRaw.GetMethod(_uiButtonClass, "get_IsActive", 0);

                _rayCasterClass = Il2CppRaw.GetClass(GameAsm, "_Code.Rooms", "UIRayCaster");
                if (_rayCasterClass != IntPtr.Zero)
                    _getInstance = Il2CppRaw.GetMethod(_rayCasterClass, "get_Instance", 0);

                MelonLogger.Msg($"[TwoDProbe] resolved: uiButton={_uiButtonClass != IntPtr.Zero} " +
                                $"rayCaster={_rayCasterClass != IntPtr.Zero} getInstance={_getInstance != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[TwoDProbe] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
