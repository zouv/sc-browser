# SC-Browser icon

Hand-drawn SVG: an icy-blue **S** and a turquoise-to-blue **C** monogram laid over a glowing blue
**globe** (ocean gradient, faint graticule, abstract continents, terminator shadow and specular
highlight), wrapped by a tilted orbital ring with a small satellite. Everything outside the sphere
is transparent, so the mark reads as a round planet on dark and light backgrounds alike.

## Files

- `sc-browser.svg`: editable vector source; 512 x 512 viewBox; no fonts, JavaScript, or external dependencies.
- `sc-browser-1024.png`: large transparent PNG for previews and reuse.
- `sc-browser-{512,256,128,64,48,32,24,16}.png`: application and favicon exports.
- `sc-browser.ico`: Windows icon containing 16, 24, 32, 48, 64, 128, and 256 px frames.

## Layers

- `atmosphere`: soft cyan halo just outside the sphere.
- `globe-clip` group: ocean gradient, graticule, continents, terminator shadow and specular highlight.
- `monogram`: custom Bezier letterforms (no fonts), with a dark under-stroke for contrast on the globe.
- `orbit`: tilted ring + satellite dot.

## Regenerating the exports

SVG is the source of truth. Rasterize with any SVG renderer (tested with `@resvg/resvg-js`),
then rebuild the ICO from the 256 px render (frames 16/24/32/48/64/128/256).
Keep the SVG free of external resources so this stays reproducible.
