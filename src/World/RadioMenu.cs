using System;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Speech;
using UnityEngine;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Keyboard control + narration for the RADIO close-up. The radio is the only ANALOG mouse-only affordance in the
    /// base game, and the rip (PlayerInputActions.asset, 2026-06-03) CONFIRMS its actions are GAMEPAD-ONLY with no
    /// keyboard binding: RadioKnob → right stick, RadioLeftHandle/RadioRightHandle → L/R shoulder. So this stepper is
    /// NOT duplication — it is the only keyboard path to tune, and the game's Navigate (arrows/WASD) does not touch it.
    ///
    /// HOW THE RADIO WORKS (from RadioModel): you scroll the frequency and home in on a hidden signal BY EAR/EYE. The
    /// station's message is noise-garbled when you're far (NormalisedDistance→1) and resolves to readable text as you
    /// approach (→0). For a sighted player the de-garbling text IS the homing feedback; we translate that gradient
    /// into audio (closeness buckets) and then read the CLEAR message once it resolves.
    ///
    /// We DRIVE THE GAME'S OWN methods rather than re-implement tuning. Mod key wiring lives in AccessMod.OnUpdate:
    /// - <b>Tune</b>: while PageUp/PageDown is HELD, call the knob's private <c>RotateKnob(float delta)</c> each frame —
    ///   the exact method the drag path calls — so the real value-update + station detection run. (<see cref="Tune"/>.)
    /// - <b>Band</b>: Home/End toggles AM/FM via the model's public <c>SwitchState(ERadioState)</c>. The game models
    ///   AM/FM as two separate handle buttons; collapsing them to one toggle loses nothing. (<see cref="SwitchBand"/>.)
    ///
    /// Narration (proactive, from <see cref="Tick"/> — there is no per-item hover sink like the fridge):
    /// - <b>Closeness</b>: coarse "warmer/colder" buckets from <c>NormalisedDistance</c> while tuning, so the player
    ///   homes in by ear — the audio analog of the visual garble gradient. De-duped so it doesn't chatter when still.
    /// - <b>On-signal message</b>: once <c>NormalisedDistance</c> is low enough that the text is readable, speak the
    ///   resolved <c>GetDisplayedMessage()</c> — the payload the player tuned in FOR. Never reads garbled intermediate
    ///   text; re-armed when the player drifts off-signal so re-finding the station speaks it again.
    /// - <b>Band</b>: announces AM/FM changes immediately, from <c>CurrentState</c>.
    ///
    /// Zenject-free: the <c>RadioCloseUpView</c> is found via FindObjectIncludingInactive, and its <c>_radioKnobController</c>
    /// / <c>_radioModel</c> fields are read off it. Never throws.
    /// </summary>
    public sealed class RadioMenu
    {
        private const string GameAsm = "Assembly-CSharp.dll";
        private const string RadioNs = "_Code.Infrastructure.CloseUps.Views.Radio";

        // Per-frame knob delta while an arrow is held. Small so a held sweep is smooth, not a jump. Tuned by ear.
        private const float TuneStepPerFrame = 0.35f;
        // ERadioState: AM=0, FM=1 (enum order in the decompile).
        private const int StateAM = 0, StateFM = 1;

        private readonly ISpeechOutput _speech;

        private bool _resolved;
        private IntPtr _viewClass;        // RadioCloseUpView
        private IntPtr _knobClass;        // RadioKnobController
        private IntPtr _modelClass;       // RadioModel
        private IntPtr _rotateKnob;       // RadioKnobController.RotateKnob(float)
        private IntPtr _getNormDistance;  // RadioModel.get_NormalisedDistance
        private IntPtr _getCurrentState;  // RadioModel.get_CurrentState (ERadioState)
        private IntPtr _switchState;      // RadioModel.SwitchState(ERadioState) — model-only fallback
        private IntPtr _onRadioButtonPressed; // RadioCloseUpView.OnRadioButtonPressed(ERadioState) — the FULL band switch
        private IntPtr _getDisplayedMessage; // RadioModel.GetDisplayedMessage() : string

        // RadioModel._isWaveFound (private bool): TRUE only when the station is locked and the text is FULLY resolved.
        // This is the correct gate for speaking the message — the old distance threshold (0.06) still had partial
        // garble, and GetDisplayedMessage() changes char-by-char as it resolves, so each frame produced a DIFFERENT
        // near-garbled string that passed the dedupe and queued (interrupt:false) → dozens of strings flooded the speech
        // buffer and overloaded the mod (the user's "garbage spam" report). We now speak ONLY when _isWaveFound is true.
        private const string IsWaveFoundField = "_isWaveFound";
        // RadioModel._message (private string): the current resolved LINE of station text — only one line, which is why
        // reading it alone missed the rest of a multi-line broadcast. Used as the fallback when the line array is absent.
        private const string MessageField = "_message";
        // RadioModel._currentText (private string[]): ALL lines of the current station's message. Joined for the full read.
        private const string CurrentTextField = "_currentText";

        private int _lastBand = -1;
        private string _lastCloseness = string.Empty;
        private float _nextClosenessSpeakTime;
        private string _lastSpokenMessage = string.Empty; // de-dupe the on-signal message readout
        private bool _announcedEntry;                       // spoke the one-time "how to leave" hint this session

        public RadioMenu(ISpeechOutput speech) => _speech = speech;

        /// <summary>Sweep the tuning knob one frame's worth in the given direction (call every frame the key is held).</summary>
        public void Tune(bool backwards)
        {
            try
            {
                EnsureResolved();
                IntPtr knob = ResolveKnob();
                if (knob == IntPtr.Zero) return;
                float delta = backwards ? -TuneStepPerFrame : TuneStepPerFrame;
                Il2CppRaw.InvokeVoidWithFloat(knob, _rotateKnob, delta);
                // Closeness is spoken from Tick (throttled), not here, so a held sweep doesn't spam every frame.
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[RadioMenu] Tune threw: {e.Message}");
            }
        }

        /// <summary>Toggle the band AM↔FM (Up/Down). Speaks the new band.</summary>
        public void SwitchBand()
        {
            try
            {
                EnsureResolved();
                IntPtr model = ResolveModel();
                if (model == IntPtr.Zero) { _speech.Speak("Radio.", interrupt: true); return; }
                int cur = Il2CppRaw.InvokeInt32Getter(model, _getCurrentState, StateAM);
                int next = cur == StateFM ? StateAM : StateFM;

                // Drive the VIEW's band-button handler (the path the game's own AM/FM buttons use) so the full switch
                // runs — knob remap, station set, display — not just the model flag. SwitchState alone set the field
                // without the view-side effects, which is why the band change "did nothing". Fall back to the model
                // method if the view handler didn't resolve.
                IntPtr view = ResolveView();
                bool switched = view != IntPtr.Zero && _onRadioButtonPressed != IntPtr.Zero
                    && Il2CppRaw.InvokeVoidWithEnum(view, _onRadioButtonPressed, next);
                if (!switched)
                    Il2CppRaw.InvokeVoidWithEnum(model, _switchState, next);

                _lastBand = next;
                _speech.Speak(BandName(next), interrupt: true);
                MelonLogger.Msg($"[RadioMenu] band {BandName(cur)} -> {BandName(next)} (viewHandler={switched}).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[RadioMenu] SwitchBand threw: {e.Message}");
            }
        }

        /// <summary>Per-frame while the radio is the active context: announce band changes and give throttled
        /// "warmer/colder" closeness feedback so the player can home in on a station by ear.</summary>
        public void Tick(bool tuningHeld)
        {
            try
            {
                EnsureResolved();
                IntPtr model = ResolveModel();
                if (model == IntPtr.Zero) return;

                // One-time-per-open hint on HOW TO LEAVE. The radio close-up is HOLD-to-close (unlike the fridge/phone,
                // which close on a tap), so a quick Q press doesn't exit and a blind player can't see the hold-progress
                // fill — they think they're stuck. Read the view's own IsHoldToClose so the phrasing matches the game,
                // and tell them to HOLD Q. (Backspace remains a tap fallback.) Announced once per radio session.
                if (!_announcedEntry)
                {
                    _announcedEntry = true;
                    IntPtr view = ResolveView();
                    bool holdToClose = view != IntPtr.Zero && Il2CppRaw.ReadBoolField(view, _viewClass, "IsHoldToClose", true);
                    _speech.Speak(holdToClose ? "Radio. Hold Q to leave." : "Radio. Press Q to leave.", interrupt: false);
                }

                // Band changed out from under us (or first read) — announce it once.
                int band = Il2CppRaw.InvokeInt32Getter(model, _getCurrentState, _lastBand);
                if (band != _lastBand && _lastBand >= 0)
                    _speech.Speak(BandName(band), interrupt: true);
                _lastBand = band;

                float dist = Il2CppRaw.InvokeFloatGetter(model, _getNormDistance, 1f);

                // Station-text readout. We never read garble (the game's own audio cue already tells the player they're
                // homing in — no mod status message needed). When the wave is fully FOUND, read the WHOLE resolved
                // message: the station's text is a set of lines (_currentText[]), and reading only the first line missed
                // the rest (the "emergency services… / they're burning people" report). We join all non-empty lines.
                // De-duped on _lastSpokenMessage, which we do NOT reset on wave-loss: drifting off a station you've heard
                // must not re-read it, and drifting back on must not repeat it. A different station has different text, so
                // it still speaks; only the SAME message is suppressed once spoken.
                bool waveFound = Il2CppRaw.ReadBoolField(model, _modelClass, IsWaveFoundField);
                if (waveFound)
                {
                    string msg = ReadFullMessage(model);
                    if (msg.Length > 0 && msg != _lastSpokenMessage)
                    {
                        _lastSpokenMessage = msg;
                        _speech.Speak(msg, interrupt: false); // don't cut off — let the whole station message play out
                    }
                }

                // Closeness feedback only while actively tuning, throttled, and only when the bucket changes.
                if (!tuningHeld) return;
                if (Time.unscaledTime < _nextClosenessSpeakTime) return;

                string bucket = Closeness(dist);
                if (bucket.Length == 0 || bucket == _lastCloseness) return;
                _lastCloseness = bucket;
                _nextClosenessSpeakTime = Time.unscaledTime + 0.4f;
                _speech.Speak(bucket, interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[RadioMenu] Tick threw: {e.Message}");
            }
        }

        /// <summary>
        /// The complete resolved station message: join all non-empty lines of the model's <c>_currentText</c> string
        /// array. Falls back to the single <c>_message</c> field if the array is empty/absent. This is what fixes the
        /// "only the first line was read" bug — the station's text is multiple lines and only the current one lived in
        /// <c>_message</c>.
        /// </summary>
        private string ReadFullMessage(IntPtr model)
        {
            try
            {
                IntPtr arr = Il2CppRaw.ReadObjectField(model, _modelClass, CurrentTextField);
                if (arr != IntPtr.Zero)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (IntPtr s in Il2CppRaw.ReadObjectArray(arr))
                    {
                        if (s == IntPtr.Zero) continue;
                        string line = (Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(s) ?? string.Empty).Trim();
                        if (line.Length == 0) continue;
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(line);
                    }
                    if (sb.Length > 0) return sb.ToString();
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[RadioMenu] ReadFullMessage threw: {e.Message}");
            }
            // Fallback: the single current-line field.
            return (Il2CppRaw.ReadStringField(model, _modelClass, MessageField) ?? string.Empty).Trim();
        }

        /// <summary>Reset state when the radio closes, so reopening starts fresh.</summary>
        public void Reset()
        {
            _lastBand = -1;
            _lastCloseness = string.Empty;
            _nextClosenessSpeakTime = 0f;
            _lastSpokenMessage = string.Empty;
            _announcedEntry = false;
        }

        /// <summary>0 = exactly on a station, 1 = far. Coarse buckets so the player hears "getting closer / on the
        /// station" rather than a noisy number. Empty string = don't speak.</summary>
        private static string Closeness(float normalisedDistance)
        {
            if (normalisedDistance <= 0.06f) return "On the station.";
            if (normalisedDistance < 0.20f) return "Very close.";
            if (normalisedDistance < 0.45f) return "Getting closer.";
            return "Static.";
        }

        private static string BandName(int state) => state == StateFM ? "F M" : "A M";

        private IntPtr ResolveView() => _viewClass == IntPtr.Zero ? IntPtr.Zero : Il2CppRaw.FindObjectIncludingInactive(_viewClass);

        private IntPtr ResolveKnob()
        {
            IntPtr view = ResolveView();
            return view == IntPtr.Zero ? IntPtr.Zero : Il2CppRaw.ReadObjectField(view, _viewClass, "_radioKnobController");
        }

        private IntPtr ResolveModel()
        {
            IntPtr view = ResolveView();
            return view == IntPtr.Zero ? IntPtr.Zero : Il2CppRaw.ReadObjectField(view, _viewClass, "_radioModel");
        }

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _viewClass = Il2CppRaw.GetClass(GameAsm, RadioNs, "RadioCloseUpView");
                _knobClass = Il2CppRaw.GetClass(GameAsm, RadioNs, "RadioKnobController");
                _modelClass = Il2CppRaw.GetClass(GameAsm, RadioNs, "RadioModel");

                if (_viewClass != IntPtr.Zero)
                    _onRadioButtonPressed = Il2CppRaw.GetMethod(_viewClass, "OnRadioButtonPressed", 1);
                if (_knobClass != IntPtr.Zero)
                    _rotateKnob = Il2CppRaw.GetMethod(_knobClass, "RotateKnob", 1);
                if (_modelClass != IntPtr.Zero)
                {
                    _getNormDistance = Il2CppRaw.GetMethod(_modelClass, "get_NormalisedDistance", 0);
                    _getCurrentState = Il2CppRaw.GetMethod(_modelClass, "get_CurrentState", 0);
                    _switchState = Il2CppRaw.GetMethod(_modelClass, "SwitchState", 1);
                    _getDisplayedMessage = Il2CppRaw.GetMethod(_modelClass, "GetDisplayedMessage", 0);
                }

                MelonLogger.Msg($"[RadioMenu] resolved: view={_viewClass != IntPtr.Zero} knob={_knobClass != IntPtr.Zero} " +
                                $"model={_modelClass != IntPtr.Zero} rotateKnob={_rotateKnob != IntPtr.Zero} " +
                                $"normDist={_getNormDistance != IntPtr.Zero} curState={_getCurrentState != IntPtr.Zero} " +
                                $"switchState={_switchState != IntPtr.Zero} onRadioBtn={_onRadioButtonPressed != IntPtr.Zero} " +
                                $"displayedMsg={_getDisplayedMessage != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[RadioMenu] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
