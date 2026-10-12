# Changelog

## 0.2 — 2026-10-12

- Render live window previews through DirectComposition, with hardware Direct3D/Direct2D surfaces for labels, highlights and backings.
- Improve high-refresh-rate gesture tracking with compositor-clock pacing, dynamic-refresh boosting and touch resampling.
- Run release, close and restore-correction animations in the compositor, independently of the UI thread.
- Eliminate click-to-app flicker by keeping thumbnail rasters stable and scaling them on the GPU.
- Keep minimized-window restore covers on the GPU, without CPU screenshot readback.
- Add desktop close controls that appear on preview hover, with square white containers at 10% opacity and a separate button hover state.
- Move the compact add-desktop control to the far right.
- Animate desktop additions, removals and preview rearrangement. Preserve windows on a neighbouring desktop and protect the last desktop.
- Add layout, selection and animation regression coverage; 94 tests pass.
