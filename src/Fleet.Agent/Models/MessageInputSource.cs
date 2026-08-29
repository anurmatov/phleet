namespace Fleet.Agent.Models;

/// <summary>
/// How the text of an <see cref="IncomingMessage"/> was produced.
///
/// This is provenance, not content: it is carried as structured state rather than
/// concatenated into the user's text, so the text stays byte-identical to what the
/// user typed or what the transcription service returned. Only PromptAssembler turns
/// it into a visible marker, and only in the agent-facing prompt.
/// </summary>
public enum MessageInputSource
{
    /// <summary>Text the user typed, or a caption they wrote. The default.</summary>
    Typed = 0,

    /// <summary>
    /// Text produced by speech-to-text from a voice message. Set only when
    /// transcription actually succeeded — a failed or disabled transcription must
    /// leave the value at <see cref="Typed"/> rather than claim a transcript exists.
    /// </summary>
    VoiceTranscription = 1,
}
