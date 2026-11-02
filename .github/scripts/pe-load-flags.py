"""Checks a published acb.exe: the loader must resolve the DLLs it imports from System32 only, never from the
folder acb sits in, so a DLL placed beside it cannot run in the administrator's run. Reads the PE load
configuration's DependentLoadFlags and lists the imported DLLs. Read-only; exits 1 when the flag is missing.

    python .github/scripts/pe-load-flags.py artifacts/publish/CS2OpenDev.ConfigManager.Cli/release_win-x64/acb.exe
"""

import struct
import sys

SEARCH_SYSTEM32 = 0x800


def main(path):
    data = open(path, "rb").read()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        sys.exit(path + " is not a PE file")
    section_count, = struct.unpack_from("<H", data, pe + 6)
    optional_size, = struct.unpack_from("<H", data, pe + 20)
    optional = pe + 24
    if struct.unpack_from("<H", data, optional)[0] != 0x20B:
        sys.exit(path + " is not a 64-bit image")
    sections = optional + optional_size

    def offset(rva):
        for i in range(section_count):
            size, address, _, raw = struct.unpack_from("<IIII", data, sections + 40 * i + 8)
            if address <= rva < address + size:
                return raw + rva - address
        sys.exit("RVA 0x%X is in no section" % rva)

    def directory(index):
        # PE32+ data directories start 112 bytes into the optional header, 8 bytes each.
        return struct.unpack_from("<II", data, optional + 112 + 8 * index)

    imports = []
    import_rva, _ = directory(1)
    if import_rva:
        at = offset(import_rva)
        while struct.unpack_from("<I", data, at + 12)[0]:
            name = offset(struct.unpack_from("<I", data, at + 12)[0])
            imports.append(data[name:data.index(b"\0", name)].decode("ascii"))
            at += 20

    config_rva, _ = directory(10)
    if not config_rva:
        sys.exit(path + " has no load configuration")
    config = offset(config_rva)
    # IMAGE_LOAD_CONFIG_DIRECTORY64: DependentLoadFlags is the WORD at offset 78, there when Size reaches past it.
    declared, = struct.unpack_from("<I", data, config)
    flags = struct.unpack_from("<H", data, config + 78)[0] if declared >= 80 else 0

    print("imports: " + ", ".join(imports))
    print("DependentLoadFlags: 0x%X" % flags)
    if not flags & SEARCH_SYSTEM32:
        print("acb.exe would load the DLLs it imports from its own folder first (LOAD_LIBRARY_SEARCH_SYSTEM32 is not set).")
        sys.exit(1)


if __name__ == "__main__":
    main(sys.argv[1])
