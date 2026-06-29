using System;
using System.Text;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Menus;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Speaks two text surfaces that otherwise go silent for a blind player:
    ///
    /// 1. <b>Popup windows</b> — <c>_Code.Utils.UI.Popup.PopupWindow.Show(string title, PopupButtonData[] buttons)</c>.
    ///    A generic modal: a title plus some buttons. The notable case the user hit is the "ending unlocked" popup whose
    ///    TITLE (the name of the ending you just received) never read. The title arrives as an ALREADY-RESOLVED string
    ///    arg, and each <c>PopupButtonData.Text</c> is a plain string too, so we read both off the hook and speak
    ///    "title. Options: a, b." — covering every popup, not just the ending one.
    ///
    /// 2. <b>Credits</b> — <c>TitlesLoadText</c> (a MonoBehaviour) loads its credits text from a path into a
    ///    <c>TMP_Text _text</c> on <c>Start()</c>. We read that field after Start runs and speak it. ("Titles" = the
    ///    Russian dev term for end credits.)
    ///
    /// Both are driven by Harmony hooks in <see cref="WorldPatches"/>. Read-only; never throws into the game.
    /// </summary>
    public sealed class PopupNarrator
    {
        private const string GameAsm = "Assembly-CSharp.dll";

        private readonly ISpeechOutput _speech;

        private bool _resolved;
        private IntPtr _popupButtonDataClass; // _Code.Utils.UI.Popup.PopupButtonData (+ get_Text)
        private IntPtr _getButtonText;        // PopupButtonData.get_Text
        private IntPtr _titlesClass;          // TitlesLoadText (declares _text)
        private IntPtr _tmpTextClass;         // TMPro.TMP_Text (+ get_text)

        public PopupNarrator(ISpeechOutput speech) => _speech = speech;

        /// <summary>A popup opened: speak its title and, if any, its button labels. <paramref name="title"/> is the
        /// resolved title string from <c>PopupWindow.Show</c>; <paramref name="buttonsArrayPtr"/> is the
        /// <c>PopupButtonData[]</c> arg (zero/empty is fine — title-only popups exist).</summary>
        public void OnPopupShown(string? title, IntPtr buttonsArrayPtr)
        {
            try
            {
                EnsureResolved();
                var sb = new StringBuilder();
                string t = Clean(title);
                if (t.Length > 0) sb.Append(t).Append('.');

                string[] labels = ReadButtonLabels(buttonsArrayPtr);
                if (labels.Length > 0)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append("Options: ").Append(string.Join(", ", labels)).Append('.');
                }

                if (sb.Length == 0) return;
                _speech.Speak(sb.ToString(), interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PopupNarrator] OnPopupShown threw: {e.Message}");
            }
        }

        /// <summary>The credits view started: read its loaded TMP text and speak it. <paramref name="titlesViewPtr"/> is
        /// the <c>TitlesLoadText</c> instance whose <c>Start()</c> just populated <c>_text</c>.</summary>
        public void OnCreditsShown(IntPtr titlesViewPtr)
        {
            try
            {
                if (titlesViewPtr == IntPtr.Zero) return;
                EnsureResolved();
                if (_titlesClass == IntPtr.Zero) return;

                IntPtr tmp = Il2CppRaw.ReadObjectField(titlesViewPtr, _titlesClass, "_text");
                string text = Clean(Il2CppRaw.ReadTmpComponentText(tmp));
                if (text.Length == 0) return;
                _speech.Speak(text, interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PopupNarrator] OnCreditsShown threw: {e.Message}");
            }
        }

        private string[] ReadButtonLabels(IntPtr buttonsArrayPtr)
        {
            if (buttonsArrayPtr == IntPtr.Zero || _getButtonText == IntPtr.Zero) return Array.Empty<string>();
            IntPtr[] buttons = Il2CppRaw.ReadObjectArray(buttonsArrayPtr);
            if (buttons.Length == 0) return Array.Empty<string>();
            var labels = new System.Collections.Generic.List<string>(buttons.Length);
            foreach (IntPtr b in buttons)
            {
                if (b == IntPtr.Zero) continue;
                string label = Clean(Il2CppRaw.InvokeStringGetter(b, _getButtonText));
                if (label.Length > 0) labels.Add(label);
            }
            return labels.ToArray();
        }

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _popupButtonDataClass = Il2CppRaw.GetClass(GameAsm, "_Code.Utils.UI.Popup", "PopupButtonData");
                if (_popupButtonDataClass != IntPtr.Zero)
                    _getButtonText = Il2CppRaw.GetMethod(_popupButtonDataClass, "get_Text", 0);

                _titlesClass = Il2CppRaw.GetClass(GameAsm, "_Code.Menues.Titles", "TitlesLoadText");

                _tmpTextClass = Il2CppRaw.GetClass("Unity.TextMeshPro.dll", "TMPro", "TMP_Text");

                MelonLogger.Msg($"[PopupNarrator] resolved: buttonData={_popupButtonDataClass != IntPtr.Zero} " +
                                $"getText={_getButtonText != IntPtr.Zero} titles={_titlesClass != IntPtr.Zero} " +
                                $"tmp={_tmpTextClass != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PopupNarrator] EnsureResolved threw: {e.Message}");
            }
        }

        private static string Clean(string? s) =>
            string.IsNullOrWhiteSpace(s) ? string.Empty : ControlDescriber.Clean(s!).Trim();
    }
}
