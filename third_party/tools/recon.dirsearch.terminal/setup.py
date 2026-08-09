#!/usr/bin/env python3
# -*- coding: utf-8 -*-
#  This program is free software; you can redistribute it and/or modify
#  it under the terms of the GNU General Public License as published by
#  the Free Software Foundation; either version 2 of the License, or
#  (at your option) any later version.
#
#  This program is distributed in the hope that it will be useful,
#  but WITHOUT ANY WARRANTY; without even the implied warranty of
#  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
#  GNU General Public License for more details.
#
#  You should have received a copy of the GNU General Public License
#  along with this program; if not, write to the Free Software
#  Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston,
#  MA 02110-1301, USA.
#
#  Author: Mauro Soria

import ast
import os
import shutil
import tempfile
from pathlib import Path

import setuptools


ROOT = Path(__file__).resolve().parent

# Preserve the historical packaged layout until the repository is reorganized.
# The built distribution expects a top-level "dirsearch" package that contains
# the entry module, config.ini, db/, and the lib/ package tree.
env_dir = tempfile.mkdtemp(prefix="dirsearch-install-")
package_root = Path(env_dir, "dirsearch")
shutil.copytree(
    ROOT,
    package_root,
    ignore=shutil.ignore_patterns(
        ".git",
        ".github",
        ".cache",
        ".venv",
        "build",
        "dist",
        "target",
        "__pycache__",
        "*.pyc",
        "*.pyo",
        "*.pyd",
        "tests",
        "sessions",
    ),
)

os.chdir(env_dir)


def package_files(directory: Path) -> list[str]:
    files: list[str] = []
    for path in sorted(directory.rglob("*")):
        if path.is_file():
            files.append(str(path.relative_to(package_root)))
    return files


def read_version(path: Path) -> str:
    module = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    for node in module.body:
        if isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Name) and target.id == "VERSION":
                    value = ast.literal_eval(node.value)
                    if isinstance(value, str):
                        return value
    raise RuntimeError(f"Unable to find VERSION in {path}")


def read_requirements(path: Path) -> list[str]:
    requirements: list[str] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        requirement = line.strip()
        if requirement and not requirement.startswith("#"):
            requirements.append(requirement)
    return requirements


setuptools.setup(
    name="dirsearch",
    description="Advanced web path scanner",
    version=read_version(ROOT / "lib/core/settings.py"),
    python_requires=">=3.11",
    classifiers=[
        "Programming Language :: Python",
        "Environment :: Console",
        "Intended Audience :: Information Technology",
        "License :: OSI Approved :: GNU General Public License v2 (GPLv2)",
        "Operating System :: OS Independent",
        "Topic :: Security",
        "Programming Language :: Python :: 3.11",
        "Programming Language :: Python :: 3.12",
        "Programming Language :: Python :: 3.13",
        "Programming Language :: Python :: 3.14",
    ],
    install_requires=read_requirements(ROOT / "requirements/runtime.txt"),
    extras_require={
        "mysql": ["mysql-connector-python==9.6.0"],
        "postgresql": ["psycopg[binary]==3.3.3"],
        "db": read_requirements(ROOT / "requirements/db.txt"),
    },
    entry_points={
        "console_scripts": [
            "dirsearch=dirsearch.dirsearch:main",
            "dirsearch-build-native=dirsearch.lib.core.native_builder:main",
        ]
    },
    packages=setuptools.find_packages(exclude=("dirsearch.tests", "dirsearch.tests.*")),
    package_data={
        "dirsearch": [
            "config.ini",
            *package_files(package_root / "db"),
            *package_files(package_root / "native"),
        ],
        "dirsearch.lib.report": ["templates/*.html"],
    },
    include_package_data=False,
)
