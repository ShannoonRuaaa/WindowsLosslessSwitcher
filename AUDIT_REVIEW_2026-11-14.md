# Audit review and fixes — November 14, 2026

Reviewed the supplied `windows_lossless_switcher_audit_report.md` against the local custom intro.10 source (base commit 69a3011), rather than applying its patches verbatim. The resulting release is intro.11. Prior security dependency changes are preserved.

## Bug findings

| Finding | Review and disposition |
| --- | --- |
| BUG-01 | Confirmed. Patient retry now records pause ownership and passes the friendly device name to resume diagnostics. Cancellation/disposal regression added. |
| BUG-02 | Rejected as stated. Direct inspection of the installed NAudio 2.2.1 constructor at 48 kHz/24-bit/stereo returned `dwChannelMask=3`, not zero. Existing explicit packed/padded and zero-mask fallback probes are retained. Tests cover positioned integer PCM at 16/24/32 bits. |
| BUG-03 | Confirmed. Removed exclusive-open detection entirely. An open handle also does not establish which process owns it or which track it represents. Local matching retains download-folder, fresh-write, and recent cache-info signals; ambiguous stale replay files now fall through instead of being guessed from a lock. |
| BUG-04 | Confirmed race. Deferred-track registration now rechecks playing state and a resume-event counter, catching a resume between the paused read and registration. Playback state is refreshed before the asynchronous property refresh on Playing events. A deterministic regression checks that switching completes before the 5-second watchdog would retry. |
| BUG-05 | Confirmed. Read sharing permits Apple Music writes. Added trailer, offset, reference, range, integer-size, collection-size, file-size, recursion-depth and total-reference limits; cycle detection and memoization prevent recursive or repeated-reference blowups. Malformed data produces InvalidDataException. |
| BUG-06 | Confirmed. Empty albums match only empty albums, never serve as wildcards. Punctuation-only normalized identities cannot collide. Title and artist still require usable identities. |
| BUG-07 | Confirmed. Text changes stop stale animation and invalidate measurement; animation restarts after arrangement using the new text width. WPF regression covers short-to-long-to-short text at a fixed window width. |
| BUG-08 | Confirmed. Session refresh/attachment is serialized with a semaphore; loss-debounce detachment and disposal use the same gate, and disposal blocks future attachment. |
| BUG-09 | Confirmed for primary-artist extraction. Use the earliest explicit separator; require spaces around `/`; preserve ambiguous band-name punctuation such as AC/DC and Earth, Wind & Fire in search terms. Collaborator-token scoring remains available. |
| BUG-10 | Confirmed. Compare prerelease identifiers using SemVer numeric/text ordering and ignore build metadata. The update badge uses an actual update action rather than unequal version strings. |
| BUG-11 | Confirmed. Serialize and flush to a unique adjacent temporary file, then replace the destination. Failed replacement preserves the existing settings and cleans the temporary file. App-level save failures are logged and shown to the user instead of crashing or being silently swallowed. |
| BUG-12 | Confirmed. Publish copied settings with independent lists; each track operation takes its own snapshot across awaits. Callers cannot mutate the coordinator's published settings through its public getter. Local-device selection uses the coordinator's published snapshot. |
| BUG-13 | Confirmed unbounded growth. Rotate at 5 MiB, retaining one previous log; logging I/O failure cannot interrupt playback. Dispose the named mutex during application shutdown, with serialized/idempotent disposal. Windows would also reclaim process handles on exit, so the original report overstates the permanence of the mutex leak. |

## Optimization findings

| Finding | Disposition |
| --- | --- |
| OPT-01 | Implemented squared-magnitude comparisons, power-of-two ring indexing, and skipped FFT/window/bin work after a full window of exact-zero samples. Bar decay and silence timing still advance. Existing tone, packet, silence and decay tests are retained. No hardware speedup percentage is claimed. |
| OPT-02 | Fixed undisposed MMDeviceEnumerator. Deferred PID-name caching: PID reuse and process restart need an invalidation policy, and the report supplies no measurement of the gain. |
| OPT-03 | Deferred asynchronous cache/log persistence. FormatCacheStore currently promises durable success/failure to callers; dropping that contract or removing its flush would require an explicit queued-write/shutdown/error design. Rotation and nonfatal logging errors are fixed separately. |
| OPT-04 | Source-generated the static bundle and developer-token regexes. Preserved album/search-attempt ordering and the remaining dynamic manifest regex: fewer searches can change which album edition wins, and the claimed network saving was not measured against this setup. |
| OPT-05 | Implemented one media-file snapshot per local lookup, shared by the three matchers. |
| OPT-06 | ALAC reads sample rate and depth from the same cookie and bypasses TagLib for a valid positive-rate ALAC result. Other containers and incomplete cookie metadata retain the existing fallback. Added ALAC rate/depth/probe tests and a container recursion limit. |
| OPT-07 | Reduced settled-idle visualizer polling from 33 ms to 200 ms, retaining periodic wakeup to detect activity; combined artwork sine/cosine calculations. A fully stopped timer would need an activity-change notification to restart. |

## Validation

Validation covers 418 passing cases across runs: all 55 playback coordinator tests, all 362 final non-playback tests, and the separately added resume/deferral race test. The first full run had 413 passes and one test-assertion failure: Windows reported UnauthorizedAccessException rather than IOException for a locked destination. The assertion was corrected to accept either appropriate failure type; all 362 non-playback tests then passed. No playback failure occurred. The whole suite was not repeated after that assertion-only correction; subsequent parser/probe changes were verified with the complete non-playback subset.

Release WPF checks passed, including settings bindings, layouts, contrast/accessibility checks, and fixed-width marquee transitions. Results are in artifacts/test-results/audit-regression.trx, audit-final-fast.trx, and audit-resume-race.trx.

The tests simulate playback and audio endpoints. Actual Apple Music/DAC switching, quiet intros and device behavior still require listening on the physical setup. Performance changes have not been benchmarked on that hardware.

Installed intro.11 on November 14, 2026. Verified the installed executable SHA256 against the release and confirmed it remained running after restart. Previous installed files are preserved in deliverables/installed-backup-20261114-043756. The reusable installer now targets the intro.11 payload and matching checksum. Settings were not changed by deployment.
