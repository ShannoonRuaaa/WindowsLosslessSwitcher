# intro.9 update review

Checked October 28, 2026. Installed Apple Music: 1.1792.39682.0.
Fetched origin and tags successfully. Upstream main remains 8084c5eec63fbb064200fa750201be5529180fad (July 31, 2026); latest fetched version tag is v1.0.0-beta.7. No newer main-branch code to integrate.

## Resume correction
Existing October 24 logs show an intro restart followed by an acknowledged Play, then a Paused snapshot incorrectly treated as a user pause. The coordinator now retains ownership of its pause until Playing is observed. Each acknowledged play gets up to one second for state propagation; existing three-attempt retry limits remain. Confirmation exits immediately once Playing appears. Subsequent user pauses remain respected.

The media API does not identify who caused a pause. A user pause before the first Playing observation can be indistinguishable from a pending app-owned resume during this bounded handoff.

Retained: automatic rate/bit-depth matching, intro replay preference, native recording corrections, cache, same-format fast path, recovery, shutdown/cancellation handling, and intro.8 dark palette. No Windows audio settings changed.

This fixes a resume-state race, not the hardware stream rebuild delay. Intro replay can also wait for a safe Previous position where Apple Music cannot seek. Real playback on this Apple Music build and DAC still needs listening verification.

Upstream: https://github.com/jordanmgibson/WindowsLosslessSwitcher/commits/main/
