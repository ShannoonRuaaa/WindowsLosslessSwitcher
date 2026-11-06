# Intro-preserving custom build

Modified from jordanmgibson/WindowsLosslessSwitcher commit
`8084c5eec63fbb064200fa750201be5529180fad` (five commits after v1.0.0-beta.7).
This is an independent modified build, version `1.0.0-intro.10`. Upstream attribution,
GPL-3.0-or-later licensing, and third-party notices are retained.

## Run

Exit any running Windows Lossless Switcher from its tray menu, then launch
`WindowsLosslessSwitcher.exe`. This self-contained x64 build includes .NET; no SDK,
installer, or DLL patch is required. Windows may warn because the executable is unsigned.
Keep the EXE in a permanent folder if you enable launch at login. Native runtime libraries
are extracted to the normal .NET single-file cache on first launch.

The app shares the upstream settings/cache location in
`%APPDATA%\WindowsLosslessSwitcher`. Existing settings are honored. The new
**Switching > Replay the intro after a format change** setting defaults to on; its JSON key is
`rewindAfterFormatSwitch`. Turn it off to retain upstream's timeline-position behavior.
User-set endpoint mute remains respected. The custom release disables upstream update
checks at build time so a stock release cannot replace these changes through the updater.

## Pinned output fix (intro.10)

Local-file format selection now uses the chosen output device's supported formats.
Earlier builds always checked the Windows default device, even when a different DAC
was pinned. This could choose an unsupported format or overlook one the pinned DAC
can play. The default and pinned device paths now share the same selection rule,
with a regression test covering a 44.1 kHz default and 96 kHz pinned DAC.

## GitHub issue fixes (intro.6)

Immediately known matching formats now bypass mute and restart, including consecutive
cached tracks. Pending network lookups can still be muted when intro replay is on.
Unknown-rate fallback keeps the endpoint format and displays Undetermined, rather
than forcing a guessed rate. Catalog matching recognizes a verified artist alias
and reordered complete credits, and gives album-specific queries a chance to find
the right edition. Matching-cache version 3 drops stale positive/no-match results;
saved native format corrections are preserved. See ISSUE_FIXES.md for coverage,
primary sources, and the hardware/reporting limitations. Publication remains on hold.

## Intro preview fix (intro.5)

With intro replay enabled, mute before resolving the new track and keep preparation
silent until the confirmed restart. This avoids deliberately playing the opening
and then repeating it after a format change. An unchanged-format track can have a
brief lookup gap. Turn intro replay off to retain audible resolution without rewind.
Track metadata can arrive late from Windows; the mute starts when the app detects
and processes the new recording. Default-device selection now avoids enumerating
all endpoints; pinned-device selection and stability/recovery safeguards remain.
These changes await listening approval before being added to the upstream draft PR.

## Interface refresh (intro.4)

The Windows UI now uses Segoe UI system typography, 14-DIP body text, 12-DIP captions,
clear sentence-case labels, wrapping settings rows, consistent field and button sizing,
and grouped settings. Native format selectors show units such as 44.1 kHz and 24-bit.
Windows light/dark appearance and contrast colors are followed automatically. No macOS
binary or Apple font is included. See UI_DESIGN.md for the platform guidance and checks.

Settings has a standard title area, maximize/restore, keyboard navigation, accessible
control names, Escape to close, and scrolling at compact sizes. Ctrl+, opens settings
from the main window. Launching the app again activates its existing window. The custom
release badge says Custom build instead of claiming an update check has succeeded.

The fixed-output option introduced in intro.3 has been removed from the UI. An old
fixed-output preference is reset to off at startup. Automatic format matching and the
existing recording corrections are retained; this is not an exclusive audio renderer.

## Native format corrections (intro.2)

The initial build inherited an upstream detection limitation: Apple's public web catalog can
advertise a different ALAC format from the native Windows player. Reproduced with **Dirty
Diana / Michael Jackson / Number Ones**: catalog song ID `273598924` exposes 24/48 in its
public enhanced HLS manifest, while the native player reports 24/96. The existing cache was
correctly storing that web result; clearing it alone cannot supply the native format.

In **Switching > Native format correction**, select the rate and bit depth displayed by
Apple Music's Lossless popup and choose **Save and apply**. The correction is persisted in
`settings.json` under `trackFormatCorrections`, keyed by the exact title/artist/album, and
takes priority over all catalog/cache lookups. **Remove correction** restores automatic
resolution for that recording. Different albums, artists, and live/remix titles are unaffected.
The source label explicitly reads **Native correction**. There is no global force-to-96-kHz
setting or built-in assumption that every Hi-Res track is 96 kHz.

This is a durable per-recording correction mechanism, not automatic native stream inspection.
Other streaming tracks still use the upstream catalog/file/fallback chain until corrected.

## Changes

- After an actual live sample-rate/bit-depth change, wait for the rebuilt Apple renderer,
  pause, restart the same recording at 0:00, unmute, and play. Native Apple Music on this
  computer reports seek disabled and acknowledges direct seeks without moving. The new
  restart checks metadata and position; it uses seek when supported, otherwise a single
  Previous command only at five seconds or later, with the same recording paused. Both
  commands require confirmation that the same recording reached 0:00. A failed/unsafe
  restart resumes at the existing position instead of repeatedly sending Previous.
  Radio/autoplay sessions can disable both seek and Previous; those sessions cannot
  be forced to restart safely through the available media commands.
- With intro replay on, mute during resolution and stream startup to suppress a preview.
  With replay off, only a real device-format change is muted.
- A different format detected while paused is deferred until playback resumes. This avoids
  invalidating a dormant Apple renderer that can otherwise stay silent when reused.
  A native resume event retries the pending recording; the watchdog also checks for a
  missed resume event. User pause takes priority, including during resolution.
- Fixed output is not offered in intro.4; automatic format matching remains enabled.
- Rewind at most once successfully per track generation. Recovery after a switched track
  can also request the intro rewind. Unchanged-format tracks and later watchdog
  recoveries never request an intro rewind.
- Track cancellation transfers owned pauses to the successor. Shutdown keeps the media
  command session available until owned pauses are released. User pauses/stops during
  playback observation are respected.
- Realign a healthy renderer with a 100 ms settle instead of 400 ms. Dead-renderer recovery
  retains its 400 ms settle. Resume retries use 100 ms gaps instead of 250 ms.
- Poll for device stability at 50 ms instead of 100 ms, still requiring two agreeing reads.
  Core Audio format verification checks at 0, 50, 150, 300, and 800 ms rather than
  0, 200, 400, 600, and 800 ms. The full propagation budget is preserved.
- Use a single Core Audio session enumeration for combined rebuild activity/peak checks.
  Poll at 50 ms during the first second, then back off to the original cadence. Idle
  watchdog cadence is unchanged. Switching timeouts use a monotonic stopwatch.
- Release enumerated audio devices deterministically and reuse the default-device identity
  already returned by enumeration. Superseded tracks release their owned mute before
  the successor starts a potentially slow format lookup.
- Publish an optimized Release, ReadyToRun, untrimmed executable with its own .NET runtime.
  Single-file compression is disabled to avoid startup decompression overhead.

## What was deliberately retained

The 1.5-second sustained-render-stream gate protects against switching into the previous
track's dying stream. Gemini's 100 ms replacement removed that protection. The placeholder
metadata debounce remains 2 seconds to avoid resolving/switching on temporary "Connecting"
metadata. Recovery timeouts and escalation cooldowns remain in place. The supplied binary
offset patch was reviewed but never executed; installed binaries were not patched.

Windows and Apple's renderer still control the actual DAC/stream rebuild time. These
changes do not eliminate hardware settling or improve the music's encoded quality. Timing
changes above describe code paths, not measured speedups on your audio hardware.

## Build from this source

Install the .NET 8 SDK, then run in PowerShell:

```powershell
./Build-Optimized.ps1
```

This runs the regression suite before publishing to `artifacts/publish/win-x64`.
An isolated WPF harness then loads the actual compiled settings page and verifies the
rewind toggle's two-way binding and the custom release's disabled upstream updater. It
does not start audio services or open/change the user's settings.
For a specific SDK use `-DotNetPath C:\path\to\dotnet.exe`. ARM64 source publishing is
available with `-Runtime win-arm64`; only x64 is delivered/tested here.
NuGet restore requires internet access. No installed application is stopped or modified
by the build script. `-SkipTests` is available for a repeat publish after successful tests.

The test suite uses simulated transport and endpoint controllers. It covers verified
restarts, no-op seeks, unsafe Previous positions, metadata changes, fixed output mode,
disabled rewind, cancellation, shutdown, user pause/mute, recovery, rapid
track changes, and driver verification timing. A successful test run does not certify
real Apple Music/DAC compatibility. Auditory confirmation of the exact first sample,
track endings, quiet intros, and rapid skips still requires your actual playback setup.
