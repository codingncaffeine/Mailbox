#!/usr/bin/env python3
"""Derive the icon metadata JSON generate-icons.py consumes from a bundled TTF itself.

The upstream repository's metadata files describe whatever the font is *today*, and a map
generated from them once pointed three names at codepoints the bundled font does not have —
the buttons drew blank, sized and positioned, in every theme. The bundled file is the truth,
so the metadata comes out of its own cmap and post tables:

    python3 tools/font-metadata.py assets/fonts/FluentSystemIcons-Regular.ttf regular.json
    python3 tools/font-metadata.py assets/fonts/FluentSystemIcons-Filled.ttf filled.json
    python3 tools/generate-icons.py regular.json filled.json
"""

import json
import struct
import sys


def tables(data):
    count = struct.unpack(">H", data[4:6])[0]
    directory = {}
    for i in range(count):
        offset = 12 + 16 * i
        tag = data[offset:offset + 4].decode("ascii")
        table_offset, table_length = struct.unpack(">II", data[offset + 8:offset + 16])
        directory[tag] = (table_offset, table_length)
    return directory


def cmap_map(data, offset):
    """Codepoint -> glyph id, preferring the format 12 subtable (the full repertoire)."""
    subtable_count = struct.unpack(">H", data[offset + 2:offset + 4])[0]
    best = None
    for i in range(subtable_count):
        _, _, suboffset = struct.unpack(">HHI", data[offset + 4 + 8 * i:offset + 12 + 8 * i])
        fmt = struct.unpack(">H", data[offset + suboffset:offset + suboffset + 2])[0]
        if fmt == 12:
            best = offset + suboffset
            break
        if fmt == 4 and best is None:
            best = offset + suboffset

    mapping = {}
    fmt = struct.unpack(">H", data[best:best + 2])[0]
    if fmt == 12:
        groups = struct.unpack(">I", data[best + 12:best + 16])[0]
        for g in range(groups):
            start, end, glyph = struct.unpack(">III", data[best + 16 + 12 * g:best + 28 + 12 * g])
            for code in range(start, end + 1):
                mapping[code] = glyph + (code - start)
        return mapping

    seg_x2 = struct.unpack(">H", data[best + 6:best + 8])[0]
    segments = seg_x2 // 2
    ends = struct.unpack(f">{segments}H", data[best + 14:best + 14 + seg_x2])
    starts = struct.unpack(f">{segments}H", data[best + 16 + seg_x2:best + 16 + 2 * seg_x2])
    deltas = struct.unpack(f">{segments}h", data[best + 16 + 2 * seg_x2:best + 16 + 3 * seg_x2])
    range_base = best + 16 + 3 * seg_x2
    ranges = struct.unpack(f">{segments}H", data[range_base:range_base + seg_x2])
    for i in range(segments):
        for code in range(starts[i], ends[i] + 1):
            if code == 0xFFFF:
                continue
            if ranges[i] == 0:
                mapping[code] = (code + deltas[i]) & 0xFFFF
            else:
                address = range_base + 2 * i + ranges[i] + 2 * (code - starts[i])
                glyph = struct.unpack(">H", data[address:address + 2])[0]
                if glyph:
                    mapping[code] = (glyph + deltas[i]) & 0xFFFF
    return mapping


def post_names(data, offset, length):
    """Glyph id -> name, from a version 2.0 post table."""
    version = struct.unpack(">I", data[offset:offset + 4])[0]
    if version != 0x20000:
        return {}
    count = struct.unpack(">H", data[offset + 32:offset + 34])[0]
    indices = struct.unpack(f">{count}H", data[offset + 34:offset + 34 + 2 * count])
    strings = []
    position = offset + 34 + 2 * count
    end = offset + length
    while position < end:
        strlen = data[position]
        strings.append(data[position + 1:position + 1 + strlen].decode("latin1"))
        position += 1 + strlen
    names = {}
    for glyph, index in enumerate(indices):
        if index >= 258:
            key = index - 258
            names[glyph] = strings[key] if key < len(strings) else ""
    return names


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2

    data = open(sys.argv[1], "rb").read()
    directory = tables(data)
    codepoints = cmap_map(data, directory["cmap"][0])
    names = post_names(data, *directory["post"])

    metadata = {}
    for code, glyph in codepoints.items():
        name = names.get(glyph, "")
        if name and name not in metadata:
            metadata[name] = code

    with open(sys.argv[2], "w") as out:
        json.dump(metadata, out)
    print(f"{len(metadata)} glyphs -> {sys.argv[2]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
