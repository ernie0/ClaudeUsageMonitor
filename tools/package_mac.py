"""Wraps a `dotnet publish -r osx-*` folder into ClaudeUsageMonitor.app and zips it.

Usage: package_mac.py <publish-dir> <arch: arm64|x64> <version> <out-dir> <repo-root>

The zip keeps Unix permissions (the program inside must stay executable after unzipping on a
Mac), and the .app gets an Info.plist (menu-bar app: LSUIElement) and an .icns icon drawn by
make_icon.py. Prints the SHA-256 and checks the Mach-O header of the program.
"""
import hashlib
import struct
import sys
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from make_icon import render  # noqa: E402

APP = "ClaudeUsageMonitor"
INFO_PLIST = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>zh_TW</string>
  <key>CFBundleDisplayName</key><string>Claude 額度小幫手</string>
  <key>CFBundleExecutable</key><string>{app}</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundleIdentifier</key><string>com.ernie0.claudeusagemonitor</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>{app}</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>{version}</string>
  <key>CFBundleVersion</key><string>{version}</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Copyright (c) 2026 ernie0</string>
</dict>
</plist>
"""

# icns entry types for PNG payloads, by pixel size.
ICNS_TYPES = [(b"icp4", 16), (b"icp5", 32), (b"ic11", 32), (b"ic12", 64), (b"ic07", 128),
              (b"ic08", 256), (b"ic13", 256), (b"ic09", 512), (b"ic14", 512)]


def icns() -> bytes:
    cache = {}
    body = b""
    for kind, size in ICNS_TYPES:
        png = cache.setdefault(size, render(size))
        body += kind + struct.pack(">I", len(png) + 8) + png
    return b"icns" + struct.pack(">I", len(body) + 8) + body


def macho_report(path: Path) -> str:
    data = path.read_bytes()
    magic, cputype, _, _, ncmds, _, _ = struct.unpack_from("<IiiIIII", data, 0)
    if magic != 0xFEEDFACF:
        return f"not a 64-bit Mach-O (magic {magic:#x})"
    arch = {0x0100000C: "arm64", 0x01000007: "x86_64"}.get(cputype, hex(cputype))
    offset, signed = 32, False
    for _ in range(ncmds):
        cmd, size = struct.unpack_from("<II", data, offset)
        if cmd == 0x1D:  # LC_CODE_SIGNATURE
            signed = True
        offset += size
    return f"Mach-O {arch}, {'has' if signed else 'NO'} code signature (ad-hoc)"


def main(publish: Path, arch: str, version: str, out: Path, repo: Path) -> None:
    out.mkdir(parents=True, exist_ok=True)
    zip_path = out / f"{APP}-v{version}-macos-{arch}.zip"
    prefix = f"{APP}.app/Contents/"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        def add(name: str, data: bytes, mode: int) -> None:
            info = zipfile.ZipInfo(name)
            info.create_system = 3  # Unix, so the permission bits below are honoured
            info.external_attr = (0o100000 | mode) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)

        add(prefix + "Info.plist", INFO_PLIST.format(app=APP, version=version).encode("utf-8"), 0o644)
        add(prefix + "Resources/AppIcon.icns", icns(), 0o644)
        for file in sorted(publish.rglob("*")):
            if file.is_dir() or file.suffix == ".pdb":
                continue
            rel = file.relative_to(publish).as_posix()
            executable = rel == APP or rel == "createdump"
            add(prefix + "MacOS/" + rel, file.read_bytes(), 0o755 if executable else 0o644)
        for doc in ("README.md", "THIRD_PARTY_NOTICES.md"):
            add(doc, (repo / doc).read_bytes(), 0o644)
        for shot in sorted((repo / "docs" / "screenshots").glob("*.png")):
            add("docs/screenshots/" + shot.name, shot.read_bytes(), 0o644)

    digest = hashlib.sha256(zip_path.read_bytes()).hexdigest().upper()
    (out / (zip_path.name + ".sha256")).write_text(f"{digest}  {zip_path.name}\n", encoding="ascii")
    print(f"{zip_path.name}: {zip_path.stat().st_size / 1e6:.1f} MB  SHA-256 {digest}")
    print("  program:", macho_report(publish / APP))


if __name__ == "__main__":
    main(Path(sys.argv[1]), sys.argv[2], sys.argv[3], Path(sys.argv[4]), Path(sys.argv[5]))
