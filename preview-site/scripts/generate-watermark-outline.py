"""Generate a font-independent SVG outline for the preview watermark.

Requires fontTools and the @fontsource/manrope package used by the site.
The generated outline uses Manrope 800 under the SIL Open Font License.
"""

import json
from pathlib import Path

from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont


root = Path(__file__).resolve().parents[1]
font_path = root / "node_modules/@fontsource/manrope/files/manrope-latin-800-normal.woff"
output_path = root / "server/watermark-outline.mjs"
word = "evpmerch.com"
font = TTFont(font_path)
glyphs = font.getGlyphSet()
cmap = font.getBestCmap()
outline = SVGPathPen(glyphs)
bounds = BoundsPen(glyphs)
cursor = 0

for char in word:
    name = cmap[ord(char)]
    glyph = glyphs[name]
    translation = (1, 0, 0, 1, cursor, 0)
    glyph.draw(TransformPen(outline, translation))
    glyph.draw(TransformPen(bounds, translation))
    cursor += font["hmtx"][name][0] + 40

left, bottom, right, top = bounds.bounds
module = (
    "// Outlines of evpmerch.com from Manrope 800 (SIL Open Font License).\n"
    "// Regenerate with: python scripts/generate-watermark-outline.py\n"
    "export const watermarkOutline = {\n"
    f"  path: {json.dumps(outline.getCommands())},\n"
    f"  left: {left}, bottom: {bottom}, right: {right}, top: {top},\n"
    "};\n"
)
output_path.write_text(module, encoding="utf-8")
print(output_path)
