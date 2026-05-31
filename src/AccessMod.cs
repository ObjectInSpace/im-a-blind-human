using System;
using MelonLoader;
using NoImNotAHumanAccess;
using NoImNotAHumanAccess.Speech;
using UnityEngine;

[assembly: MelonInfo(typeof(AccessMod), "No I'm Not a Human Access", "0.0.1", "objectinspace")]
[assembly: MelonGame("Trioskaz", "NoImNotAHuman")]

namespace NoImNotAHumanAccess
{
    /// <summary>
    /// Mod entry point. Phase 0 scope only: stand up the native speech channel and prove that a
    /// spoken announcement reaches the OS screen reader (Windows Narrator) from inside this IL2CPP
    /// build. This is the go/no-go gate for the native-or-nothing approach. Feature hooks
    /// (dialogue/subtitles, menu narration) are added in later phases.
    /// </summary>
    public sealed class AccessMod : MelonMod
    {
        private ISpeechOutput? _speech;

        // F8 = manual smoke-test trigger. Chosen because the game's UI/world maps do not bind F8
        // (verified key set: arrows/WASD/enter/space/escape/tab/page/home/end/q/e/f/shift/backspace).
        private const KeyCode SmokeTestKey = KeyCode.F8;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Initializing native speech channel...");
            try
            {
                _speech = new NativeAnnouncer();
                LoggerInstance.Msg($"Speech channel: {_speech.Name}, available={_speech.IsAvailable}");
            }
            catch (Exception e)
            {
                LoggerInstance.Error($"Failed to create native speech channel: {e}");
                _speech = null;
            }
        }

        public override void OnLateInitializeMelon()
        {
            // Announce load once the game is up. If Narrator speaks this, the native path works.
            Speak("No I'm Not a Human accessibility mod loaded. Press F8 to test screen reader output.");
        }

        public override void OnUpdate()
        {
            if (Input.GetKeyDown(SmokeTestKey))
            {
                LoggerInstance.Msg("F8 pressed: sending test announcement.");
                Speak("Screen reader test. If you hear this, native announcements are working.");
            }
        }

        private void Speak(string text)
        {
            if (_speech == null)
            {
                LoggerInstance.Warning($"No speech channel; would have said: {text}");
                return;
            }
            LoggerInstance.Msg($"Speak: {text}");
            _speech.Speak(text);
        }
    }
}
