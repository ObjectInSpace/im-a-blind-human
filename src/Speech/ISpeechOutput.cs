namespace NoImNotAHumanAccess.Speech
{
    /// <summary>
    /// Thin output seam over whatever screen-reader channel is active. The rest of the mod
    /// (dialogue hooks, menu narration) talks only to this interface, so switching the channel
    /// (native Unity AssistiveSupport vs. UnityAccessibilityLib) is a configuration change, not a
    /// rewrite. This is NOT a hybrid runtime: exactly one implementation is live at a time.
    /// </summary>
    public interface ISpeechOutput
    {
        /// <summary>True if this channel believes it can currently deliver speech to a screen reader.</summary>
        bool IsAvailable { get; }

        /// <summary>Speak a line. <paramref name="interrupt"/> requests the channel cut off current speech.</summary>
        void Speak(string text, bool interrupt = false);

        /// <summary>Human-readable channel name for logs/diagnostics.</summary>
        string Name { get; }
    }
}
