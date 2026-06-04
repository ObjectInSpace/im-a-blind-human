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

        // RadioModel._isWaveFound (private bool): TRUE while the station is locked. The TEXT still garbles in line-by-line
        // AFTER this flips, so it gates WHEN to start watching the display, not when the text is clean.
        private const string IsWaveFoundField = "_isWaveFound";

        // A displayed line is treated as fully resolved once GetDisplayedMessage() has been UNCHANGED for this long
        // (the de-garble reveal stops changing the string when the line settles). Long enough to clear mid-reveal
        // flicker, short enough to feel responsive.
        private const float ResolveStableSeconds = 0.5f;

        private int _lastBand = -1;
        private string _lastCloseness = string.Empty;
        private float _nextClosenessSpeakTime;
        private string _lastSpokenMessage = string.Empty; // de-dupe the spoken line
        private string _lastDisplayed = string.Empty;      // last GetDisplayedMessage() seen, for the stability debounce
        private float _displayedStableAt;                  // unscaled time at which the current display becomes "stable"
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

                // Station-text readout. A station's message plays out as a CONVERSATION — lines appear one after another
                // over time, and each line GARBLES IN char-by-char before settling. So we must (a) never read garble,
                // and (b) read each line as it finishes, not the whole thing at once.
                //
                // We poll the on-screen text (GetDisplayedMessage) only while the wave is FOUND. The reveal constantly
                // changes the string as it de-garbles; a line is DONE when that string STOPS changing. So we debounce:
                // remember the last displayed string and when it last changed, and only speak once it has been stable
                // for ResolveStableSeconds. De-duped on _lastSpokenMessage so a held, unchanged line isn't repeated; we
                // do NOT reset the dedupe on wave-loss, so drifting off and back doesn't re-read the same line.
                bool waveFound = Il2CppRaw.ReadBoolField(model, _modelClass, IsWaveFoundField);
                if (waveFound)
                {
                    string shown = (Il2CppRaw.InvokeStringGetter(model, _getDisplayedMessage) ?? string.Empty).Trim();
                    if (shown != _lastDisplayed)
                    {
                        // Still resolving (or a new line started): note it and (re)start the stability timer.
                        _lastDisplayed = shown;
                        _displayedStableAt = Time.unscaledTime + ResolveStableSeconds;
                    }
                    else if (shown.Length > 0 && shown != _lastSpokenMessage && Time.unscaledTime >= _displayedStableAt)
                    {
                        // Stable long enough → this line has finished resolving. Speak it once.
                        _lastSpokenMessage = shown;
                        _speech.Speak(shown, interrupt: false);
                        MelonLogger.Msg($"[RadioMenu] spoke resolved line: \"{shown}\"");
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

        /// <summary>Reset state when the radio closes, so reopening starts fresh.</summary>
        public void Reset()
        {
            _lastBand = -1;
            _lastCloseness = string.Empty;
            _nextClosenessSpeakTime = 0f;
            _lastSpokenMessage = string.Empty;
            _lastDisplayed = string.Empty;
            _displayedStableAt = 0f;
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
