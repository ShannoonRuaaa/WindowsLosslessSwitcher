using System.Diagnostics;

namespace WindowsLosslessSwitcher.Services;

internal sealed record RestartPlaybackSnapshot(string TrackKey, TimeSpan? Position, bool CanSeek, bool CanPrevious, bool IsPaused);

/// <summary>Apple Music can acknowledge a seek without moving. Never treat that as a restart.</summary>
internal static class TrackRestartController
{
    internal static readonly TimeSpan SafePreviousPosition = TimeSpan.FromSeconds(5);

    // A pause is acknowledged before GSMTC reflects it; give the reported status time to settle
    // instead of abandoning the restart on the first (stale) Playing read.
    internal static readonly TimeSpan DefaultPauseSettleTimeout = TimeSpan.FromSeconds(1);

    // A seek either lands quickly or not at all; keep this short so a no-op seek does not add
    // much silence before the Previous fallback.
    private static readonly TimeSpan DefaultSeekConfirmationTimeout = TimeSpan.FromMilliseconds(900);

    // After Previous, Apple Music may briefly publish placeholder metadata and only refreshes its
    // timeline on its own schedule. Field logs showed restarts that really happened being
    // reported as failures because confirmation gave up after the first unexpected read.
    private static readonly TimeSpan DefaultPreviousConfirmationTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan StrictStartWindow = TimeSpan.FromMilliseconds(500);

    // Apple Music reports whole-second timeline positions and may resume on its own after
    // Previous, so a read of up to 1.5 s counts as "restarted" when it is also clearly behind the
    // pre-restart position.
    private static readonly TimeSpan RelaxedStartWindow = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MinimumRestartDrop = TimeSpan.FromSeconds(2);

    internal static async Task<bool> TryRestartAsync(
        string expectedTrackKey,
        Func<CancellationToken, Task<RestartPlaybackSnapshot>> read,
        Func<CancellationToken, Task<bool>> seek,
        Func<CancellationToken, Task<bool>> previous,
        CancellationToken cancellationToken,
        TimeSpan? confirmationTimeout = null,
        TimeSpan? pauseSettleTimeout = null,
        Action<string>? log = null)
    {
        var seekTimeout = confirmationTimeout ?? DefaultSeekConfirmationTimeout;
        var previousTimeout = confirmationTimeout ?? DefaultPreviousConfirmationTimeout;
        var settleTimeout = pauseSettleTimeout ?? DefaultPauseSettleTimeout;

        var snapshot = await ReadSettledAsync();
        if (!Matches(snapshot))
        {
            log?.Invoke($"restart skipped: current recording '{snapshot.TrackKey}' is not the expected one");
            return false;
        }

        if (!snapshot.IsPaused)
        {
            log?.Invoke("restart skipped: playback never reported Paused");
            return false;
        }

        if (AtStart(snapshot.Position)) return true;
        var before = snapshot.Position;

        if (snapshot.CanSeek)
        {
            if (await seek(cancellationToken) && await ConfirmAsync(seekTimeout, before)) return true;
        }

        // Re-read after a slow/no-op seek: a user skip must never become Previous on the new song.
        snapshot = await read(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(snapshot) || !snapshot.IsPaused)
        {
            log?.Invoke("restart skipped: playback changed while the seek was pending");
            return false;
        }

        if (AtStart(snapshot.Position)) return true;
        if (snapshot.Position is null || snapshot.Position < SafePreviousPosition)
        {
            log?.Invoke(
                $"restart skipped: seek unavailable and Previous is unsafe this early (position={snapshot.Position?.ToString() ?? "n/a"})");
            return false;
        }

        if (!snapshot.CanPrevious)
        {
            // Apple Music reports Previous as unavailable for some queues (seen live on half of
            // the restarts after install). Past 5 s, Previous restarts the current recording;
            // if it is really disabled the command is simply rejected, and the confirmation
            // below verifies the same recording is back at the start either way.
            log?.Invoke($"Previous is reported unavailable at {snapshot.Position}; sending it anyway and verifying the result");
        }

        before = snapshot.Position;
        if (!await previous(cancellationToken))
        {
            log?.Invoke("restart failed: Previous was rejected");
            return false;
        }

        if (await ConfirmAsync(previousTimeout, before)) return true;
        log?.Invoke("restart unconfirmed: the timeline did not return to the start after Previous");
        return false;

        bool Matches(RestartPlaybackSnapshot value) => value.TrackKey == expectedTrackKey;
        static bool AtStart(TimeSpan? position) =>
            position is { } value && value >= TimeSpan.Zero && value <= StrictStartWindow;
        static bool Restarted(TimeSpan? position, TimeSpan? reference) =>
            AtStart(position) ||
            (position is { } value && value >= TimeSpan.Zero && value <= RelaxedStartWindow &&
             reference is { } origin && origin - value >= MinimumRestartDrop);

        async Task<RestartPlaybackSnapshot> ReadSettledAsync()
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                var current = await read(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!Matches(current) || current.IsPaused || elapsed.Elapsed >= settleTimeout)
                {
                    return current;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }

        async Task<bool> ConfirmAsync(TimeSpan timeout, TimeSpan? reference)
        {
            var elapsed = Stopwatch.StartNew();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await read(cancellationToken);
                // A transient mismatch (Apple Music's "Connecting…" placeholder) is not a
                // verdict; a real track change cancels the caller through its own token.
                if (Matches(current) && Restarted(current.Position, reference)) return true;
                if (elapsed.Elapsed >= timeout) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            } while (true);
        }
    }
}
