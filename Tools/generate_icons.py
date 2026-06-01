#!/usr/bin/env python3

from __future__ import annotations

import math
import shutil
from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
RESOURCES = ROOT / "Resources"
ICONSET = RESOURCES / "AppIcon.iconset"
APP_ICON = RESOURCES / "AppIcon.icns"
MENU_ICON = RESOURCES / "MenuBarIcon.png"
SOURCE_ICON = RESOURCES / "AppIconSource.png"


def draw_starburst(
    draw: ImageDraw.ImageDraw,
    center: tuple[float, float],
    inner_radius: float,
    outer_radius: float,
    spokes: int,
    color: tuple[int, int, int, int],
    width: int,
) -> None:
    cx, cy = center
    for index in range(spokes):
        angle = math.tau * index / spokes
        x0 = cx + math.cos(angle) * inner_radius
        y0 = cy + math.sin(angle) * inner_radius
        x1 = cx + math.cos(angle) * outer_radius
        y1 = cy + math.sin(angle) * outer_radius
        draw.line((x0, y0, x1, y1), fill=color, width=width)


def build_menu_icon(size: int = 36) -> None:
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    draw.rounded_rectangle(
        (
            round(size * 0.08),
            round(size * 0.18),
            round(size * 0.22),
            round(size * 0.82),
        ),
        radius=round(size * 0.10),
        fill=(0, 0, 0, 255),
    )
    draw_starburst(
        draw,
        center=(round(size * 0.46), round(size * 0.50)),
        inner_radius=round(size * 0.05),
        outer_radius=round(size * 0.18),
        spokes=10,
        color=(0, 0, 0, 255),
        width=max(1, round(size * 0.08)),
    )
    draw.arc(
        (
            round(size * 0.56),
            round(size * 0.52),
            round(size * 0.92),
            round(size * 0.88),
        ),
        start=132,
        end=312,
        fill=(0, 0, 0, 255),
        width=max(2, round(size * 0.08)),
    )
    image.save(MENU_ICON)


def save_iconset() -> None:
    if not SOURCE_ICON.exists():
        raise FileNotFoundError(f"Missing icon source: {SOURCE_ICON}")

    if ICONSET.exists():
        shutil.rmtree(ICONSET)
    ICONSET.mkdir(parents=True)

    size_map = {
        "icon_16x16.png": 16,
        "icon_16x16@2x.png": 32,
        "icon_32x32.png": 32,
        "icon_32x32@2x.png": 64,
        "icon_128x128.png": 128,
        "icon_128x128@2x.png": 256,
        "icon_256x256.png": 256,
        "icon_256x256@2x.png": 512,
        "icon_512x512.png": 512,
        "icon_512x512@2x.png": 1024,
    }

    with Image.open(SOURCE_ICON) as source:
        source = source.convert("RGBA")
        for filename, size in size_map.items():
            resized = source.resize((size, size), Image.Resampling.LANCZOS)
            resized.save(ICONSET / filename)


def build_icns() -> None:
    with Image.open(ICONSET / "icon_512x512@2x.png") as image:
        image.save(APP_ICON)


def main() -> None:
    save_iconset()
    build_icns()
    build_menu_icon()
    print(f"Generated {APP_ICON.relative_to(ROOT)} and {MENU_ICON.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
