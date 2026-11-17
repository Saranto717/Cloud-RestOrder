from __future__ import annotations

import hashlib
import shutil
import zipfile
from pathlib import Path


VERSION = "17.3.70"
SOURCE = Path(__file__).resolve().parent.parent
OUTPUT = Path(r"E:\CodexBuild")
ROOT = f"RestaurantOrders-{VERSION}-source"
EXCLUDED_DIRS = {".git", ".vs", "bin", "obj", "node_modules", "artifacts", "build", "dist"}
EXCLUDED_NAMES = {"appsettings.Local.json"}
EXCLUDED_SUFFIXES = {".db", ".sqlite", ".sqlite3", ".log", ".pdb"}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_hash(path: Path) -> None:
    path.with_suffix(path.suffix + ".sha256").write_text(
        f"{sha256(path)}  {path.name}\n", encoding="ascii"
    )


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    source_zip = OUTPUT / f"{ROOT}.zip"
    with zipfile.ZipFile(source_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in SOURCE.rglob("*"):
            if not path.is_file():
                continue
            relative = path.relative_to(SOURCE)
            if any(part in EXCLUDED_DIRS for part in relative.parts):
                continue
            if path.name in EXCLUDED_NAMES or path.suffix.lower() in EXCLUDED_SUFFIXES:
                continue
            archive.write(path, Path(ROOT) / relative)
    write_hash(source_zip)

    setup_source = SOURCE / "artifacts" / "installer" / f"RestaurantOrders-Setup-{VERSION}-win-x64.exe"
    setup = OUTPUT / setup_source.name
    shutil.copy2(setup_source, setup)
    write_hash(setup)
    print(source_zip)
    print(setup)


if __name__ == "__main__":
    main()
