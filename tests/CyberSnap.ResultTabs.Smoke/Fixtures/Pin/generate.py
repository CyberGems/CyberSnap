"""Regenerate the small, synthetic Pin format fixtures (requires Pillow)."""
from pathlib import Path
import struct
from PIL import Image

ROOT = Path(__file__).resolve().parent
SIZE = 32
COLORS = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255)]


def quadrant(x, y):
    return (y // 16) * 2 + x // 16


def write_tiff(name, bits, cmyk=False, alpha=False):
    """Write a baseline, little-endian, uncompressed TIFF without external codecs."""
    samples = 4 if cmyk or alpha else 3
    tags = [
        (256, 4, 1, SIZE), (257, 4, 1, SIZE),
        (258, 3, samples, None), (259, 3, 1, 1),
        (262, 3, 1, 5 if cmyk else 2), (273, 4, 1, None),
        (277, 3, 1, samples), (278, 4, 1, SIZE),
        (279, 4, 1, SIZE * SIZE * samples * (bits // 8)), (284, 3, 1, 1),
    ]
    if cmyk:
        tags.append((332, 3, 1, 1))  # CMYK ink set.
    if alpha:
        tags.append((338, 3, 1, 2))  # Unassociated (straight) alpha.
    tags.sort()
    bits_offset = 8 + 2 + len(tags) * 12 + 4
    pixels_offset = bits_offset + samples * 2
    data = bytearray(b'II' + struct.pack('<HIH', 42, 8, len(tags)))
    for tag, kind, count, value in tags:
        value = bits_offset if tag == 258 else pixels_offset if tag == 273 else value
        data += struct.pack('<HHII', tag, kind, count, value)
    data += struct.pack('<I', 0) + struct.pack('<' + 'H' * samples, *([bits] * samples))
    for y in range(SIZE):
        for x in range(SIZE):
            index = quadrant(x, y)
            channels = COLORS[index]
            if cmyk:
                channels = tuple(255 - value for value in channels) + (0,)
            elif bits == 16:
                channels = tuple(value * 257 for value in channels)
                if alpha:
                    channels += (32768 if index == 0 else 65535,)
            data += struct.pack('<' + ('H' if bits == 16 else 'B') * samples, *channels)
    (ROOT / name).write_bytes(data)


rgb = Image.new('RGB', (SIZE, SIZE))
rgb.putdata([COLORS[quadrant(x, y)] for y in range(SIZE) for x in range(SIZE)])
rgb.save(ROOT / 'rgb24.bmp')
indexed = Image.new('P', (SIZE, SIZE))
indexed.putpalette([v for color in COLORS for v in color] + [0] * (768 - 12))
indexed.putdata([quadrant(x, y) for y in range(SIZE) for x in range(SIZE)])
indexed.save(ROOT / 'indexed8.bmp')
indexed.save(ROOT / 'indexed.gif')
second_frame = Image.new('RGB', (SIZE, SIZE), (0, 0, 0))
rgb.save(ROOT / 'animated.gif', save_all=True, append_images=[second_frame],
         duration=[100, 100], loop=0, disposal=2, optimize=False)
rgb.convert('CMYK').save(ROOT / 'cmyk.jpg', quality=100, subsampling=0)
write_tiff('rgb48.tiff', 16)
write_tiff('rgba64.tiff', 16, alpha=True)
write_tiff('cmyk.tiff', 8, cmyk=True)
print('Regenerated eight synthetic Pin fixtures.')
