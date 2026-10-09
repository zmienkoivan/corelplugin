"""Package the Vanya Tools ICO as a Win32 .res for CorelDRAW's config.xml."""

from pathlib import Path
import struct


ROOT = Path(__file__).resolve().parents[1]
ICON = ROOT / "addon" / "VanyaToolsNative" / "VanyaToolsIcon.ico"
OUTPUT = ROOT / "native" / "VanyaTools.Native" / "Assets" / "VanyaToolsIcon.res"


def resource(type_id: int, name_id: int, data: bytes) -> bytes:
    header = struct.pack("<IIHHHHIHHII", len(data), 32, 0xFFFF, type_id,
                         0xFFFF, name_id, 0, 0x1030, 0, 0, 0)
    return header + data + bytes((-len(data)) % 4)


def main() -> None:
    icon = ICON.read_bytes()
    reserved, kind, count = struct.unpack_from("<HHH", icon)
    if (reserved, kind, count) != (0, 1, 1):
        raise ValueError("Expected one image in VanyaToolsIcon.ico")

    width, height, colors, reserved, planes, bits, size, offset = struct.unpack_from(
        "<BBBBHHII", icon, 6)
    image = icon[offset:offset + size]
    if len(image) != size:
        raise ValueError("Incomplete icon image")

    group = struct.pack("<HHH", 0, 1, 1) + struct.pack(
        "<BBBBHHIH", width, height, colors, reserved, planes, bits, size, 109)

    # String resource 10223 is the last entry in block 639.
    strings = bytes(30) + struct.pack("<H", len("Vanya Tools")) + "Vanya Tools".encode("utf-16le")
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_bytes(resource(0, 0, b"") + resource(3, 109, image) +
                       resource(14, 108, group) + resource(6, 639, strings))


if __name__ == "__main__":
    main()
