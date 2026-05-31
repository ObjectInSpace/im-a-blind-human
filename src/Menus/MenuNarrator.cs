using System;
using MelonLoader;
using NoImNotAHumanAccess.Speech;
using UnityEngine;
using UnityEngine.EventSystems;

namespace NoImNotAHumanAccess.Menus
{
    /// <summary>
    /// Speaks the currently focused UI control whenever menu selection changes. Polls the active
    /// <see cref="EventSystem"/>'s <c>currentSelectedGameObject</c> each frame; the game drives selection
    /// through InputSystemUIInputModule -> EventSystem, so the game's own arrow/WASD/Enter/Escape/Tab
    /// navigation flows through here. Control description (label, role, value) is built by
    /// <see cref="ControlDescriber"/>.
    /// </summary>
    public sealed class MenuNarrator
    {
        private readonly ISpeechOutput _speech;
        private int _lastSelectedId;
        private string _lastSpoken = string.Empty;
        private string _lastValue = string.Empty; // value of the focused control, for in-place change detection

        public MenuNarrator(ISpeechOutput speech) => _speech = speech;

        public void Tick()
        {
            try
            {
                EventSystem? es = EventSystem.current;
                if (es == null) { _lastSelectedId = 0; return; }

                GameObject? selected = es.currentSelectedGameObject;
                if (selected == null) { _lastSelectedId = 0; return; }

                int id = selected.GetInstanceID();

                if (id != _lastSelectedId)
                {
                    // Focus moved to a new control: speak the full description (label, role, value).
                    _lastSelectedId = id;
                    string description = ControlDescriber.Describe(selected);
                    _lastValue = ControlDescriber.ReadValue(selected);
                    if (string.IsNullOrWhiteSpace(description) || description == _lastSpoken) return;
                    _lastSpoken = description;
                    _speech.Speak(description, interrupt: true);
                    return;
                }

                // Same control still focused: if its value changed in place (slider drag, toggle, cycle),
                // speak just the new value so adjustments are heard without leaving and returning.
                string value = ControlDescriber.ReadValue(selected);
                if (!string.IsNullOrEmpty(value) && value != _lastValue)
                {
                    _lastValue = value;
                    _speech.Speak(value, interrupt: true);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[MenuNarrator] Tick threw: {e.Message}");
            }
        }
    }
}
