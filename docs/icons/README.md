# ScBrowser icon

A pale **S** sits inside a mint-to-blue **C**. The open C is the orbital trail; its
angled tips suggest navigation without a separate ring, globe grid, or satellite.
A thin rim keeps the midnight-blue disc distinct on both light and dark surfaces.
The open space between the glyphs is intentional: check it at 16, 24, and 32 px.

## Files

- `sc-browser.svg`: editable 512 × 512 vector master; no fonts, filters, or external assets.
- `sc-browser-{16,24,32,48,64,128,256,512,1024}.png`: transparent raster exports.
- `sc-browser.ico`: 16, 24, 32, 48, 64, 128, and 256 px PNG frames for Windows.

The desktop app, internal tab favicons, and the installer use `src/ScBrowser/Assets/sc-browser.ico`.
The new-tab and About pages use `src/ScBrowser/Assets/ui/common/sc-browser.svg`.
Both are copies of the corresponding files here; regenerate and update them together.

## Regenerate

Run the following Python with CairoSVG and Pillow from the repository root. These are only
export tools, not app dependencies. Small icons are supersampled, and the ICO embeds the
same PNGs rather than resizing them again.

```python
from io import BytesIO
from pathlib import Path
from shutil import copyfile
from struct import pack

from cairosvg import svg2png
from PIL import Image

icons = Path("docs/icons")
assets = Path("src/ScBrowser/Assets")
source = icons / "sc-browser.svg"
ico = icons / "sc-browser.ico"
sizes = (16, 24, 32, 48, 64, 128, 256)

for size in (*sizes, 512, 1024):
    render_size = 4 * size if size <= 128 else size
    png = svg2png(url=str(source), output_width=render_size, output_height=render_size)
    with Image.open(BytesIO(png)) as rendered:
        rendered.convert("RGBA").resize((size, size), Image.Resampling.LANCZOS).save(
            icons / f"sc-browser-{size}.png", optimize=True
        )

frames = [(size, (icons / f"sc-browser-{size}.png").read_bytes()) for size in sizes]
offset = 6 + 16 * len(frames)
with ico.open("wb") as output:
    output.write(pack("<HHH", 0, 1, len(frames)))
    for size, data in frames:
        output.write(pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
    for _, data in frames:
        output.write(data)

copyfile(ico, assets / ico.name)
copyfile(source, assets / "ui/common" / source.name)
```
