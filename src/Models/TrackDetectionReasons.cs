namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// <see cref="TrackSnapshot.DetectionReason"/> values the switching pipeline reacts to.
/// </summary>
public static class TrackDetectionReasons
{
    /// <summary>
    /// The track source attached to an Apple Music session (app start, or Apple Music relaunched)
    /// and published whatever that session was already playing.
    /// </summary>
    public const string SessionAttached = "session-attached";

    /// <summary>The current track is being re-evaluated after a settings or correction change.</summary>
    public const string Reprocess = "reprocess";
}
