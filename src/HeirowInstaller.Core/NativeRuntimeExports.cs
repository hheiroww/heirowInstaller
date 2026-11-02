using System.Text;

namespace HeirowInstaller.Core;

// Static single-file hosts export runtime contracts rather than bundling libcoreclr.
// https://github.com/dotnet/runtime/tree/main/src/native/corehost/apphost/static
internal static class NativeRuntimeExports
{
    internal static bool ContainsRuntimeContract(string path)
    {
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        if (stream.Length < 64) return false;
        return reader.ReadUInt32() switch { 0x464c457f => Elf(stream, reader), 0xfeedfacf => Mach(stream, reader), _ => false };
    }

    private static bool Contract(string name) => name is "DotNetRuntimeInfo" or "DotNetRuntimeContractDescriptor" or "coreclr_initialize";
    private static bool Bounds(Stream stream, ulong offset, ulong size) => offset <= (ulong)stream.Length && size <= (ulong)stream.Length - offset;
    private static string Name(Stream stream, BinaryReader reader, ulong offset, ulong available)
    {
        if (!Bounds(stream, offset, available)) return "";
        stream.Position = (long)offset; var name = new StringBuilder();
        for (ulong i = 0; i < Math.Min(available, 128); i++) { var value = reader.ReadByte(); if (value == 0) return name.ToString(); name.Append((char)value); }
        return "";
    }

    private static bool Elf(Stream stream, BinaryReader reader)
    {
        if (reader.ReadByte() != 2 || reader.ReadByte() != 1) return false; // ELF64, little endian
        stream.Position = 40; var sections = reader.ReadUInt64();
        stream.Position = 58; var stride = reader.ReadUInt16(); var count = reader.ReadUInt16();
        if (stride != 64 || count == 0 || !Bounds(stream, sections, (ulong)count * stride)) return false;
        for (var section = 0; section < count; section++)
        {
            var start = sections + (ulong)section * stride;
            stream.Position = (long)start + 4;
            if (reader.ReadUInt32() != 11) continue; // Only the dynamic symbol table grants an export.
            stream.Position = (long)start + 24; var symbols = reader.ReadUInt64(); var length = reader.ReadUInt64(); var link = reader.ReadUInt32();
            stream.Position = (long)start + 56;
            if (reader.ReadUInt64() != 24 || length % 24 != 0 || length / 24 > 1_000_000 || link >= count || !Bounds(stream, symbols, length)) continue;
            stream.Position = (long)(sections + link * stride) + 4;
            if (reader.ReadUInt32() != 3) continue;
            stream.Position = (long)(sections + link * stride) + 24;
            var strings = reader.ReadUInt64(); var stringLength = reader.ReadUInt64();
            if (!Bounds(stream, strings, stringLength)) continue;
            for (ulong index = 0; index < length / 24; index++)
            {
                stream.Position = (long)(symbols + index * 24);
                var name = reader.ReadUInt32(); var info = reader.ReadByte(); var visibility = reader.ReadByte(); var definition = reader.ReadUInt16();
                if (definition == 0 || info >> 4 is not (1 or 2) || (visibility & 3) is not (0 or 3) || name >= stringLength) continue;
                if (Contract(Name(stream, reader, strings + name, stringLength - name))) return true;
            }
        }
        return false;
    }

    private static bool Mach(Stream stream, BinaryReader reader)
    {
        stream.Position = 16; var count = reader.ReadUInt32(); var length = reader.ReadUInt32();
        if (count > 65536 || !Bounds(stream, 32, length)) return false;
        long command = 32;
        for (var index = 0; index < count; index++)
        {
            if (command > 32L + length - 8) return false;
            stream.Position = command; var kind = reader.ReadUInt32(); var size = reader.ReadUInt32();
            if (size < 8 || size > 32L + length - command) return false;
            if (kind == 2 && size >= 24) // LC_SYMTAB, nlist_64
            {
                var symbols = reader.ReadUInt32(); var symbolCount = reader.ReadUInt32(); var strings = reader.ReadUInt32(); var stringLength = reader.ReadUInt32();
                if (symbolCount > 1_000_000 || !Bounds(stream, symbols, (ulong)symbolCount * 16) || !Bounds(stream, strings, stringLength)) return false;
                for (uint symbol = 0; symbol < symbolCount; symbol++)
                {
                    stream.Position = symbols + (long)symbol * 16; var name = reader.ReadUInt32(); var type = reader.ReadByte(); var section = reader.ReadByte();
                    if ((type & 0xe1) != 1 || (type & 0x0e) != 0x0e || section == 0 || name >= stringLength) continue;
                    if (Contract(Name(stream, reader, (ulong)strings + name, stringLength - name).TrimStart('_'))) return true;
                }
            }
            command += size;
        }
        return false;
    }
}
