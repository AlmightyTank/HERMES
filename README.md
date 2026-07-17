# HERMES 0.1.0-alpha13.0.7

## Native flea checkbox visuals

Alpha13.0.7 keeps the Alpha13.0.6 Crafts filters and replaces the custom text-glyph Available indicator with EFT's native Ragfair filter checkbox artwork.

### Crafts toolbar

- Main filters remain **Ready**, **Profitable**, **Active**, and **All**.
- **Available** remains an independent checkbox and can be combined with any main filter.
- HERMES scans the loaded Ragfair filter popup controls and captures their checkbox background sprite, checkmark sprite, colors, and tint settings.
- The entire Available row remains clickable, not only the 18-pixel box.
- Checked and unchecked states are visibly distinct.
- A drawn fallback checkmark is used only when the Ragfair popup artwork has not loaded yet.

### Included compatibility fixes

- Keeps the compact status strip sizing fix.
- Uses `IndexOf(..., StringComparison.Ordinal)` for SPT/Unity-safe map matching.

### Expected log

```text
HERMES 0.1.0-alpha13.0.7 native flea checkbox controls loaded.
HERMES captured native Ragfair filter checkbox '...' with checkmark sprite '...'.
```

If the native artwork is not loaded at the moment the Crafts page is first built, HERMES logs a warning and displays a visible fallback checkmark rather than an invisible text glyph.

## Installation

Overlay the `Client` and `Server` folders onto the current HERMES source tree, clean `bin` and `obj`, rebuild the solution in Visual Studio, and restart the SPT server and EFT client.
