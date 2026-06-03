using System;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Speech;
using UnityEngine;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Keyboard control + narration for the RADIO close-up. The radio is the only ANALOG mouse-only affordance in the
    /// base game: tuning is a knob dragged with the pointer (<c>RadioKnobController</c>, IPointerDown/Drag) and the
    /// AM/FM band is picked with pointer-only buttons (<c>RadioButtonView</c>). The game's Navigate (arrows/WASD)
    /// action does not touch either, so a blind player can't tune. (There IS a <c>UIRadioKnob</c> input action, but
    /// whether it has a keyboard binding is unconfirmed — if it turns out tuning already works by keyboard, this
    /// stepper is harmless duplication and the value of this class is the narration; see the radio-input memo.)
    ///
    /// We DRIVE THE GAME'S OWN methods rather than re-implement tuning:
    /// - <b>Tune</b>: while Left/Right is HELD, call the knob's private <c>RotateKnob(float delta)</c> each frame — the
    ///   exact method the drag path calls — so the real value-update + station detection run. (<see cref="Tune"/>.)
    /// - <b>Band</b>: Up/Down toggles AM/FM via the model's public <c>SwitchState(ERadioState)</c>. (<see cref="SwitchBand"/>.)
    ///
    /// Narration: there is no per-item hover sink like the fridge, so this speaks proactively from <see cref="Tick"/>,
    /// reading <c>RadioModel.NormalisedDistance</c> (0 = on a station, 1 = far) + <c>CurrentState</c> (AM/FM). It
    /// announces band changes immediately and gives periodic "warmer/colder" closeness feedback while tuning so the
    /// player can home in by ear. De-dupes so it doesn't chatter when the knob isn't moving.
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
        private IntPtr _switchState;      // RadioModel.SwitchState(ERadioState)

        private int _lastBand = -1;
        private string _lastCloseness = string.Empty;
        private float _nextClosenessSpeakTime;

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
                Il2CppRaw.InvokeVoidWithEnum(model, _switchState, next);
                _lastBand = next;
                _speech.Speak(BandName(next), interrupt: true);
                MelonLogger.Msg($"[RadioMenu] band {BandName(cur)} -> {BandName(next)}.");
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

                // Band changed out from under us (or first read) — announce it once.
                int band = Il2CppRaw.InvokeInt32Getter(model, _getCurrentState, _lastBand);
                if (band != _lastBand && _lastBand >= 0)
                    _speech.Speak(BandName(band), interrupt: true);
                _lastBand = band;

                // Closeness feedback only while actively tuning, throttled, and only when the bucket changes.
                if (!tuningHeld) return;
                if (Time.unscaledTime < _nextClosenessSpeakTime) return;

                float dist = Il2CppRaw.InvokeFloatGetter(model, _getNormDistance, 1f);
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

                if (_knobClass != IntPtr.Zero)
                    _rotateKnob = Il2CppRaw.GetMethod(_knobClass, "RotateKnob", 1);
                if (_modelClass != IntPtr.Zero)
                {
                    _getNormDistance = Il2CppRaw.GetMethod(_modelClass, "get_NormalisedDistance", 0);
                    _getCurrentState = Il2CppRaw.GetMethod(_modelClass, "get_CurrentState", 0);
                    _switchState = Il2CppRaw.GetMethod(_modelClass, "SwitchState", 1);
                }

                MelonLogger.Msg($"[RadioMenu] resolved: view={_viewClass != IntPtr.Zero} knob={_knobClass != IntPtr.Zero} " +
                                $"model={_modelClass != IntPtr.Zero} rotateKnob={_rotateKnob != IntPtr.Zero} " +
                                $"normDist={_getNormDistance != IntPtr.Zero} curState={_getCurrentState != IntPtr.Zero} " +
                                $"switchState={_switchState != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[RadioMenu] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
