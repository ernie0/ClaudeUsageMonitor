"""Generates src/Assets/app.ico (PNG-compressed entries) without third-party packages.

The mark is a rounded tile in the coral-to-violet brand gradient with a white quota ring
(three quarters filled) around a white four-pointed "AI" sparkle.
"""
import math
import struct
import sys
import zlib
from pathlib import Path

CORAL = (0xE8, 0x82, 0x5F)
VIOLET = (0x8B, 0x5C, 0xF6)
WHITE = (0xFF, 0xFF, 0xFF)
TRACK = (0xFF, 0xFF, 0xFF, 0.32)
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
SAMPLES = 4


def mix(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


def shade(x: float, y: float):
    """(alpha, rgb) of a point in unit coordinates, or None outside the tile."""
    radius = 0.24
    cx = min(max(x, radius), 1 - radius)
    cy = min(max(y, radius), 1 - radius)
    if (x - cx) ** 2 + (y - cy) ** 2 > radius ** 2:
        return None
    base = mix(CORAL, VIOLET, min(1.0, max(0.0, (x + y) / 2)))
    dx, dy = x - 0.5, y - 0.5
    distance = math.hypot(dx, dy)
    # Sparkle: an astroid |x|^p + |y|^p <= r^p gives a four-pointed star.
    p = 0.62
    if abs(dx) ** p + abs(dy) ** p <= 0.2 ** p:
        return (1.0, WHITE)
    if 0.29 <= distance <= 0.37:
        angle = (math.degrees(math.atan2(dx, -dy)) + 360) % 360
        if angle <= 270:
            return (1.0, WHITE)
        alpha = TRACK[3]
        return (1.0, mix(base, WHITE, alpha))
    return (1.0, base)


def render(size: int) -> bytes:
    rows = []
    for py in range(size):
        row = bytearray([0])
        for px in range(size):
            r = g = b = a = 0.0
            for sy in range(SAMPLES):
                for sx in range(SAMPLES):
                    hit = shade((px + (sx + 0.5) / SAMPLES) / size, (py + (sy + 0.5) / SAMPLES) / size)
                    if hit:
                        alpha, (cr, cg, cb) = hit
                        r, g, b, a = r + cr * alpha, g + cg * alpha, b + cb * alpha, a + alpha
            n = SAMPLES * SAMPLES
            if a > 0:
                row += bytes((round(r / a), round(g / a), round(b / a), round(255 * a / n)))
            else:
                row += bytes(4)
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main(out: Path) -> None:
    images = [render(s) for s in SIZES]
    directory = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for size, png in zip(SIZES, images):
        directory += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(directory + b"".join(images))
    print(f"wrote {out} ({out.stat().st_size} bytes, sizes {SIZES})")


if __name__ == "__main__":
    main(Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[1] / "src" / "Assets" / "app.ico")
