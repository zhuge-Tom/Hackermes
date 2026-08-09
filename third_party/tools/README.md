# Hackermes bundled tools

This directory contains independently distributed third-party tools and the portable runtime they need.
Each tool remains under its own upstream license; inclusion here does not relicense it as Hackermes code.

Rules:

- A tool directory name is its stable Hackermes tool id.
- `manifest.json` records upstream provenance, version/commit and redistribution status.
- Build output copies this directory to `tools/` beside `Hackermes.App.exe`.
- Hackermes resolves only application-relative bundled entry points. It never falls back to user-machine absolute paths.
- Proprietary, license-unclear and exceptionally large tools must not be copied here without an explicit distribution decision.
