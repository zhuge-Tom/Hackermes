# Hackermes bundled tools

This directory contains independently distributed third-party tools and the portable runtime they need.
Each tool remains under its own upstream license; inclusion here does not relicense it as Hackermes code.

Rules:

- A tool directory name is its stable Hackermes tool id.
- `manifest.json` records upstream provenance, version/commit and redistribution status.
- Build output copies this directory to `tools/` beside `Hackermes.App.exe`.
- Hackermes resolves only application-relative bundled entry points. It never falls back to user-machine absolute paths.
- Proprietary, license-unclear and exceptionally large tools must not be copied here without an explicit distribution decision.
- `gui.*` directories hold human-only GUI tools launched from the security tools panel
  against the bundled JavaFX/Swing runtime (`_runtime/javafx/lib` + a Java 21 runtime on
  PATH). They are NOT agent-callable and never run through the ToolHost pipeline.
