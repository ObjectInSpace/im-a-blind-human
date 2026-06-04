using System;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Keyboard DIALING for the PHONE close-up. The dial pad is a MOUSE/CONTROLLER-only affordance in the base game:
    /// each key is a <c>PhoneButtonView</c> (a uGUI Selectable) that the player clicks (pointer down/up) or, on a
    /// controller, navigates with the D-pad and presses with Submit. There is NO number-key binding, so a sighted
    /// touch-typist (and a blind player who knows the number layout) cannot just type the number — exactly the
    /// keyboard-input gap the mod fills for the fridge/radio.
    ///
    /// We supply the keyboard equivalent by DRIVING THE GAME'S OWN button-press path rather than re-implementing the
    /// phone: pressing the <c>1</c> key finds the <c>PhoneButtonView</c> whose <c>_phoneKey</c> is <c>D1</c> and fires
    /// its <c>OnPointerDown</c> then <c>OnPointerUp</c> — the same two calls a mouse click makes — so the real key
    /// press runs: the tone plays, the digit is appended to the number on the screen, and the visual "pressed" sprite
    /// flashes. # maps to <c>Hash</c>, * to <c>Star</c>. The game AUTO-PLACES the call once a complete number has been
    /// entered (the phone's own <c>TryCall</c> fires when the digits match a subscriber), so dialing the digits is all
    /// that's needed — there's no separate "Call" step to synthesize. Clear/Call still have on-screen buttons the
    /// player can reach by arrowing (now that <see cref="UguiFocus"/> keeps the pad focused).
    ///
    /// Routed via the <see cref="InputContextKind.Phone"/> context so the digit keys are claimed ONLY while the phone
    /// close-up is up. Zenject-free: the <c>PhoneCloseUpView</c> is found via FindObjectIncludingInactive and its
    /// <c>_phoneButtonViews</c> array is read off it. Never throws.
    /// </summary>
    public sealed class PhoneMenu
    {
        private const string GameAsm = "Assembly-CSharp.dll";
        private const string PhoneNs = "_Code.Infrastructure.CloseUps.Views.Phone";

        // EPhoneKey enum order (decompile): D0..D9 = 0..9, Star=10, Hash=11, Call=12, Clear=13. The caller passes these
        // ints into Press(); the digit map is in KeyToPhoneKey, the symbols (#, *) come from the caller's inputString scan.

        // The pin board lives in TWO namespaces: the controller is under the _NINAH__ variant, the view under the
        // plain one (the game's own inconsistent split — verified from the decompile).
        private const string PinControllerNs = "_Code.Infrastructure._NINAH__CloseUps.Views.Phone.Pins";
        private const string PinViewNs = "_Code.Infrastructure.CloseUps.Views.Phone.Pins";

        private readonly ISpeechOutput _speech;

        private bool _resolved;
        private IntPtr _viewClass;        // PhoneCloseUpView
        private IntPtr _buttonClass;      // PhoneButtonView
        private IntPtr _getPhoneKey;      // PhoneButtonView.get_PhoneKey — falls back to the _phoneKey field if absent
        private IntPtr _onPointerDown;    // PhoneButtonView.OnPointerDown(PointerEventData)
        private IntPtr _onPointerUp;      // PhoneButtonView.OnPointerUp(PointerEventData)
        private IntPtr _pinControllerClass; // PhonePinController (holds the _pins array)
        private IntPtr _pinViewClass;       // PhonePinView (_numberText + PhoneSubscriber)
        private IntPtr _getPinSubscriber;   // PhonePinView.get_PhoneSubscriber

        public PhoneMenu(ISpeechOutput speech) => _speech = speech;

        /// <summary>True while the phone close-up is up (its view is found AND active). Used by the input context to
        /// route the digit keys here only when the dial pad is on screen.</summary>
        public bool IsActive()
        {
            try
            {
                EnsureResolved();
                IntPtr view = ResolveView();
                return view != IntPtr.Zero && Il2CppRaw.GetComponentGameObjectActive(view);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PhoneMenu] IsActive threw: {e.Message}");
                return false;
            }
        }

        /// <summary>Press the dial-pad key for an <c>EPhoneKey</c> value, dialing that digit (tone + screen + visual)
        /// exactly as a mouse click on that button would.</summary>
        public void Press(int phoneKey)
        {
            try
            {
                EnsureResolved();
                IntPtr view = ResolveView();
                if (view == IntPtr.Zero) return;

                IntPtr button = FindButton(view, phoneKey);
                if (button == IntPtr.Zero)
                {
                    MelonLogger.Msg($"[PhoneMenu] no button for key {phoneKey}.");
                    return;
                }

                // A real click is OnPointerDown then OnPointerUp; feed both a PointerEventData so the game's down/up
                // handlers (tone + press visual + digit append) run identically to a mouse press.
                IntPtr ped = Il2CppRaw.NewPointerEventData();
                bool down = Il2CppRaw.InvokeVoidWithObject(button, _onPointerDown, ped);
                bool up = Il2CppRaw.InvokeVoidWithObject(button, _onPointerUp, ped);
                MelonLogger.Msg($"[PhoneMenu] pressed key {phoneKey} (down threw={!down} up threw={!up}).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PhoneMenu] Press threw: {e.Message}");
            }
        }

        /// <summary>Map a pressed keyboard DIGIT <see cref="UnityEngine.KeyCode"/> (number row or numpad) to its
        /// <c>EPhoneKey</c> int (D0..D9 = 0..9), or -1 if it isn't a digit key. The # / * symbols are handled by the
        /// caller via <c>Input.inputString</c> (layout/shift-correct), not here.</summary>
        public static int KeyToPhoneKey(UnityEngine.KeyCode key) => key switch
        {
            UnityEngine.KeyCode.Alpha0 or UnityEngine.KeyCode.Keypad0 => 0,
            UnityEngine.KeyCode.Alpha1 or UnityEngine.KeyCode.Keypad1 => 1,
            UnityEngine.KeyCode.Alpha2 or UnityEngine.KeyCode.Keypad2 => 2,
            UnityEngine.KeyCode.Alpha3 or UnityEngine.KeyCode.Keypad3 => 3,
            UnityEngine.KeyCode.Alpha4 or UnityEngine.KeyCode.Keypad4 => 4,
            UnityEngine.KeyCode.Alpha5 or UnityEngine.KeyCode.Keypad5 => 5,
            UnityEngine.KeyCode.Alpha6 or UnityEngine.KeyCode.Keypad6 => 6,
            UnityEngine.KeyCode.Alpha7 or UnityEngine.KeyCode.Keypad7 => 7,
            UnityEngine.KeyCode.Alpha8 or UnityEngine.KeyCode.Keypad8 => 8,
            UnityEngine.KeyCode.Alpha9 or UnityEngine.KeyCode.Keypad9 => 9,
            _ => -1,
        };

        /// <summary>
        /// Read out the player's known phone numbers — the pinned contact cards on the phone close-up (name + number).
        /// This is how a sighted player "remembers" a number (it's shown on a pinboard); a blind player gets it spoken
        /// on demand. Enumerates <c>PhonePinController._pins</c>, speaking each card's spoken subscriber name and its
        /// <c>_numberText</c>. Digits are spaced out so the screen reader says them one at a time, not as one big number.
        /// </summary>
        public void ReadContacts()
        {
            try
            {
                EnsureResolved();
                IntPtr view = ResolveView();
                if (view == IntPtr.Zero) return;

                IntPtr controller = Il2CppRaw.ReadObjectField(view, _viewClass, "_phonePinController");
                IntPtr arr = controller == IntPtr.Zero ? IntPtr.Zero
                    : Il2CppRaw.ReadObjectField(controller, _pinControllerClass, "_pins");

                var lines = new System.Collections.Generic.List<string>();
                foreach (IntPtr pin in Il2CppRaw.ReadObjectArray(arr))
                {
                    if (pin == IntPtr.Zero) continue;
                    if (!Il2CppRaw.GetComponentGameObjectActive(pin)) continue; // an unassigned/hidden pin = no contact yet

                    string? number = Il2CppRaw.ReadTmpFieldText(pin, _pinViewClass, "_numberText");
                    if (string.IsNullOrWhiteSpace(number)) continue; // no number on this card → skip

                    int sub = _getPinSubscriber != IntPtr.Zero
                        ? Il2CppRaw.InvokeInt32Getter(pin, _getPinSubscriber, 0)
                        : 0;
                    lines.Add($"{SubscriberName(sub)}, {SpellNumber(number!)}.");
                }

                if (lines.Count == 0)
                {
                    _speech.Speak("No phone numbers yet.", interrupt: true);
                    MelonLogger.Msg("[PhoneMenu] ReadContacts: no active pins with numbers.");
                    return;
                }

                _speech.Speak("Phone numbers. " + string.Join(" ", lines), interrupt: true);
                MelonLogger.Msg($"[PhoneMenu] ReadContacts: spoke {lines.Count} contact(s).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PhoneMenu] ReadContacts threw: {e.Message}");
            }
        }

        /// <summary>Space out a phone number's digits so the screen reader reads them individually ("5 5 5 1 2 3 4")
        /// rather than as one large quantity. Non-digit characters (spaces, dashes) are passed through.</summary>
        private static string SpellNumber(string number)
        {
            var sb = new System.Text.StringBuilder(number.Length * 2);
            foreach (char c in number.Trim())
            {
                if (char.IsWhiteSpace(c)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Spoken name for an <c>EPhoneSubscriber</c> value (enum order from the decompile). Falls back to a
        /// generic label so a contact is never silent if the game adds a subscriber we haven't named.</summary>
        private static string SubscriberName(int subscriber) => subscriber switch
        {
            1 => "Forrest",
            2 => "FEMA",
            3 => "Neighbour",
            4 => "Psychics",
            5 => "Aura cam",
            6 => "Extrasens",
            7 => "Best son family",
            8 => "Mother's husband",
            9 => "Alkonost's husband",
            10 => "Daughter",
            11 => "Phone roulette",
            12 => "Anger's wife",
            13 => "FEMA recruiter",
            14 => "Foreign embassy",
            15 => "Daughter's friend",
            _ => "Contact",
        };

        /// <summary>The <c>PhoneButtonView</c> in the view's <c>_phoneButtonViews</c> array whose key matches, or zero.</summary>
        private IntPtr FindButton(IntPtr view, int phoneKey)
        {
            IntPtr arr = Il2CppRaw.ReadObjectField(view, _viewClass, "_phoneButtonViews");
            foreach (IntPtr btn in Il2CppRaw.ReadObjectArray(arr))
            {
                if (btn == IntPtr.Zero) continue;
                int key = ButtonKey(btn);
                if (key == phoneKey) return btn;
            }
            return IntPtr.Zero;
        }

        /// <summary>Read a button's <c>EPhoneKey</c>: via the <c>PhoneKey</c> getter if present, else the
        /// <c>_phoneKey</c> field by offset.</summary>
        private int ButtonKey(IntPtr btn) =>
            _getPhoneKey != IntPtr.Zero
                ? Il2CppRaw.InvokeInt32Getter(btn, _getPhoneKey, -1)
                : Il2CppRaw.ReadInt32Field(btn, _buttonClass, "_phoneKey", -1);

        private IntPtr ResolveView() =>
            _viewClass == IntPtr.Zero ? IntPtr.Zero : Il2CppRaw.FindObjectIncludingInactive(_viewClass);

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _viewClass = Il2CppRaw.GetClass(GameAsm, PhoneNs, "PhoneCloseUpView");
                _buttonClass = Il2CppRaw.GetClass(GameAsm, PhoneNs, "PhoneButtonView");
                if (_buttonClass != IntPtr.Zero)
                {
                    _getPhoneKey = Il2CppRaw.GetMethod(_buttonClass, "get_PhoneKey", 0); // may be absent (field-only)
                    _onPointerDown = Il2CppRaw.GetMethod(_buttonClass, "OnPointerDown", 1);
                    _onPointerUp = Il2CppRaw.GetMethod(_buttonClass, "OnPointerUp", 1);
                }

                _pinControllerClass = Il2CppRaw.GetClass(GameAsm, PinControllerNs, "PhonePinController");
                _pinViewClass = Il2CppRaw.GetClass(GameAsm, PinViewNs, "PhonePinView");
                if (_pinViewClass != IntPtr.Zero)
                    _getPinSubscriber = Il2CppRaw.GetMethod(_pinViewClass, "get_PhoneSubscriber", 0);

                MelonLogger.Msg($"[PhoneMenu] resolved: view={_viewClass != IntPtr.Zero} button={_buttonClass != IntPtr.Zero} " +
                                $"getPhoneKey={_getPhoneKey != IntPtr.Zero} onDown={_onPointerDown != IntPtr.Zero} " +
                                $"onUp={_onPointerUp != IntPtr.Zero} pinController={_pinControllerClass != IntPtr.Zero} " +
                                $"pinView={_pinViewClass != IntPtr.Zero} getPinSub={_getPinSubscriber != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PhoneMenu] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
