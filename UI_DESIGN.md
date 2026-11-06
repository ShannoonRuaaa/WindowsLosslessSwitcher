# Interface decisions and validation

This remains a Windows WPF application. Apple’s guidance informs clarity, hierarchy,
grouping, and adaptability; Windows conventions govern its fonts, keyboard shortcuts,
window controls, appearance, and accessibility. This is not a macOS port or a claim of
complete Human Interface Guidelines or accessibility certification.

## Guidance used

- [Apple: Typography](https://developer.apple.com/design/human-interface-guidelines/typography): readable sizes, hierarchy, and restrained typeface choices.
- [Apple: Layout](https://developer.apple.com/design/human-interface-guidelines/layout): adapt to available space and avoid losing content when resized.
- [Apple: Settings](https://developer.apple.com/design/human-interface-guidelines/settings): group related settings, minimize prominent options, and respect system appearance.
- [Microsoft: Typography in Windows](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography): system fonts, sentence case, and the 14/20 and 12/16 text hierarchy.
- [Microsoft: Content layout and spacing](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics): consistent alignment, grouping, and spacing.

## Applied changes

- Use neutral charcoal surfaces and soft red active controls inspired by Apple Music.
  Keep artwork-derived colors, a matching neutral light palette, and Windows contrast overrides.

- Use Segoe UI Variable Text with Segoe UI fallback. Remove artificial letter spacing
  from labels. Body text is 14 DIP; supporting text is at least 12 DIP. Use semibold
  for important titles and opaque supporting text with tested contrast.
- Separate labels and toggles into wrapping columns. Group output-device, playback,
  recording correction, and advanced options. Keep uncommon device limits and recovery
  options in a disclosure rather than crowding the main settings surface.
- Make format fields consistent with the app’s appearance and display rates in kHz and
  depths in bits. Preserve the underlying numeric settings and culture-specific decimal
  separators. Provide accessible field labels, toggle names, visible keyboard focus,
  readable dropdown items, and full-text tooltips for truncated metadata.
- Follow Windows app light/dark preference and contrast colors without changing any
  system preferences. Keep controls and text legible in all three resource palettes.
- Give Settings a full-width title area and maximize/restore control. Constrain initial
  windows to the available work area, scroll content in compact windows, and retain
  the app’s tray behavior. Ctrl+, opens settings; Escape dismisses a dropdown before closing the settings window.
- Reopening the EXE requests activation of the existing main window using a local
  session event rather than showing an already-running dialog.
- Remove fixed-output UI and clear a legacy fixed-output preference at startup. Preserve
  native-rate matching, intro restart, startup registration, and recording corrections.
  This does not add exclusive playback or establish bit-perfect output.

## Verification scope

The isolated UI harness loads the actual release BAML without starting audio services
or reading the user’s settings. It checks the intro and native-format bindings, the
absence of fixed-output UI, five settings pages at 760- and 920-DIP widths, compact
window layouts, control bounds, accessible toggle names, palette contrast, and deferred
keyboard focus-style resolution. Focus resources are declared before their consumers
to prevent a crash when dismissing a dropdown with the keyboard.
It renders normal, light, contrast, and 200%-scale raster previews for visual inspection.
These raster checks do not replace testing every monitor, Windows text-size setting,
keyboard layout, or assistive technology. The installed app is additionally inspected
through its real UI, including settings navigation and dropdown behavior.

The non-coordinator regression suite covers the new formatting/converter behavior,
resource restoration, and existing-instance activation. The switching coordinator,
Apple media transport, endpoint controller, and format-selection policy are unchanged
from intro.3, whose full 362-test and live SMSL/Apple Music results remain the audio
baseline. That baseline is not presented as a new hardware listening test of intro.4.
