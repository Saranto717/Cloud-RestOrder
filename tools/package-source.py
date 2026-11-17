from pathlib import Path
import hashlib
import sys
import zipfile

source = Path(__file__).resolve().parent.parent
version = sys.argv[1] if len(sys.argv) > 1 else "17.3.122"
output = source.parent
output.mkdir(parents=True, exist_ok=True)
name = f"RestaurantOrders-{version}-source.zip"
archive = output / name
excluded_dirs = {"bin", "obj", "node_modules", ".git", ".gradle", "build", "dist", "artifacts", "secrets"}
excluded_suffixes = {".db", ".sqlite", ".sqlite3", ".log", ".license", ".pdb", ".pem", ".key", ".pfx"}

with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
    for path in source.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(source)
        if any(part in excluded_dirs for part in relative.parts):
            continue
        if path.suffix.lower() in excluded_suffixes:
            continue
        zf.write(path, Path(f"RestaurantOrders-{version}-source") / relative)

digest = hashlib.sha256(archive.read_bytes()).hexdigest()
archive.with_suffix(archive.suffix + ".sha256").write_text(
    f"{digest}  {name}\n", encoding="ascii"
)
print(archive)
print(f"{archive.stat().st_size} bytes; SHA-256 {digest}")
