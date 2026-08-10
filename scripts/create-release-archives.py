from __future__ import annotations

import argparse
import hashlib
import os
import tarfile
import zipfile
from pathlib import Path


def iter_files(root: Path):
    for path in sorted(root.rglob("*")):
        if path.is_file():
            yield path


def create_zip(source: Path, destination: Path) -> None:
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in iter_files(source):
            archive.write(path, Path(source.name) / path.relative_to(source))


def create_tar(source: Path, destination: Path) -> None:
    executable_names = {"Hackermes.App", "Hackermes.ToolHost", "install.sh", "uninstall.sh"}

    def filter_info(info: tarfile.TarInfo) -> tarfile.TarInfo:
        info.uid = 0
        info.gid = 0
        info.uname = "root"
        info.gname = "root"
        info.mtime = 0
        info.mode = 0o755 if info.isdir() or Path(info.name).name in executable_names else 0o644
        return info

    with tarfile.open(destination, "w:gz", compresslevel=9) as archive:
        archive.add(source, arcname=source.name, recursive=True, filter=filter_info)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--windows", type=Path)
    parser.add_argument("--linux", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)
    if args.windows is None and args.linux is None:
        parser.error("at least one of --windows or --linux is required")

    archives: list[Path] = []
    if args.windows is not None:
        windows_archive = args.output / f"Hackermes-{args.version}-windows-x64.zip"
        create_zip(args.windows, windows_archive)
        archives.append(windows_archive)
    if args.linux is not None:
        linux_archive = args.output / f"Hackermes-{args.version}-linux-x64.tar.gz"
        create_tar(args.linux, linux_archive)
        archives.append(linux_archive)

    checksum_file = args.output / "SHA256SUMS.txt"
    checksum_file.write_text(
        "".join(f"{sha256(archive)}  {archive.name}\n" for archive in archives),
        encoding="utf-8",
        newline="\n",
    )


if __name__ == "__main__":
    main()
