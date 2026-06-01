using System;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using UnityEngine;
using UnityEngine.UI;

namespace NoImNotAHumanAccess.Menus
{
    /// <summary>
    /// Turns a focused UI GameObject into a spoken description: label, role, and current value.
    ///
    /// Role/value detection prefers the standard uGUI control on the object — Toggle, Slider, Dropdown — which
    /// is how a screen reader normally derives role and state, and generalizes beyond this game's settings
    /// classes. UnityEngine.UI types (Toggle, Slider) are not behind the Il2CppInterop dual-naming wall, so we
    /// read them with normal interop generics; TMP text and the TMP dropdown caption go through the raw path
    /// (see <see cref="Il2CppRaw"/>).
    /// </summary>
    public static class ControlDescriber
    {
        /// <summary>Build "label, role, value" style text for the focused control. Never throws.</summary>
        public static string Describe(GameObject go)
        {
            try
            {
                // Order matters. The game's selector controls (fullscreen/resolution/language via
                // ScrollableDropdown) wrap a TMP_Dropdown AND carry a stray Toggle in their subtree, so we must
                // check dropdown BEFORE toggle or they get mislabeled "checkbox". (Verified live + in source:
                // ScreenSettingsInstance._fullScreenDropdown/_resolutionDropdown are ScrollableDropdown.)
                //
                // For these rows the setting NAME is only the GameObject name (no name-label TMP in the row —
                // verified live: the row's "Label" TMP is the caption=value, and Template/Item labels are
                // options). So: name from the GameObject, value from the caption.
                string? dropdownValue = TryReadDropdownCaption(go);
                if (dropdownValue != null)
                {
                    string ddName = Humanize(go.name);
                    if (LabelIsRedundant(ddName, dropdownValue)) return $"{dropdownValue}, dropdown";
                    return $"{ddName}, dropdown, {dropdownValue}";
                }

                // Real slider (volume). The name + value live on the SoundSettingsVolumeSlider component as
                // sibling references (_groupNameText / _valueText), NOT in the focused object's own subtree
                // (verified live: VolumeSlider node has no child TMPs). Read those directly.
                var slider = go.GetComponentInChildren<Slider>(true);
                if (slider != null)
                {
                    // Two slider families: SoundSettingsVolumeSlider (has a localized _groupNameText) and
                    // FakeSlider (sensitivity sliders — value only, name comes from the row). Both expose a
                    // _valueText for the display value.
                    string sName = ReadVolumeGroupName(go) ?? ReadSliderRowLabel(go) ?? Humanize(go.name);
                    string sValue = ReadVolumeValueText(go) ?? ReadFakeSliderValueText(go) ?? FormatSlider(slider);
                    return $"{sName}, slider, {sValue}";
                }

                // Real toggle (e.g. vsync, typewriter). "<label>, on/off" — role implied by on/off.
                var toggle = go.GetComponentInChildren<Toggle>(true);
                if (toggle != null)
                    return $"{ReadLabel(go)}, {(toggle.isOn ? "on" : "off")}";

                // Plain button / unknown selectable: just the label. (A dialogue-choice style word — energy/gun/
                // give-item — was prototyped here but parked: the choice TEXT usually already conveys it, so it
                // read as redundant. NoteDialogButtonStyle still logs styles passively to gather evidence for a
                // later, data-driven decision; see project_status.md.)
                NoteDialogButtonStyle(go);
                return ReadLabel(go);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ControlDescriber] Describe: {e.Message}");
                return Clean(go.name);
            }
        }

        /// <summary>
        /// The current spoken VALUE of the focused control only (no label), for detecting in-place changes
        /// while focus stays put (slider drag, toggle flip, dropdown cycle). Empty if the control has no value.
        /// </summary>
        public static string ReadValue(GameObject go)
        {
            try
            {
                string? dropdownValue = TryReadDropdownCaption(go);
                if (dropdownValue != null) return dropdownValue;

                var slider = go.GetComponentInChildren<Slider>(true);
                if (slider != null) return ReadVolumeValueText(go) ?? ReadFakeSliderValueText(go) ?? FormatSlider(slider);

                var toggle = go.GetComponentInChildren<Toggle>(true);
                if (toggle != null) return toggle.isOn ? "on" : "off";
            }
            catch { /* fall through */ }
            return string.Empty;
        }

        /// <summary>True if the label adds nothing over the value (e.g. both "English"), case-insensitive.</summary>
        private static bool LabelIsRedundant(string label, string value) =>
            string.Equals(label?.Trim(), value?.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Trim a trailing word like "slider" off a label so we don't say "Volume slider, slider".</summary>
        private static string StripTrailing(string label, string word)
        {
            if (string.IsNullOrEmpty(label)) return label;
            string trimmed = label.TrimEnd();
            if (trimmed.EndsWith(word, StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - word.Length).TrimEnd();
            return string.IsNullOrEmpty(trimmed) ? label : trimmed;
        }

        private static string FormatSlider(Slider s)
        {
            try
            {
                float min = s.minValue, max = s.maxValue, v = s.value;
                if (Math.Abs(max - min) < 0.0001f) return v.ToString("0.##");
                // Normalised 0..1 (or 0..100) volume-style sliders read best as a percent.
                float pct = (v - min) / (max - min) * 100f;
                return $"{Math.Round(pct)} percent";
            }
            catch { return string.Empty; }
        }

        // ---- Position in list ("2 of 3") ----
        //
        // Neither the dialogue choice buttons (HoverableButton) nor the menu rows (UISelectable) are uGUI
        // Selectable subclasses — they implement ISelectHandler directly — so there is no Navigation graph or
        // built-in index to read. We derive position from the transform tree: find the nearest ancestor that
        // contains 2+ selectable descendants (the "group"), then count the active selectable members and the
        // focused control's ordinal among them, in hierarchy order. The ancestor climb (not just direct siblings)
        // tolerates one layer of per-item wrapper objects. A group of 1 yields no position (single buttons stay
        // clean). A one-time diagnostic logs the first count so the numbers can be verified in-game.

        private static IntPtr _hoverableButtonClass;
        private static IntPtr _uiSelectableClass;
        private static bool _selectableResolved;

        private static void EnsureSelectableResolved()
        {
            if (_selectableResolved) return;
            _selectableResolved = true;
            // Namespaces are the RUNTIME names (interop strips the Il2Cpp prefix): HoverableButton lives in
            // _Code.Characters.DialogSystem, UISelectable in _Code.Utils.UI. Verified against the interop assembly.
            _hoverableButtonClass = Il2CppRaw.GetClass("Assembly-CSharp.dll", "_Code.Characters.DialogSystem", "HoverableButton");
            _uiSelectableClass = Il2CppRaw.GetClass("Assembly-CSharp.dll", "_Code.Utils.UI", "UISelectable");
        }

        /// <summary>
        /// True if <paramref name="go"/> is itself a navigable control: a dialogue choice button, a menu
        /// selectable, or a standard uGUI Slider/Toggle/Dropdown. Used to identify the members of a list group.
        /// Checks the object itself (not children) so a row container isn't double-counted with its control.
        /// </summary>
        private static bool IsSelectable(GameObject go)
        {
            if (go == null) return false;
            EnsureSelectableResolved();
            EnsureTmpResolved();
            if (_hoverableButtonClass != IntPtr.Zero && Il2CppRaw.GetComponent(go, _hoverableButtonClass) != IntPtr.Zero) return true;
            if (_uiSelectableClass != IntPtr.Zero && Il2CppRaw.GetComponent(go, _uiSelectableClass) != IntPtr.Zero) return true;
            // Standard uGUI controls (typed interop works for UnityEngine.UI here).
            if (go.GetComponent<Slider>() != null) return true;
            if (go.GetComponent<Toggle>() != null) return true;
            if (_dropdownClass != IntPtr.Zero && Il2CppRaw.GetComponent(go, _dropdownClass) != IntPtr.Zero) return true;
            return false;
        }

        // Distinct HoverableButton.style ints seen this session, for the one-time-per-value diagnostic.
        private static readonly System.Collections.Generic.HashSet<int> _loggedStyles = new();

        /// <summary>
        /// PARKED FEATURE, data collection only: log each distinct dialogue-choice <c>EDialogButtonStyle</c> seen
        /// (with the focused control's name), once per value per session. Reads the int-backed
        /// <c>HoverableButton.style</c> field by offset. Speaking the style as a suffix was prototyped and dropped
        /// (the choice text usually already conveys energy/gun/give-item, making it redundant); this passive log
        /// builds up which styles real choices actually use, so the call can be revisited with evidence later.
        /// EDialogButtonStyle order: 0 Default, 1 Default_UI, 2 Energy, 3 ConsumablesGet, 4 ConsumablesGive,
        /// 5 Gun, 6 Pause_UI, 7 Skip. Never throws.
        /// </summary>
        private static void NoteDialogButtonStyle(GameObject go)
        {
            EnsureSelectableResolved();
            if (_hoverableButtonClass == IntPtr.Zero) return;
            try
            {
                IntPtr btn = Il2CppRaw.GetComponent(go, _hoverableButtonClass);
                if (btn == IntPtr.Zero) return;

                int style = Il2CppRaw.ReadInt32Field(btn, _hoverableButtonClass, "style", fallback: -1);
                if (style < 0) return;

                if (_loggedStyles.Add(style))
                    MelonLogger.Msg($"[ControlDescriber] dialog button style={style} on '{go.name}'");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ControlDescriber] NoteDialogButtonStyle: {e.Message}");
            }
        }

        /// <summary>
        /// The list group a focused control belongs to: the group container, the focused control's 1-based
        /// position, and the member count. <see cref="Group"/> is null when the control isn't part of a
        /// multi-item group (so single controls produce no position and no category announcement).
        /// </summary>
        public readonly struct GroupContext
        {
            public readonly Transform? Group;
            public readonly int Index;
            public readonly int Total;
            public GroupContext(Transform? group, int index, int total) { Group = group; Index = index; Total = total; }
            public bool HasGroup => Group != null && Total >= 2;
            /// <summary>Stable id of the group container for change-detection, or 0 if none.</summary>
            public int GroupId => Group != null ? Group.gameObject.GetInstanceID() : 0;
        }

        /// <summary>
        /// Resolve the focused control's list group: climb to the nearest ancestor whose subtree holds 2+
        /// selectables (tolerating one layer of per-item wrappers), then count members and the focused ordinal.
        /// Never throws.
        /// </summary>
        public static GroupContext ResolveGroup(GameObject go)
        {
            try
            {
                if (go == null || go.transform == null) return default;

                Transform? group = null;
                Transform? t = go.transform.parent;
                int climb = 0;
                while (t != null && climb++ < 4)
                {
                    if (CountSelectables(t, go, out _, out _) >= 2) { group = t; break; }
                    t = t.parent;
                }
                if (group == null) return default;

                int total = CountSelectables(group, go, out int index, out bool found);
                if (!found || total < 2) return default;

                return new GroupContext(group, index, total);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ControlDescriber] ResolveGroup: {e.Message}");
                return default;
            }
        }

        /// <summary>" N of M" suffix for the position within a group, or empty for a non-group control. Leading
        /// space included so callers can append directly.</summary>
        public static string DescribePosition(in GroupContext ctx) =>
            ctx.HasGroup ? $", {ctx.Index} of {ctx.Total}" : string.Empty;

        /// <summary>
        /// A spoken category/header name for a group container, or empty if none is found. Heuristic: the first
        /// TMP_Text in the group's subtree that is NOT inside one of the selectable members (i.e. a header label,
        /// not a control's own caption) and isn't value-like. Containers are often named for layout ('Buttons',
        /// 'Content') so the GameObject name is a poor fallback and is deliberately not used.
        /// </summary>
        public static string DescribeGroupLabel(Transform group)
        {
            if (group == null) return string.Empty;
            try
            {
                EnsureTmpResolved();
                return ScanForGroupHeader(group, 0) ?? string.Empty;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ControlDescriber] DescribeGroupLabel: {e.Message}");
                return string.Empty;
            }
        }

        /// <summary>Find a header TMP in the group subtree, skipping selectable members' own subtrees.</summary>
        private static string? ScanForGroupHeader(Transform t, int depth)
        {
            if (t == null || depth > 5) return null;
            GameObject go = t.gameObject;
            // Don't descend into a control's own subtree — its text is the control's caption, not a header.
            if (depth > 0 && go.activeInHierarchy && IsSelectable(go)) return null;

            if (go.activeInHierarchy)
            {
                IntPtr tmp = Il2CppRaw.GetComponent(go, _tmpTextClass);
                if (tmp != IntPtr.Zero)
                {
                    string? text = Il2CppRaw.InvokeStringGetter(tmp, _tmpGetText);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string c = Clean(text!);
                        if (c.Length > 0 && !IsValueLike(c)) return c;
                    }
                }
            }
            int kids = t.childCount;
            for (int i = 0; i < kids; i++)
            {
                string? r = ScanForGroupHeader(t.GetChild(i), depth + 1);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>
        /// Count active selectable controls in <paramref name="root"/>'s subtree (hierarchy order), and report the
        /// 1-based ordinal of <paramref name="target"/> among them. A selectable is not descended into (a control's
        /// own children — e.g. its label — are not separate members).
        /// </summary>
        private static int CountSelectables(Transform root, GameObject target, out int targetIndex, out bool found)
        {
            int count = 0;
            targetIndex = 0;
            found = false;
            CountSelectablesRec(root, target, ref count, ref targetIndex, ref found, 0);
            return count;
        }

        private static void CountSelectablesRec(Transform t, GameObject target, ref int count, ref int targetIndex,
                                                ref bool found, int depth)
        {
            if (t == null || depth > 6) return;
            GameObject go = t.gameObject;
            if (go.activeInHierarchy && IsSelectable(go))
            {
                count++;
                if (go.GetInstanceID() == target.GetInstanceID()) { targetIndex = count; found = true; }
                return; // don't descend into a control's own subtree
            }
            int kids = t.childCount;
            for (int i = 0; i < kids; i++)
                CountSelectablesRec(t.GetChild(i), target, ref count, ref targetIndex, ref found, depth + 1);
        }

        // ---- TMP reads via raw IL2CPP (dual-naming wall) ----

        private static IntPtr _tmpTextClass;
        private static IntPtr _tmpGetText;
        private static IntPtr _dropdownClass;
        private static bool _tmpResolved;

        private static void EnsureTmpResolved()
        {
            if (_tmpResolved) return;
            _tmpResolved = true;
            _tmpTextClass = Il2CppRaw.GetClass("Unity.TextMeshPro.dll", "TMPro", "TMP_Text");
            _tmpGetText = Il2CppRaw.GetMethod(_tmpTextClass, "get_text", 0);
            _dropdownClass = Il2CppRaw.GetClass("Unity.TextMeshPro.dll", "TMPro", "TMP_Dropdown");
        }

        /// <summary>Read the first TMP_Text label in the subtree via raw IL2CPP. Falls back to GameObject name.</summary>
        public static string ReadLabel(GameObject go)
        {
            EnsureTmpResolved();
            try
            {
                IntPtr comp = Il2CppRaw.GetComponentInChildren(go, _tmpTextClass);
                string? text = Il2CppRaw.InvokeStringGetter(comp, _tmpGetText);
                if (!string.IsNullOrWhiteSpace(text)) return Clean(text!);
            }
            catch (Exception e) { MelonLogger.Warning($"[ControlDescriber] ReadLabel: {e.Message}"); }
            return Clean(go.name);
        }

        /// <summary>
        /// If the control is/has a TMP_Dropdown, read its current caption text (the selected option). Returns
        /// null if there is no dropdown. The caption is itself a TMP_Text, so we read its get_text.
        /// </summary>
        private static unsafe string? TryReadDropdownCaption(GameObject go)
        {
            EnsureTmpResolved();
            if (_dropdownClass == IntPtr.Zero) return null;
            try
            {
                IntPtr dd = Il2CppRaw.GetComponentInChildren(go, _dropdownClass);
                if (dd == IntPtr.Zero) return null;

                // Get the captionText (a TMP_Text) field/property, then its text.
                IntPtr getCaption = Il2CppRaw.GetMethod(_dropdownClass, "get_captionText", 0);
                if (getCaption == IntPtr.Zero) return null;
                IntPtr exc = IntPtr.Zero;
                IntPtr caption = IL2CPP_runtime_invoke(getCaption, dd, ref exc);
                if (exc != IntPtr.Zero || caption == IntPtr.Zero) return null;

                string? text = Il2CppRaw.InvokeStringGetter(caption, _tmpGetText);
                return string.IsNullOrWhiteSpace(text) ? null : Clean(text!);
            }
            catch (Exception e) { MelonLogger.Warning($"[ControlDescriber] dropdown: {e.Message}"); return null; }
        }

        private static unsafe IntPtr IL2CPP_runtime_invoke(IntPtr method, IntPtr obj, ref IntPtr exc) =>
            Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, obj, (void**)0, ref exc);

        // ---- Volume slider name/value (read SoundSettingsVolumeSlider sibling references) ----

        private static IntPtr _volSliderClass;
        private static IntPtr _localizeStringEventClass;
        private static IntPtr _getLocalizedString;
        private static bool _volResolved;

        private static IntPtr _fakeSliderClass;

        /// <summary>Read FakeSlider._valueText (sensitivity sliders). Null if not a FakeSlider.</summary>
        private static string? ReadFakeSliderValueText(GameObject go)
        {
            EnsureVolResolved();
            if (_fakeSliderClass == IntPtr.Zero) return null;
            try
            {
                IntPtr fs = Il2CppRaw.GetComponentInChildren(go, _fakeSliderClass);
                if (fs == IntPtr.Zero) fs = Il2CppRaw.GetComponentInParent(go, _fakeSliderClass);
                if (fs == IntPtr.Zero) return null;
                IntPtr txt = Il2CppRaw.ReadObjectField(fs, _fakeSliderClass, "_valueText");
                string? v = Il2CppRaw.InvokeStringGetter(txt, _tmpGetText);
                return string.IsNullOrWhiteSpace(v) ? null : Clean(v!);
            }
            catch { return null; }
        }

        /// <summary>
        /// For a slider row with no dedicated name field (FakeSlider), read a name label from the row: the
        /// first TMP_Text in the row's subtree whose text is not purely numeric (the value). Walks up to the
        /// row container (parent of the focused slider object) then scans down.
        /// </summary>
        private static string? ReadSliderRowLabel(GameObject go)
        {
            EnsureTmpResolved();
            try
            {
                Transform? scan = go.transform != null ? (go.transform.parent ?? go.transform) : null;
                return scan != null ? ScanForNameLabel(scan, 0) : null;
            }
            catch { return null; }
        }

        private static string? ScanForNameLabel(Transform t, int depth)
        {
            if (t == null || depth > 4) return null;
            var go = t.gameObject;
            IntPtr tmp = Il2CppRaw.GetComponent(go, _tmpTextClass);
            if (tmp != IntPtr.Zero)
            {
                string? text = Il2CppRaw.InvokeStringGetter(tmp, _tmpGetText);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    string c = Clean(text!);
                    // Skip the value display (numbers / percent) — we want the name.
                    if (c.Length > 0 && !IsValueLike(c)) return c;
                }
            }
            int kids = t.childCount;
            for (int i = 0; i < kids; i++)
            {
                string? r = ScanForNameLabel(t.GetChild(i), depth + 1);
                if (r != null) return r;
            }
            return null;
        }

        private static bool IsValueLike(string s)
        {
            // "100", "80 percent", "1.5", "60 Hz" style value strings.
            int digits = 0;
            foreach (char c in s) if (char.IsDigit(c)) digits++;
            return digits > 0 && digits >= s.Length - 8; // mostly digits (+ short unit suffix)
        }

        /// <summary>Find the SoundSettingsVolumeSlider component on the focused object, its children, or an ancestor.</summary>
        private static IntPtr FindVolumeComponent(GameObject go)
        {
            if (_volSliderClass == IntPtr.Zero) return IntPtr.Zero;
            IntPtr vs = Il2CppRaw.GetComponentInChildren(go, _volSliderClass);
            if (vs == IntPtr.Zero) vs = Il2CppRaw.GetComponentInParent(go, _volSliderClass);
            return vs;
        }

        private static void EnsureVolResolved()
        {
            if (_volResolved) return;
            _volResolved = true;
            _volSliderClass = Il2CppRaw.GetClass("Assembly-CSharp.dll", "_Code.Infrastructure.Settings.Sound", "SoundSettingsVolumeSlider");
            _fakeSliderClass = Il2CppRaw.GetClass("Assembly-CSharp.dll", "_Code.Infrastructure.Settings.Sound", "FakeSlider");
            _localizeStringEventClass = Il2CppRaw.GetClass("Unity.Localization.dll", "UnityEngine.Localization.Components", "LocalizeStringEvent");
            _getLocalizedString = Il2CppRaw.GetMethod(_localizeStringEventClass, "GetLocalizedString", 0);
        }

        /// <summary>Read the localized group name (e.g. "Master") from SoundSettingsVolumeSlider._groupNameText.</summary>
        private static string? ReadVolumeGroupName(GameObject go)
        {
            EnsureVolResolved();
            try
            {
                IntPtr vs = FindVolumeComponent(go);
                if (vs == IntPtr.Zero) return null;
                IntPtr lse = Il2CppRaw.ReadObjectField(vs, _volSliderClass, "_groupNameText");
                if (lse == IntPtr.Zero) return null;

                // Prefer the resolved string via GetLocalizedString() if it bound; otherwise read the TMP_Text
                // that the LocalizeStringEvent drives (its own GameObject's text component). The latter is what
                // is actually on screen and avoids depending on the method overload resolving.
                if (_getLocalizedString != IntPtr.Zero)
                {
                    string? viaApi = Il2CppRaw.InvokeStringGetter(lse, _getLocalizedString);
                    if (!string.IsNullOrWhiteSpace(viaApi)) return Clean(viaApi!);
                }

                IntPtr lseGo = Il2CppRaw.GetComponentGameObject(lse);
                if (lseGo != IntPtr.Zero)
                {
                    IntPtr tmp = Il2CppRaw.GetComponentRaw(lseGo, _tmpTextClass);
                    string? viaTmp = Il2CppRaw.InvokeStringGetter(tmp, _tmpGetText);
                    if (!string.IsNullOrWhiteSpace(viaTmp)) return Clean(viaTmp!);
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>Read the displayed value text from SoundSettingsVolumeSlider._valueText (RTLTextMeshPro).</summary>
        private static string? ReadVolumeValueText(GameObject go)
        {
            EnsureVolResolved();
            try
            {
                IntPtr vs = FindVolumeComponent(go);
                if (vs == IntPtr.Zero) return null;
                IntPtr txt = Il2CppRaw.ReadObjectField(vs, _volSliderClass, "_valueText");
                if (txt == IntPtr.Zero) return null;
                string? v = Il2CppRaw.InvokeStringGetter(txt, _tmpGetText); // RTLTextMeshPro : TMP_Text, get_text works
                return string.IsNullOrWhiteSpace(v) ? null : Clean(v!);
            }
            catch { return null; }
        }

        /// <summary>
        /// Turn a GameObject name into a spoken setting name: split CamelCase, strip a trailing "Slider",
        /// singularize a trailing plural "s" on the whole token (Languages -> Language).
        /// </summary>
        private static string Humanize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new System.Text.StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            string s = sb.ToString().Trim();
            s = StripTrailing(s, "slider");
            if (s.Length > 1 && s.EndsWith("s", StringComparison.Ordinal) && !s.EndsWith("ss", StringComparison.Ordinal))
                s = s.Substring(0, s.Length - 1); // Languages -> Language
            return s.Trim();
        }

        public static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var sb = new System.Text.StringBuilder(raw.Length);
            bool inTag = false;
            foreach (char c in raw)
            {
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return sb.ToString().Replace('\n', ' ').Replace("  ", " ").Trim();
        }
    }
}
