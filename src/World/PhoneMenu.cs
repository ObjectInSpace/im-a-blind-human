using System;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Keyboard support for the PHONE close-up. The dial pad is a MOUSE/CONTROLLER-only affordance in the base game:
    /// each key is a <c>PhoneButtonView</c> (a uGUI Selectable) clicked by pointer, or D-pad-navigated on a controller.
    /// The player navigates the pad with the ARROW KEYS like any uGUI menu — <see cref="UguiFocus"/> keeps a button
    /// focused and the game's own Selectable navigation moves between them, while <see cref="Menus.ControlDescriber"/>
    /// speaks the focused key. The buttons have NO uGUI submit handler, so Enter is translated to a press of the
    /// focused button via <see cref="PressFocused"/> (it drives the game's own OnPointerDown/Up click path). The game
    /// AUTO-PLACES the call once a complete number is entered (its own TryCall), and Call/Clear are reachable by
    /// arrowing. (Direct number-key TYPING was tried and removed: it fought the arrow focus and couldn't reach Call.)
    ///
    /// Also provides the on-demand CONTACTS readout (<see cref="ReadContacts"/>, F9): the pinned cards (name + number)
    /// are how a sighted player remembers a number.
    ///
    /// Routed via the <see cref="InputContextKind.Phone"/> context. Zenject-free: the <c>PhoneCloseUpView</c> is found
    /// via FindObjectIncludingInactive. Never throws.
    /// </summary>
    public sealed class PhoneMenu
    {
        private const string GameAsm = "Assembly-CSharp.dll";
        private const string PhoneNs = "_Code.Infrastructure.CloseUps.Views.Phone";

        // The pin board lives in TWO namespaces: the controller is under the _NINAH__ variant, the view under the
        // plain one (the game's own inconsistent split — verified from the decompile).
        private const string PinControllerNs = "_Code.Infrastructure._NINAH__CloseUps.Views.Phone.Pins";
        private const string PinViewNs = "_Code.Infrastructure.CloseUps.Views.Phone.Pins";

        private readonly ISpeechOutput _speech;

        private bool _resolved;
        private IntPtr _viewClass;        // PhoneCloseUpView
        private IntPtr _buttonClass;      // PhoneButtonView
        private IntPtr _onPointerDown;    // PhoneButtonView.OnPointerDown(PointerEventData)
        private IntPtr _onPointerUp;      // PhoneButtonView.OnPointerUp(PointerEventData)
        private IntPtr _pinControllerClass; // PhonePinController (holds the _pins array)
        private IntPtr _pinViewClass;       // PhonePinView (_numberText + PhoneSubscriber)
        private IntPtr _getPinSubscriber;   // PhonePinView.get_PhoneSubscriber
        private IntPtr _getCurrentEventSystem; // EventSystem.get_current (static)
        private IntPtr _getCurrentSelected;    // EventSystem.get_currentSelectedGameObject

        public PhoneMenu(ISpeechOutput speech) => _speech = speech;

        /// <summary>True while the phone close-up is up (its view is found AND active). Used by the input context to
        /// route Enter (press focused button) and F9 (read contacts) here only when the dial pad is on screen.</summary>
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

        /// <summary>
        /// Press the dial-pad button that currently holds EventSystem focus (the one the player arrowed onto). The
        /// phone buttons have no uGUI submit handler, so native Enter won't fire them — we drive the game's own click
        /// path (OnPointerDown then OnPointerUp) on the focused <c>PhoneButtonView</c>, which plays the tone, appends
        /// the digit (or runs Call/Clear), and flashes the pressed sprite exactly as a mouse click would. No-op if
        /// the focused control isn't a phone button.
        /// </summary>
        public void PressFocused()
        {
            try
            {
                EnsureResolved();
                if (_buttonClass == IntPtr.Zero) return;

                IntPtr es = Il2CppRaw.InvokeStaticObjectGetter(_getCurrentEventSystem);
                if (es == IntPtr.Zero) return;
                IntPtr selectedGo = Il2CppRaw.InvokeObjectGetter(es, _getCurrentSelected);
                if (selectedGo == IntPtr.Zero) return;

                // The focused object is the button's GameObject; get the PhoneButtonView component on it.
                IntPtr button = Il2CppRaw.GetComponentRaw(selectedGo, _buttonClass);
                if (button == IntPtr.Zero) return;

                IntPtr ped = Il2CppRaw.NewPointerEventData();
                bool down = Il2CppRaw.InvokeVoidWithObject(button, _onPointerDown, ped);
                bool up = Il2CppRaw.InvokeVoidWithObject(button, _onPointerUp, ped);
                MelonLogger.Msg($"[PhoneMenu] pressed focused button (down threw={!down} up threw={!up}).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PhoneMenu] PressFocused threw: {e.Message}");
            }
        }

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
                    _onPointerDown = Il2CppRaw.GetMethod(_buttonClass, "OnPointerDown", 1);
                    _onPointerUp = Il2CppRaw.GetMethod(_buttonClass, "OnPointerUp", 1);
                }

                _pinControllerClass = Il2CppRaw.GetClass(GameAsm, PinControllerNs, "PhonePinController");
                _pinViewClass = Il2CppRaw.GetClass(GameAsm, PinViewNs, "PhonePinView");
                if (_pinViewClass != IntPtr.Zero)
                    _getPinSubscriber = Il2CppRaw.GetMethod(_pinViewClass, "get_PhoneSubscriber", 0);

                // EventSystem (for PressFocused): the focused phone button is read off EventSystem.current.
                IntPtr esClass = Il2CppRaw.GetClass("UnityEngine.UI.dll", "UnityEngine.EventSystems", "EventSystem");
                if (esClass == IntPtr.Zero)
                    esClass = Il2CppRaw.GetClass("UnityEngine.UIModule.dll", "UnityEngine.EventSystems", "EventSystem");
                if (esClass != IntPtr.Zero)
                {
                    _getCurrentEventSystem = Il2CppRaw.GetMethod(esClass, "get_current", 0);
                    _getCurrentSelected = Il2CppRaw.GetMethod(esClass, "get_currentSelectedGameObject", 0);
                }

                MelonLogger.Msg($"[PhoneMenu] resolved: view={_viewClass != IntPtr.Zero} button={_buttonClass != IntPtr.Zero} " +
                                $"onDown={_onPointerDown != IntPtr.Zero} onUp={_onPointerUp != IntPtr.Zero} " +
                                $"pinController={_pinControllerClass != IntPtr.Zero} pinView={_pinViewClass != IntPtr.Zero} " +
                                $"getPinSub={_getPinSubscriber != IntPtr.Zero} eventSystem={_getCurrentEventSystem != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PhoneMenu] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
