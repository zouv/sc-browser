# SC-Browser icon

Hand-drawn SVG monogram: an icy-blue S and a turquoise-to-blue C, with a tilted orbital line on a midnight-blue rounded tile.

## Files

- `sc-browser.svg`: editable vector source; 512 x 512 viewBox; no fonts, JavaScript, or external dependencies.
- `sc-browser-1024.png`: large transparent-corner PNG for previews and reuse.
- `sc-browser-{512,256,128,64,48,32,16}.png`: application and favicon exports.
- `sc-browser.ico`: Windows icon containing 16, 24, 32, 48, 64, 128, and 256 px frames.

## Editing

- `background`: rounded dark tile; hide this group for a tile-free mark.
- `orbit`: subtle orbital decoration; hide for a simpler monogram.
- `monogram`: custom Bezier letterforms, independent of installed fonts.
- `s-ink`, `c-ink`, `tile`: editable gradient definitions.

The corners outside the background tile are transparent; the tile itself is opaque.
SVG is the source of truth; re-export the raster files after vector changes.
