#!/usr/bin/env bash
set -euo pipefail

source_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/app"
install_dir="${XDG_DATA_HOME:-$HOME/.local/share}/hackermes"
bin_dir="$HOME/.local/bin"
applications_dir="${XDG_DATA_HOME:-$HOME/.local/share}/applications"

test -x "$source_dir/Hackermes.App" || {
  echo "Hackermes.App is missing or not executable: $source_dir/Hackermes.App" >&2
  exit 1
}

mkdir -p "$install_dir" "$bin_dir" "$applications_dir"
rm -rf "$install_dir/app"
cp -a "$source_dir" "$install_dir/app"
ln -sfn "$install_dir/app/Hackermes.App" "$bin_dir/hackermes"

cat > "$applications_dir/hackermes.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Hackermes
Comment=Web debugging, traffic analysis and authorized assessment workbench
Exec=$install_dir/app/Hackermes.App
Icon=$install_dir/app/Assets/hackermes-icon.png
Terminal=false
Categories=Development;Network;
EOF

echo "Hackermes installed to $install_dir/app"
echo "If $bin_dir is on PATH, run: hackermes"
