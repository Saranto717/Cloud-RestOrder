from pathlib import Path
import hashlib
import sys
import zipfile

source = Path(__file__).resolve().parent.parent
version = sys.argv[1] if len(sys.argv) > 1 else "17.3.113"
output = source.parent
archive = output / f"RestaurantOrders-Cloud-{version}.zip"
excluded_dirs = {"bin", "obj", ".git", "secrets", "node_modules"}
excluded_suffixes = {".db", ".sqlite", ".sqlite3", ".log", ".pem", ".key", ".pfx"}
included_roots = (
    source / "cloud" / "RestaurantOrders.Cloud.Api",
    source / "deploy" / "cloud",
)

with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
    for included_root in included_roots:
        if not included_root.is_dir():
            raise FileNotFoundError(f"Required deployment directory not found: {included_root}")
        for path in included_root.rglob("*"):
            if not path.is_file():
                continue
            relative = path.relative_to(source)
            if any(part in excluded_dirs for part in relative.parts):
                continue
            if path.suffix.lower() in excluded_suffixes:
                continue
            zf.write(path, Path(f"RestaurantOrders-Cloud-{version}") / relative)

digest = hashlib.sha256(archive.read_bytes()).hexdigest()
archive.with_suffix(archive.suffix + ".sha256").write_text(
    f"{digest}  {archive.name}\n", encoding="ascii"
)
print(archive)
print(f"{archive.stat().st_size} bytes; SHA-256 {digest}")
