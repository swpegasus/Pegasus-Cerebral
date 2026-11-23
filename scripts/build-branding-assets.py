#!/usr/bin/env python3
"""Package the approved Cerebral appmark into PNG and Windows ICO resources."""

from __future__ import annotations

import argparse
import io
import math
import shutil
import struct
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


PNG_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256, 512, 1024)
ICO_SIZES = tuple((size, size) for size in PNG_SIZES if size <= 256)
# Stable executable/resource filenames retain the product base with a small role badge.
ROLES = ("orchestrator", "integrator", "worker", "setup")
APPMARK_FILENAME = "cerebral-appmark-master.png"


def render_role(master: Image.Image, role: str, size: int) -> Image.Image:
    factor = 4 if size <= 256 else 1
    frame = master.resize((size * factor, size * factor), Image.Resampling.LANCZOS)
    if role != "orchestrator":
        draw = ImageDraw.Draw(frame)
        scale = frame.width / 64
        ink = "#0B234A"
        width = max(1, round((4.0 if size <= 20 else 3.2) * scale))

        def bounds(x: float, y: float, radius: float) -> tuple[float, ...]:
            return tuple(value * scale for value in (x - radius, y - radius, x + radius, y + radius))

        def line(points: tuple[tuple[float, float], ...]) -> None:
            scaled = [(round(x * scale), round(y * scale)) for x, y in points]
            draw.line(scaled, fill=ink, width=width, joint="curve")
            # Round endpoints and corners consistently with the suite's vector badges.
            for x, y in scaled:
                half = width / 2
                draw.ellipse((x - half, y - half, x + half, y + half), fill=ink)

        draw.ellipse(bounds(49, 49, 14), fill="#F8FAFF", outline="#245BCE", width=max(1, round(2 * scale)))
        if role == "integrator":
            line(((41, 56), (52, 45)))
            line(((50, 39), (50, 45), (56, 45), (56, 39)))
        elif role == "worker":
            draw.ellipse(bounds(49, 49, 6), outline=ink, width=width)
            for index in range(8):
                angle = index * math.pi / 4
                line(tuple((49 + radius * math.cos(angle), 49 + radius * math.sin(angle)) for radius in (7, 10)))
        elif role == "setup":
            line(((49, 39), (49, 51)))
            line(((44, 47), (49, 52), (54, 47)))
            line(((40, 52), (40, 57), (58, 57), (58, 52)))
        else:
            raise ValueError(f"Unknown Cerebral role: {role}")
    return frame if factor == 1 else frame.resize((size, size), Image.Resampling.LANCZOS)


def write_ico(destination: Path, frames: dict[int, Image.Image]) -> None:
    # Store each optically corrected small frame, rather than resizing one large badge into every slot.
    payloads = []
    for size, _ in ICO_SIZES:
        stream = io.BytesIO()
        frames[size].save(stream, format="PNG", optimize=True)
        payloads.append((size, stream.getvalue()))
    offset = 6 + 16 * len(payloads)
    directory = bytearray(struct.pack("<HHH", 0, 1, len(payloads)))
    for size, payload in payloads:
        dimension = 0 if size == 256 else size
        directory.extend(struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(payload), offset))
        offset += len(payload)
    destination.write_bytes(bytes(directory) + b"".join(payload for _, payload in payloads))


def write_role(root: Path, role: str, master: Image.Image, preserve_existing: bool = False) -> None:
    icons = root / "icons"
    role_dir = icons / role
    role_dir.mkdir(parents=True, exist_ok=True)
    frames = {size: render_role(master, role, size) for size in PNG_SIZES}
    try:
        role_master = icons / f"cerebral-{role}-master-1024.png"
        if not preserve_existing or not role_master.exists():
            frames[1024].save(role_master, optimize=True)
        for size, frame in frames.items():
            destination = role_dir / f"cerebral-{role}-{size}.png"
            if not preserve_existing or not destination.exists():
                frame.save(destination, optimize=True)

        ico = root / f"cerebral-{role}.ico"
        if not preserve_existing or not ico.exists():
            write_ico(ico, frames)
        # Desktop resources and ApplicationIcon consume assets/branding; assets
        # also retains a legacy mirror. Keep all newly generated role copies aligned.
        for mirror in (root.parent, root.parent.parent):
            destination = mirror / ico.name
            if not preserve_existing or not destination.exists():
                shutil.copy2(ico, destination)
    finally:
        for frame in frames.values():
            frame.close()


def write_contact_sheet(root: Path) -> Path:
    sheet = Image.new("RGB", (790, 44 + 72 * len(ROLES)), "white")
    draw = ImageDraw.Draw(sheet)
    font = ImageFont.load_default()
    draw.rectangle((490, 0, 790, sheet.height), fill="#101A2D")
    draw.text((12, 12), "Cerebral role", fill="#172B4D", font=font)
    draw.text((204, 12), "16    16 x3       32       32 x2", fill="#172B4D", font=font)
    draw.text((504, 12), "16    16 x3       32       32 x2", fill="white", font=font)
    labels = {"orchestrator": "Main (unchanged)", "integrator": "Configuration / wrench", "worker": "Worker / gear", "setup": "Setup / installation"}
    for row, role in enumerate(ROLES):
        top = 44 + row * 72
        draw.text((12, top + 24), labels[role], fill="#172B4D", font=font)
        for size, offset, zoom in ((16, 0, 1), (16, 38, 3), (32, 108, 1), (32, 164, 2)):
            with Image.open(root / "icons" / role / f"cerebral-{role}-{size}.png") as source:
                frame = source.convert("RGBA")
                preview = frame.resize((size * zoom, size * zoom), Image.Resampling.NEAREST)
                for left in (204, 504):
                    sheet.paste(preview, (left + offset, top + (64 - preview.height) // 2), preview)
                preview.close()
                frame.close()
    destination = root / "cerebral-icon-family-preview.png"
    sheet.save(destination, optimize=True)
    sheet.close()
    return destination


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--preserve-main", action="store_true", help="Keep every existing unbadged Main ICO/PNG byte unchanged.")
    args = parser.parse_args()
    root = args.root.resolve() / "assets" / "branding" / "cerebral"
    master_path = root / APPMARK_FILENAME
    if not master_path.is_file():
        raise SystemExit(f"Approved Cerebral appmark is missing: {master_path}")
    with Image.open(master_path) as master_image:
        if master_image.format != "PNG" or master_image.width != master_image.height or master_image.width < 1024:
            raise SystemExit("Approved Cerebral appmark must be a square PNG at least 1024 pixels wide.")
        master = master_image.convert("RGBA")
        for role in ROLES:
            write_role(root, role, master, preserve_existing=args.preserve_main and role == "orchestrator")
        master.close()
    preview = write_contact_sheet(root)
    # This is packaging only. Poster, splash, installer art and the neutral alpha
    # watermark are independently authored assets and must remain byte-preserved.
    print(f"Cerebral icon family: 4 role masters, 44 PNG sizes, 4 nine-frame ICO files and mirrors; authored base preserved. Preview: {preview}")


if __name__ == "__main__":
    main()
