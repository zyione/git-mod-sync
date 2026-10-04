# ModSync Sync Block icon

Approved option A: cube with opposing synchronization arrows.

- `modsync-dark.png`: bright azure, cyan and mint on a navy tile.
- `modsync-light.png`: deep cobalt, teal and jade on a pale tile.
- Matching `.ico` files contain 16, 20, 24, 32, 40, 48, 64, 128 and 256 px frames with transparent corners.

The running window and compact header use the app theme's palette. The executable and shortcuts use the fixed dark icon, whose tile and perimeter remain distinguishable on light and dark backgrounds. Windows pinned shortcuts can cache the executable icon independently of the running window.

Regenerate the ICO files from the source artwork with `powershell -STA -File scripts/build-icons.ps1` from the repository root. This packages the approved artwork without requiring an image-editing dependency.
