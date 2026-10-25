"""Patch only the independent test fixture's import names to Winsock ordinals."""
from pathlib import Path
import argparse
import struct
import pefile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("source", type=Path, help="compiled independent native test fixture")
parser.add_argument("destination", type=Path, help="ordinal-import test fixture output")
args = parser.parse_args()
source = args.source.resolve()
destination = args.destination.resolve()
if source == destination:
    parser.error("source and destination must differ")
pe = pefile.PE(str(source))
data = bytearray(source.read_bytes())
changed = []
for descriptor in pe.DIRECTORY_ENTRY_IMPORT:
    if descriptor.dll.lower() != b"ws2_32.dll":
        continue
    for index, item in enumerate(descriptor.imports):
        ordinal = {b"connect": 4, b"closesocket": 3, b"ioctlsocket": 10, b"WSAGetLastError": 111}.get(item.name)
        if ordinal:
            offset = pe.get_offset_from_rva(descriptor.struct.OriginalFirstThunk + index * 4)
            struct.pack_into("<I", data, offset, 0x80000000 | ordinal)
            changed.append(ordinal)
assert sorted(changed) == [3, 4, 10, 111], changed
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_bytes(data)
check = pefile.PE(str(destination))
found = [i.ordinal for d in check.DIRECTORY_ENTRY_IMPORT if d.dll.lower() == b"ws2_32.dll"
         for i in d.imports if i.import_by_ordinal]
assert all(i in found for i in [3, 4, 10, 111])
print("PASS test-only PE imports use closesocket=3 connect=4 ioctlsocket=10 WSAGetLastError=111:", destination)
