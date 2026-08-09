# Third-party components

Hackermes itself and the bundled third-party tools are separate works. Each
tool retains its own license and upstream attribution. The machine-readable
inventory is available at `app/tools/manifest.json`.

The public Windows package includes the components whose license/source
information is present in that manifest: CPython, Nmap, Dirsearch, Wafw00f,
SQLmap and xssFuzz. The Linux preview package includes the redistributable
Python tool sources but relies on a system Python environment.

Components marked `redistribution-unverified` are intentionally not included
in public release archives. Their menu entries remain visible as unavailable
so users understand the intended integration point without receiving an
unlicensed binary.
