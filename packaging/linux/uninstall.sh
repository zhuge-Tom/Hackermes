#!/usr/bin/env bash
set -euo pipefail

install_dir="${XDG_DATA_HOME:-$HOME/.local/share}/hackermes"
bin_path="$HOME/.local/bin/hackermes"
desktop_path="${XDG_DATA_HOME:-$HOME/.local/share}/applications/hackermes.desktop"

rm -f "$bin_path" "$desktop_path"
rm -rf "$install_dir"
echo "Hackermes was removed. User configuration under ~/.local/share/Hackermes was preserved."
