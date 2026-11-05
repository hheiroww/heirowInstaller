using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Xml;
using System.Xml.Linq;

namespace HeirowInstaller.Core;

public enum WindowsExecutionLevel { AsInvoker, HighestAvailable, RequireAdministrator }

/// <summary>Reads the PE manifest resource without loading or executing the authored program.</summary>
public static class WindowsExecutablePrivileges
{
    public static WindowsExecutionLevel Read(string path)
    {
        if (!File.Exists(path)) return WindowsExecutionLevel.AsInvoker;
        PackageSafety.EnsureNoReparsePoints(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < 64) return WindowsExecutionLevel.AsInvoker;
        try
        {
            using var pe = new PEReader(file, PEStreamOptions.LeaveOpen);
            var header = pe.PEHeaders.PEHeader;
            if (header is null || header.ResourceTableDirectory.Size == 0) return WindowsExecutionLevel.AsInvoker;
            var directory = header.ResourceTableDirectory;
            if (directory.Size is < 16 or > 4 * 1024 * 1024) throw new InvalidDataException("The executable's resource directory exceeds its supported size.");
            int Offset(int rva)
            {
                foreach (var section in pe.PEHeaders.SectionHeaders)
                    if (rva >= section.VirtualAddress && rva - section.VirtualAddress < section.SizeOfRawData)
                        return checked(section.PointerToRawData + rva - section.VirtualAddress);
                throw new InvalidDataException("An executable manifest resource is outside its PE sections.");
            }
            file.Position = Offset(directory.RelativeVirtualAddress); var resources = new byte[directory.Size]; file.ReadExactly(resources);
            int U16(int offset) { if (offset < 0 || offset + 2 > resources.Length) throw new InvalidDataException("Truncated PE resource directory."); return BinaryPrimitives.ReadUInt16LittleEndian(resources.AsSpan(offset)); }
            uint U32(int offset) { if (offset < 0 || offset + 4 > resources.Length) throw new InvalidDataException("Truncated PE resource directory."); return BinaryPrimitives.ReadUInt32LittleEndian(resources.AsSpan(offset)); }
            int? Find(int offset, int? wanted)
            {
                var count = checked(U16(offset + 12) + U16(offset + 14));
                if (count > 4096) throw new InvalidDataException("Too many PE resource directory entries.");
                for (var index = 0; index < count; index++)
                {
                    var entry = checked(offset + 16 + index * 8); var name = U32(entry);
                    if (wanted is null || (name & 0x80000000) == 0 && name == wanted)
                        return unchecked((int)U32(entry + 4));
                }
                return null;
            }
            var type = Find(0, 24); // RT_MANIFEST
            if (type is null) return WindowsExecutionLevel.AsInvoker;
            var node = type.Value;
            for (var depth = 0; (node & unchecked((int)0x80000000)) != 0; depth++)
            {
                if (depth >= 3) throw new InvalidDataException("The executable manifest resource is nested too deeply.");
                node = Find(node & 0x7fffffff, null) ?? throw new InvalidDataException("An executable manifest directory is empty.");
            }
            var rva = U32(node); var length = U32(node + 4);
            if (length is 0 or > 1024 * 1024) throw new InvalidDataException("The executable manifest exceeds its supported size.");
            file.Position = Offset(checked((int)rva)); var xml = new byte[length]; file.ReadExactly(xml);
            using var input = new MemoryStream(xml);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            var document = XDocument.Load(reader);
            var levels = document.Descendants().Where(element => element.Name.LocalName == "requestedExecutionLevel").Select(element => (string?)element.Attribute("level")).ToArray();
            if (levels.Length > 1) throw new InvalidDataException("The executable contains conflicting execution-level declarations.");
            return levels.FirstOrDefault() switch
            {
                null or "asInvoker" => WindowsExecutionLevel.AsInvoker,
                "highestAvailable" => WindowsExecutionLevel.HighestAvailable,
                "requireAdministrator" => WindowsExecutionLevel.RequireAdministrator,
                _ => throw new InvalidDataException("The executable declares an unknown Windows execution level.")
            };
        }
        catch (BadImageFormatException) { return WindowsExecutionLevel.AsInvoker; }
    }
}
