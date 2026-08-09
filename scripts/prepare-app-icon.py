"""Create the checked-in PNG and multi-resolution ICO from a transparent source."""

from pathlib import Path
from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "Hackermes.App" / "Assets"
SOURCE = ASSETS / "hackermes-icon.png"


def main() -> None:
    source = Image.open(SOURCE).convert("RGBA")
    bounds = source.getchannel("A").getbbox()
    if bounds is None:
        raise RuntimeError("icon has no visible pixels")

    subject = source.crop(bounds)
    side = max(subject.size)
    padding = int(side * 0.08)
    canvas_side = side + padding * 2
    canvas = Image.new("RGBA", (canvas_side, canvas_side), (0, 0, 0, 0))
    canvas.alpha_composite(
        subject,
        ((canvas_side - subject.width) // 2, (canvas_side - subject.height) // 2),
    )
    master = canvas.resize((512, 512), Image.Resampling.LANCZOS)
    master.save(SOURCE, optimize=True)
    master.save(
        ASSETS / "hackermes.ico",
        format="ICO",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )


if __name__ == "__main__":
    main()
