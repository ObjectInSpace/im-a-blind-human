using System;
using System.Collections.Generic;
using System.Text;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Menus;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Speaks the game's on-screen context control-prompt row — the persistent strip of key-hints the game shows
    /// for the CURRENT context (in a room, in the fridge, at the radio, in a run zone, in dialog). This is distinct
    /// from <see cref="HudNarrator"/>, which speaks the single interaction prompt for the one object you're looking
    /// at. The control row answers "what can I do here, and with which key", regardless of where you're aimed.
    ///
    /// SOURCE: the game owns one <c>ControlsListView</c> (ns <c>_Code.Infrastructure.ControlsViewer</c>) holding a
    /// <c>ControlView[] _controls</c>. Each <c>ControlView</c> carries the action label (<c>_descriptionText</c>,
    /// an RTLTextMeshPro) and the key glyph/text (<c>_keyText</c>, a TMP_Text) plus an <c>_isAvailable</c> flag.
    /// The game resolves the glyph itself, device-aware (keyboard vs Xbox/PlayStation/Switch), so we never hardcode
    /// any binding — we read the live display text it already computed. We speak only the AVAILABLE controls.
    ///
    /// WHEN IT SPEAKS:
    /// - First encounter of a new context (driven by a <c>HUDPresenter.SetupAndShowControlsView(EControlsList)</c>
    ///   hook in <see cref="WorldPatches"/>): the row is announced ONCE per distinct context per session, so a new
    ///   player learns the controls without being nagged on every re-entry.
    /// - On demand, any time, via the repeat key (<see cref="Repeat"/>) — re-reads the live row for the context
    ///   the player is in right now.
    ///
    /// The setup method is async (the ControlView children populate over a frame or two), so the first-encounter
    /// path doesn't read immediately: it arms a short deferred read that <see cref="Tick"/> drains once the
    /// descriptions are non-empty (or a small timeout lapses). The on-demand path reads immediately.
    /// </summary>
    public sealed class ControlsNarrator
    {
        private const string GameAsm = "Assembly-CSharp.dll";
        private const string ControlsNs = "_Code.Infrastructure.ControlsViewer";

        private readonly ISpeechOutput _speech;

        // Distinct EControlsList values already announced this session (first-encounter gate). The int is the
        // underlying enum value: None=0, InRoom=1, InFridge=2, InPhone=3, InRadio=4, InUsualCloseUp=5, RunZone=6,
        // InDialog=7.
        private readonly HashSet<int> _seenContexts = new();

        // Deferred-read state for the first-encounter path: armed by OnContextChanged, drained by Tick. We retry a
        // few frames because SetupAndShowControlsView populates the ControlView children asynchronously.
        private bool _pendingRead;
        private int _pendingContext = -1;
        private int _pendingFramesLeft;
        private const int PendingFramesMax = 30; // ~0.5s at 60fps — generous for the async populate.

        // Cached IL2CPP handles (resolved lazily, once).
        private IntPtr _controlsListViewClass;
        private IntPtr _controlViewClass;
        private IntPtr _tmpTextClass;
        private IntPtr _tmpGetText;
        private bool _resolved;

        public ControlsNarrator(ISpeechOutput speech) => _speech = speech;

        // ---------------- public entry points ----------------

        /// <summary>
        /// A control context just became active (from the <c>SetupAndShowControlsView</c> hook). If this context
        /// hasn't been announced yet this session, arm a deferred read so the freshly-populated row is spoken once.
        /// Re-entering an already-seen context is silent (the player can still hit the repeat key).
        /// </summary>
        public void OnContextChanged(int controlsList)
        {
            try
            {
                // None (0) clears the row — nothing to announce, and don't mark it seen.
                if (controlsList <= 0) return;
                if (_seenContexts.Contains(controlsList)) return;

                _pendingRead = true;
                _pendingContext = controlsList;
                _pendingFramesLeft = PendingFramesMax;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ControlsNarrator] OnContextChanged threw: {e.Message}");
            }
        }

        /// <summary>
        /// Drain the deferred first-encounter read. Called every frame from <c>AccessMod.OnUpdate</c>. Reads the live
        /// row as soon as it has spoken content; if it never populates within the window, gives up quietly. No-op when
        /// nothing is pending, so it's cheap to call unconditionally.
        /// </summary>
        public void Tick()
        {
            if (!_pendingRead) return;
            try
            {
                string row = ReadCurrentRow();
                if (row.Length > 0)
                {
                    _seenContexts.Add(_pendingContext);
                    _pendingRead = false;
                    _speech.Speak(row, interrupt: false); // don't stomp a dialogue/interaction line that may be mid-read
                    return;
                }
                if (--_pendingFramesLeft <= 0)
                {
                    // Populate never produced text. Mark seen anyway so we don't keep re-arming on every re-entry,
                    // and stop. (An empty row is a legitimate context with no available controls.)
                    _seenContexts.Add(_pendingContext);
                    _pendingRead = false;
                }
            }
            catch (Exception e)
            {
                _pendingRead = false;
                MelonLogger.Warning($"[ControlsNarrator] Tick threw: {e.Message}");
            }
        }

        /// <summary>
        /// On-demand readout (repeat key). Re-reads and speaks the live control row for the current context,
        /// interrupting so the player gets an immediate answer. Speaks a short "no controls" note if the row is empty
        /// rather than staying silent, so the key always gives feedback.
        /// </summary>
        public void Repeat()
        {
            try
            {
                string row = ReadCurrentRow();
                _speech.Speak(row.Length > 0 ? row : "No controls available.", interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ControlsNarrator] Repeat threw: {e.Message}");
            }
        }

        // ---------------- read the live row ----------------

        /// <summary>
        /// Read the live <c>ControlsListView._controls</c> and compose a spoken phrase of the AVAILABLE controls,
        /// each as "key, action" (e.g. "Shift, run. E, interact."). Empty string if there is no view, no controls,
        /// or none available. Never throws.
        /// </summary>
        private string ReadCurrentRow()
        {
            if (!EnsureResolved()) return string.Empty;

            IntPtr view = Il2CppRaw.FindObjectIncludingInactive(_controlsListViewClass);
            if (view == IntPtr.Zero) return string.Empty;

            IntPtr arrayPtr = Il2CppRaw.ReadObjectField(view, _controlsListViewClass, "_controls");
            IntPtr[] controls = Il2CppRaw.ReadObjectArray(arrayPtr);
            if (controls.Length == 0) return string.Empty;

            var sb = new StringBuilder();
            var spoken = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // de-dupe identical entries
            foreach (IntPtr cv in controls)
            {
                if (cv == IntPtr.Zero) continue;
                if (!IsAvailable(cv)) continue;

                string action = ReadControlText(cv, "_descriptionText");
                string key = ReadControlText(cv, "_keyText");
                if (action.Length == 0 && key.Length == 0) continue;

                // "key, action" reads like a screen-reader control list; tolerate either side missing (some glyph
                // rows carry only a sprite, leaving _keyText empty — then we speak the action alone).
                string entry;
                if (key.Length > 0 && action.Length > 0) entry = $"{key}, {action}";
                else if (action.Length > 0) entry = action;
                else entry = key;

                if (!spoken.Add(entry)) continue;
                if (sb.Length > 0) sb.Append(". ");
                sb.Append(entry);
            }
            return sb.ToString();
        }

        /// <summary>Read a <c>ControlView</c>'s <c>_isAvailable</c> flag (controls present-but-disabled are skipped).</summary>
        private bool IsAvailable(IntPtr controlView)
        {
            // ReadInt32Field reads the bool's single byte as an int; non-zero = true. Default to true if the field is
            // missing so a layout change doesn't silently drop every control.
            int v = Il2CppRaw.ReadInt32Field(controlView, _controlViewClass, "_isAvailable", fallback: 1);
            return v != 0;
        }

        /// <summary>Read one of a ControlView's TMP text fields (_descriptionText / _keyText), cleaned of markup.</summary>
        private string ReadControlText(IntPtr controlView, string fieldName)
        {
            try
            {
                string? raw = Il2CppRaw.ReadTmpFieldText(controlView, _controlViewClass, fieldName);
                return string.IsNullOrWhiteSpace(raw) ? string.Empty : ControlDescriber.Clean(raw!).Trim();
            }
            catch { return string.Empty; }
        }

        // ---------------- handle resolution ----------------

        private bool EnsureResolved()
        {
            if (_resolved) return _controlsListViewClass != IntPtr.Zero && _controlViewClass != IntPtr.Zero;
            _resolved = true;
            _controlsListViewClass = Il2CppRaw.GetClass(GameAsm, ControlsNs, "ControlsListView");
            _controlViewClass = Il2CppRaw.GetClass(GameAsm, ControlsNs, "ControlView");
            _tmpTextClass = Il2CppRaw.GetClass("Unity.TextMeshPro.dll", "TMPro", "TMP_Text");
            _tmpGetText = Il2CppRaw.GetMethod(_tmpTextClass, "get_text", 0);
            MelonLogger.Msg(
                $"[ControlsNarrator] resolved: listView={_controlsListViewClass != IntPtr.Zero} " +
                $"controlView={_controlViewClass != IntPtr.Zero} tmp={_tmpTextClass != IntPtr.Zero}");
            return _controlsListViewClass != IntPtr.Zero && _controlViewClass != IntPtr.Zero;
        }
    }
}
