# Update review — November 9, 2026

Upstream main (8084c5e) is already included in this custom intro.10 build; no upstream commits need merging. GitHub's latest-release API returned 404, so a published-release comparison could not be confirmed.

Updated Microsoft.SourceLink.GitHub from 8.0.0 to 10.0.403 to remove vulnerable transitive Microsoft.Build.Tasks.Git 8.0.0 (GHSA-23fw-v26w-5fgq). NuGet's transitive vulnerability check reports no known vulnerable packages after restore.

Installed .NET SDK 8.0.427 locally in the workspace. The Release publish and WPF interface verification passed. The installed application has not been replaced; the rebuilt executable is under artifacts/publish/win-x64.

Available major upgrades to NAudio and Velopack were not adopted in this security repair because audio and installer compatibility requires separate verification.

The skipped-switch mute regression now observes the start of recovery directly instead of waiting for recovery completion; its focused rerun passed.

Full original suite: 393 passed, one timed out (the skipped-switch mute test). After correcting that test's observation point, its focused rerun passed. The full suite was not repeated after the test-only correction. Release publish and UI verification passed separately with the patched dependency.
