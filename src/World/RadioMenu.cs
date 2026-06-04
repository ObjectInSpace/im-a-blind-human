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
        private IntPtr _switchState;      // RadioModel.SwitchState(ERadioState)
        private IntPtr _getDisplayedMessage; // RadioModel.GetDisplayedMessage() : string

        // RadioModel._isWaveFound (private bool): TRUE only when the station is locked and the text is FULLY resolved.
        // This is the correct gate for speaking the message — the old distance threshold (0.06) still had partial
        // garble, and GetDisplayedMessage() changes char-by-char as it resolves, so each frame produced a DIFFERENT
        // near-garbled string that passed the dedupe and queued (interrupt:false) → dozens of strings flooded the speech
        // buffer and overloaded the mod (the user's "garbage spam" report). We now speak ONLY when _isWaveFound is true.
        private const string IsWaveFoundField = "_isWaveFound";
        // RadioModel._message (private string): the CLEAN, full station text. GetDisplayedMessage() applies the noise
        // garble on top of this as it resolves char-by-char; reading _message directly means we NEVER speak garble, even
        // if the wave-found flag briefly overlaps a still-garbling display when drifting on/off the station.
        private const string MessageField = "_message";
        // RadioModel._onFoundProgresss (private float, note the game's triple-s typo): the de-garble progress, 0 → 1,
        // that advances only while the player holds close to a station. >0-but-not-found = "actively resolving".
        private const string OnFoundProgressField = "_onFoundProgresss";

        // Distance at/above which the signal is FULL garble (pure static) — at this range we never read the text, only
        // the "Static." closeness cue. Below it the text is PARTIALLY resolved and worth reading on settle. Matches the
        // Closeness() "Static" cutoff so the buckets and the read threshold agree.
        private const float FullGarbleDistance = 0.45f;

        private int _lastBand = -1;
        private string _lastCloseness = string.Empty;
        private float _nextClosenessSpeakTime;
        private string _lastSpokenMessage = string.Empty; // de-dupe the on-signal message readout
        private bool _announcedResolving;                  // spoke the "resolving signal" cue once for this approach

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

                float dist = Il2CppRaw.InvokeFloatGetter(model, _getNormDistance, 1f);

                // Station-text readout. The sighted player WATCHES the message fill in character-by-character as they
                // hold close to a station; it only completes if they stay put. We don't read the partial garble (it
                // changes every frame — annoying and meaningless), and we never read full static. Instead:
                //
                //  • RESOLVING (close, signal is actively filling in, not yet fully found): speak a ONE-SHOT cue
                //    ("Resolving signal. Hold steady.") so the player knows a full message is coming if they wait — the
                //    audio analog of seeing the text fill in. Spoken once per approach; re-armed when they leave the
                //    resolving zone, so a fresh approach cues again. If they tune away before it finishes, nothing more
                //    is said — the message is simply dropped (you only ever hear the clean version, never a partial).
                //  • WAVE FOUND: speak the CLEAN station text (the model's _message field) once. Reading _message — not
                //    GetDisplayedMessage() — guarantees no leftover noise.
                //
                // The clean read is de-duped on _lastSpokenMessage, which we do NOT reset on wave-loss: drifting off a
                // station you've heard must not re-read it, and drifting back on must not repeat it. A different station
                // has different text, so it still speaks; only the SAME text is suppressed once spoken.
                bool waveFound = Il2CppRaw.ReadBoolField(model, _modelClass, IsWaveFoundField);
                // "Actively resolving": the game's on-found progress has started (>0) but hasn't completed, and we're in
                // signal range. _onFoundProgresss only advances while close, so it's the truest "filling in" signal;
                // distance is the fallback bound so the cue never fires out in the static.
                float foundProgress = Il2CppRaw.ReadFloatField(model, _modelClass, OnFoundProgressField, 0f);
                bool resolving = !waveFound && dist < FullGarbleDistance && foundProgress > 0f;

                if (waveFound)
                {
                    string msg = (Il2CppRaw.ReadStringField(model, _modelClass, MessageField) ?? string.Empty).Trim();
                    if (msg.Length > 0 && msg != _lastSpokenMessage)
                    {
                        _lastSpokenMessage = msg;
                        _speech.Speak(msg, interrupt: false); // don't cut off — let the resolved station message play out
                    }
                    _announcedResolving = false; // re-arm the cue for the next station
                }
                else if (resolving)
                {
                    if (!_announcedResolving)
                    {
                        _announcedResolving = true;
                        _speech.Speak("Resolving signal. Hold steady.", interrupt: true);
                    }
                }
                else
                {
                    _announcedResolving = false; // left the resolving zone (tuned away / full static) → re-arm
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
            _announcedResolving = false;
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
                    _getDisplayedMessage = Il2CppRaw.GetMethod(_modelClass, "GetDisplayedMessage", 0);
                }

                MelonLogger.Msg($"[RadioMenu] resolved: view={_viewClass != IntPtr.Zero} knob={_knobClass != IntPtr.Zero} " +
                                $"model={_modelClass != IntPtr.Zero} rotateKnob={_rotateKnob != IntPtr.Zero} " +
                                $"normDist={_getNormDistance != IntPtr.Zero} curState={_getCurrentState != IntPtr.Zero} " +
                                $"switchState={_switchState != IntPtr.Zero} displayedMsg={_getDisplayedMessage != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[RadioMenu] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
