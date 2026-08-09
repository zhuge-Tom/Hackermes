# Hackermes @VERSION@ for @RID@

This archive is self-contained and does not require a separately installed
.NET runtime.

## Install

- Windows: run `Install-Hackermes.ps1` from PowerShell. The default destination
  is `%LOCALAPPDATA%\Programs\Hackermes`.
- Linux: run `chmod +x install.sh && ./install.sh`. The default destination is
  `$XDG_DATA_HOME/hackermes` or `~/.local/share/hackermes`.

The application files are also portable: they can be run directly from the
`app` directory.

## Platform notes

- Windows 10/11 x64 is the fully verified desktop target. The embedded browser
  requires Microsoft Edge WebView2 Runtime.
- Linux x64 is a preview target. Install the Avalonia desktop dependencies and
  WebKitGTK supplied by your distribution. WebView2-specific CDP integration is
  currently available only on Windows.

Security and assessment features must only be used against systems you own or
are explicitly authorized to test.
