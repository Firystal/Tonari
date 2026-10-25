using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ChuniLanFour
{
    public sealed class TestRoleInspection
    {
        public string SavePath { get; internal set; }
        public string BinDirectory { get; internal set; }
        public string FileHash { get; internal set; }
        public int Role { get; internal set; }
        public int Group { get; internal set; }
        public string RoleName { get { return Role == 0 ? "基準機" : "基準機に従う"; } }
        public string GroupName { get { return Group == 0 ? "OFF" : ((char)('A' + Group - 1)).ToString(); } }
        internal string IniPath, IniHash, FileIdentity;
    }

    public sealed class TestRoleWriteException : IOException
    {
        public bool ConfigurationMayHaveChanged { get; private set; }
        internal TestRoleWriteException(string message, Exception inner) : base(message, inner) { ConfigurationMayHaveChanged = true; }
    }

    public static class GameTestRole
    {
        private const int FileSize = 0x800000, MirrorOffset = 0x3fe000;
        private const int RoleOffset = 0x6081, GroupOffset = 0x6080;
        private static readonly int[] BlockOffsets = { 0x4000, 0x4c00, 0x5c00, 0x5e00, 0x6000 };
        private static readonly int[] BlockSizes = { 0xc00, 0x1000, 0x200, 0x200, 0x600 };
        private static readonly uint[] CrcTable = MakeCrcTable();

        // Layout identity uses the record registration, live/backup mapping and role access code.
        // Patches outside these ranges do not affect recognition of this layout.
        private static readonly Signature[] GameSignatures = {
            new Signature(0x0b7560, 0xc5, "97ca916ba4858111194e1a83e808aaa635c0603c17a1d66540624102766cce8a"),
            new Signature(0x181bbd0, 0x64, "68998a9f61b0d5a302ea579a5504880eaf1158c9d72835a943cfa22e6a0e4425"),
            new Signature(0x3e1c80, 0x06, "85b3de13d8a270ea5f5711f14d93e2cf8e8d1456ac565ad22de4aaebde271be6"),
            new Signature(0x9e4265, 0x1c, "ce9cbc6bd740b448dd9ee669c552bcbbda91d5095d42155ebfad03969d6e33a6"),
            new Signature(0x3f2580, 0x20, "4cf016eccaa3fd502d82ab378f14b57bba5e40462970f41c88b31e3824ede06b"),
            new Signature(0x3f1dc0, 0x24, "0f24325eb9e6789fe7f9938974a3f729cfc70eb6e388843fa2f0eed28142bc30")
        };
        private static readonly Signature[] DaemonSignatures = {
            new Signature(0x24aa09, 0x18, "eb0da4452511cc6b82a559a0e60df617034da5df02952c5e445bc75794aac616"),
            new Signature(0x24aef1, 0x14, "0475a20ec418d25291742a95800bab35f3e89766ce10d3cb20c08276cbb3c09d"),
            new Signature(0x2cd220, 0xc5, "be1130de7f414fbcef9465472c4ff09c253cbaad55cbfdd2dbe174e10994978f")
        };

        public static TestRoleInspection Inspect(string iniPath)
        {
            var location = ResolveLocation(iniPath);
            ValidateExecutables(location.BinDirectory);
            using (var input = OpenRead(location.SavePath, FileShare.Read))
            {
                string identity = Identity(input);
                return Describe(location, ReadSave(input), identity);
            }
        }

        public static TestRoleInspection ApplyChecked(TestRoleInspection expected, int targetRole)
        {
            return ApplyCheckedCore(expected, targetRole, RequireStopped, null, File.Replace);
        }

        // The callbacks allow failure and race tests to use isolated, non-runnable fixtures.
        internal static TestRoleInspection ApplyCheckedCore(TestRoleInspection expected, int targetRole, Action requireStopped,
            Action beforeCommit, Action<string, string, string> replace)
        {
            if (expected == null || String.IsNullOrWhiteSpace(expected.IniPath) || String.IsNullOrWhiteSpace(expected.FileHash))
                throw new ArgumentException("请先读取机器状态。", "expected");
            if (targetRole != 0 && targetRole != 1) throw new ArgumentOutOfRangeException("targetRole", "机器状态须为 0 或 1。");
            requireStopped();
            string mutexName = "Local\\ChuniLanTestRole-" + HashHex(Encoding.UTF8.GetBytes(expected.SavePath.ToUpperInvariant()));
            using (var mutex = new Mutex(false, mutexName))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("另一个操作正在保存机器状态，请稍后重试。");
                try { return ApplyLocked(expected, targetRole, requireStopped, beforeCommit, replace); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static TestRoleInspection ApplyLocked(TestRoleInspection expected, int role, Action requireStopped,
            Action beforeCommit, Action<string, string, string> replace)
        {
            string temporary = null; bool replacementAttempted = false;
            try
            {
                var location = ResolveLocation(expected.IniPath);
                RequireLocation(expected, location);
                ValidateExecutables(location.BinDirectory);
                // Deny in-place writes while preparing and checking the atomic replacement.
                using (var input = OpenRead(location.SavePath, FileShare.Read | FileShare.Delete))
                {
                    string identity = Identity(input);
                    byte[] original = ReadSave(input);
                    var current = Describe(location, original, identity);
                    RequireSnapshot(expected, current);
                    if (current.Role == role) return current;
                    byte[] changed = (byte[])original.Clone();
                    changed[RoleOffset] = changed[RoleOffset + MirrorOffset] = (byte)role;
                    PutUInt32(changed, 0x6000, Crc32(changed, 0x6004, 0x5fc));
                    PutUInt32(changed, 0x404000, Crc32(changed, 0x404004, 0x5fc));
                    ValidateSave(changed);
                    RequireNarrowChange(original, changed);
                    temporary = Path.Combine(Path.GetDirectoryName(location.SavePath), ".chunilan-testrole-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        output.Write(changed, 0, changed.Length);
                        output.Flush(true);
                    }
                    if (beforeCommit != null) beforeCommit();
                    requireStopped();
                    RequireLocation(expected, ResolveLocation(expected.IniPath));
                    ValidateExecutables(location.BinDirectory);
                    using (var check = OpenRead(location.SavePath, FileShare.Read | FileShare.Delete))
                    {
                        RequireSnapshot(expected, Describe(location, ReadSave(check), Identity(check)));
                        replacementAttempted = true;
                        replace(temporary, location.SavePath, null);
                    }
                    using (var readback = OpenRead(location.SavePath, FileShare.Read))
                    {
                        byte[] actual = ReadSave(readback);
                        if (!actual.SequenceEqual(changed)) throw new IOException("机器状态写入校验失败，请重新读取设置。");
                        return Describe(location, actual, Identity(readback));
                    }
                }
            }
            catch (Exception ex)
            {
                if (replacementAttempted) throw new TestRoleWriteException("机器状态保存未能完成校验，请重新读取设置。", ex);
                throw;
            }
            finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void RequireSnapshot(TestRoleInspection expected, TestRoleInspection actual)
        {
            if (expected.FileHash != actual.FileHash || expected.FileIdentity != actual.FileIdentity || expected.Group != actual.Group || expected.Role != actual.Role)
                throw new InvalidOperationException("机器状态存档在读取后发生变化，请重新读取设置。");
        }
        private static void RequireLocation(TestRoleInspection expected, Location actual)
        {
            if (!String.Equals(expected.SavePath, actual.SavePath, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(expected.BinDirectory, actual.BinDirectory, StringComparison.OrdinalIgnoreCase) || expected.IniHash != actual.IniHash)
                throw new InvalidOperationException("游戏存档路径或 segatools.ini 已发生变化，请重新读取设置。");
        }
        private static TestRoleInspection Describe(Location location, byte[] bytes, string identity)
        {
            ValidateSave(bytes);
            return new TestRoleInspection { SavePath = location.SavePath, BinDirectory = location.BinDirectory, FileHash = Hash(bytes),
                Role = bytes[RoleOffset], Group = bytes[GroupOffset], IniPath = location.IniPath, IniHash = location.IniHash, FileIdentity = identity };
        }
        private static void ValidateSave(byte[] bytes)
        {
            if (bytes.Length != FileSize) throw new InvalidDataException("机器状态存档大小不符合已支持的布局。");
            for (int block = 0; block < BlockOffsets.Length; block++)
            {
                int offset = BlockOffsets[block], size = BlockSizes[block];
                for (int copy = 0; copy < 2; copy++)
                {
                    int start = offset + copy * MirrorOffset;
                    if (UInt32At(bytes, start + 4) != 0x53444844 || UInt32At(bytes, start) != Crc32(bytes, start + 4, size - 4))
                        throw new InvalidDataException("机器状态存档记录校验失败，请先在游戏中完成一次正常保存。");
                }
            }
            for (int i = 0; i < 0x600; i++)
                if (bytes[0x6000 + i] != bytes[0x404000 + i]) throw new InvalidDataException("机器状态的两份记录不一致，请先在游戏中完成一次正常保存。");
            if (bytes[RoleOffset] > 1 || bytes[GroupOffset] > 4) throw new InvalidDataException("机器状态存档包含未知状态或组号。");
        }
        private static void RequireNarrowChange(byte[] before, byte[] after)
        {
            for (int i = 0; i < before.Length; i++)
                if (before[i] != after[i] && i != RoleOffset && i != RoleOffset + MirrorOffset &&
                    !(i >= 0x6000 && i < 0x6004) && !(i >= 0x404000 && i < 0x404004))
                    throw new InvalidDataException("机器状态变更范围校验失败。");
        }
        private static void RequireStopped()
        {
            foreach (string name in new[] { "chusanApp", "amdaemon" })
            {
                var processes = Process.GetProcessesByName(name);
                bool running = processes.Length != 0;
                foreach (var process in processes) process.Dispose();
                if (running) throw new InvalidOperationException(name + " 正在运行，请退出游戏后保存设置。");
            }
        }

        private sealed class Location { internal string IniPath, IniHash, BinDirectory, SavePath; }
        private static Location ResolveLocation(string iniPath)
        {
            if (String.IsNullOrWhiteSpace(iniPath)) throw new ArgumentException("请先选择游戏 bin 目录。", "iniPath");
            iniPath = CanonicalPath(iniPath);
            if (!String.Equals(Path.GetFileName(iniPath), "segatools.ini", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择游戏使用的 segatools.ini。");
            byte[] bytes;
            using (var input = OpenRead(iniPath, FileShare.Read)) bytes = ReadAll(input, 4 * 1024 * 1024, "segatools.ini");
            string bin = Path.GetDirectoryName(iniPath);
            var paths = new List<string>();
            foreach (string text in DecodeIni(bytes))
            {
                string value = ReadAppData(text);
                if (value.StartsWith("\"", StringComparison.Ordinal) || value.EndsWith("\"", StringComparison.Ordinal))
                {
                    if (value.Length < 2 || value[0] != '"' || value[value.Length - 1] != '"') throw new InvalidDataException("[vfs] appdata 的引号不完整。");
                    value = value.Substring(1, value.Length - 2);
                }
                if (String.IsNullOrWhiteSpace(value)) throw new InvalidDataException("请在 [vfs] 中设置 appdata 目录。");
                if (value.Length >= 2 && value[1] == ':' && (value.Length < 3 || (value[2] != '\\' && value[2] != '/')))
                    throw new InvalidDataException("[vfs] appdata 须使用完整绝对路径或相对 bin 的路径。");
                if (Path.IsPathRooted(value) && value[0] != '\\' && !(value.Length >= 3 && value[1] == ':')) throw new InvalidDataException("[vfs] appdata 路径无效。");
                if (value.StartsWith("\\", StringComparison.Ordinal) && !value.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException("[vfs] appdata 不能使用省略盘符的路径。");
                string directory = CanonicalPath(Path.IsPathRooted(value) ? value : Path.Combine(bin, value));
                string save = CanonicalPath(Path.Combine(directory, "appfile.dat"));
                if (!paths.Any(p => String.Equals(p, save, StringComparison.OrdinalIgnoreCase))) paths.Add(save);
            }
            var existing = paths.Where(File.Exists).ToArray();
            if (existing.Length > 1) throw new InvalidDataException("segatools.ini 的路径编码存在歧义，请将配置另存为带 BOM 的 UTF-8。");
            if (existing.Length == 0) throw new FileNotFoundException("未找到机器状态存档 appfile.dat，请先启动游戏完成初始保存，并检查 [vfs] appdata。", paths[0]);
            CheckNoLinks(existing[0]);
            return new Location { IniPath = iniPath, IniHash = Hash(bytes), BinDirectory = bin, SavePath = existing[0] };
        }
        private static IEnumerable<string> DecodeIni(byte[] bytes)
        {
            if (bytes.Length == 0) throw new InvalidDataException("segatools.ini 为空。");
            var texts = new List<string>();
            if (bytes.Length >= 4 && ((bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0) ||
                (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff))) throw new InvalidDataException("请将 UTF-32 配置另存为 UTF-8。");
            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) AddDecoded(texts, new UnicodeEncoding(false, false, true), bytes, 2, true);
            else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) AddDecoded(texts, new UnicodeEncoding(true, false, true), bytes, 2, true);
            else if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) AddDecoded(texts, new UTF8Encoding(false, true), bytes, 3, true);
            else if (bytes.Contains((byte)0))
            {
                bool little = false, big = false;
                for (int i = 0; i + 1 < bytes.Length; i += 2) { if (bytes[i] == '[' && bytes[i + 1] == 0) little = true; if (bytes[i] == 0 && bytes[i + 1] == '[') big = true; }
                if (little == big || bytes.Length % 2 != 0) throw new InvalidDataException("无法确认配置的 UTF-16 字节序。");
                AddDecoded(texts, new UnicodeEncoding(big, false, true), bytes, 0, true);
            }
            else
            {
                AddDecoded(texts, new UTF8Encoding(false, true), bytes, 0, false);
                AddDecoded(texts, Encoding.GetEncoding(0, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), bytes, 0, false);
            }
            if (texts.Count == 0) throw new InvalidDataException("segatools.ini 编码无法识别，请另存为 UTF-8。");
            return texts;
        }
        private static void AddDecoded(List<string> texts, Encoding encoding, byte[] bytes, int skip, bool required)
        {
            try
            {
                string text = encoding.GetString(bytes, skip, bytes.Length - skip);
                if (text.IndexOf('\0') >= 0) throw new InvalidDataException("segatools.ini 包含空字符。");
                if (!texts.Contains(text)) texts.Add(text);
            }
            catch (DecoderFallbackException) { if (required) throw new InvalidDataException("segatools.ini 编码无效。"); }
        }
        private static string ReadAppData(string text)
        {
            string section = "", result = null;
            var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in Regex.Split(text, "\\r\\n|\\r|\\n"))
            {
                string value = line.Trim(' ', '\t');
                if (value.Length == 0 || value[0] == ';' || value[0] == '#') continue;
                if (value[0] == '[')
                {
                    var match = Regex.Match(value, @"^\[([^\]\r\n]+)\][ \t]*(?:[;#].*)?$");
                    if (!match.Success) throw new InvalidDataException("segatools.ini 存在无效节标题。");
                    section = match.Groups[1].Value.Trim(' ', '\t');
                    if (section.Length == 0 || !sections.Add(section)) throw new InvalidDataException("segatools.ini 存在空节名或重复节。");
                    continue;
                }
                int equals = value.IndexOf('=');
                if (equals < 0) throw new InvalidDataException("segatools.ini 存在无法识别的配置行。");
                string key = value.Substring(0, equals).Trim(' ', '\t');
                if (section.Length == 0 || key.Length == 0 || !keys.Add(section + "\0" + key)) throw new InvalidDataException("segatools.ini 存在重复键或节外配置。");
                if (String.Equals(section, "vfs", StringComparison.OrdinalIgnoreCase) && String.Equals(key, "appdata", StringComparison.OrdinalIgnoreCase))
                    result = value.Substring(equals + 1).Trim(' ', '\t');
            }
            if (result == null) throw new InvalidDataException("segatools.ini 缺少 [vfs] appdata。");
            return result;
        }

        private static string CanonicalPath(string path)
        {
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal)) throw new InvalidDataException("不支持设备命名空间路径。");
            if (path.IndexOfAny(new[] { '\0', '"', '<', '>', '|', '*', '?' }) >= 0 || path.IndexOf(':', 2) >= 0) throw new InvalidDataException("文件路径包含无效字符。");
            foreach (string part in path.Split('\\', '/')) if (part != "." && part != ".." && (part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal))) throw new InvalidDataException("文件路径存在不明确的结尾字符。");
            return Path.GetFullPath(path);
        }
        private static void CheckNoLinks(string path)
        {
            string current = path;
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("机器状态路径不能包含符号链接或目录联接。");
                string parent = Path.GetDirectoryName(current);
                if (String.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }
        private static FileStream OpenRead(string path, FileShare sharing)
        {
            CheckNoLinks(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, sharing);
            try { Identity(stream); return stream; } catch { stream.Dispose(); throw; }
        }
        private static string Identity(FileStream stream)
        {
            FileInformation info;
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out info)) throw new IOException("无法读取文件身份。", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            if (info.Links != 1 || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("机器状态文件不能使用硬链接或符号链接。");
            return info.Volume.ToString("X8") + info.IndexHigh.ToString("X8") + info.IndexLow.ToString("X8");
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes; internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

        private static byte[] ReadSave(FileStream input)
        {
            if (input.Length != FileSize) throw new InvalidDataException("机器状态存档大小不符合已支持的布局。");
            return ReadAll(input, FileSize, "机器状态存档");
        }
        private static byte[] ReadAll(FileStream input, int maximum, string label)
        {
            if (input.Length > maximum) throw new InvalidDataException(label + " 文件过大。");
            var bytes = new byte[(int)input.Length]; int offset = 0;
            while (offset < bytes.Length) { int count = input.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new IOException(label + " 读取不完整。"); offset += count; }
            return bytes;
        }
        private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes)); }
        private static string HashHex(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static uint[] MakeCrcTable()
        {
            var result = new uint[256];
            for (uint i = 0; i < result.Length; i++) { uint value = i; for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1; result[i] = value; }
            return result;
        }
        private static uint Crc32(byte[] bytes, int offset, int length)
        {
            uint crc = 0xffffffffU;
            for (int i = offset; i < offset + length; i++) crc = CrcTable[(crc ^ bytes[i]) & 0xff] ^ (crc >> 8);
            return crc ^ 0xffffffffU;
        }
        private static uint UInt32At(byte[] bytes, int offset) { return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24); }
        private static void PutUInt32(byte[] bytes, int offset, uint value) { for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (i * 8)); }

        private sealed class Signature
        {
            internal int Rva, Length; internal string Hash;
            internal Signature(int rva, int length, string hash) { Rva = rva; Length = length; Hash = hash; }
        }
        private static void ValidateExecutables(string bin)
        {
            ValidatePe(Path.Combine(bin, "chusanApp.exe"), 0x14c, 0x10b, 0x400000UL, GameSignatures);
            ValidatePe(Path.Combine(bin, "amdaemon.exe"), 0x8664, 0x20b, 0x140000000UL, DaemonSignatures);
        }
        private static void ValidatePe(string path, int machine, int magic, ulong imageBase, Signature[] signatures)
        {
            using (var input = OpenRead(path, FileShare.Read))
            using (var reader = new BinaryReader(input))
            {
                string error = Path.GetFileName(path) + " 的机器状态存档布局尚不支持。";
                try
                {
                    if (input.Length < 0x100 || input.Length > 512 * 1024 * 1024 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException(error);
                    input.Position = 0x3c; int pe = reader.ReadInt32();
                    if (pe < 0x40 || pe > input.Length - 24) throw new InvalidDataException(error);
                    input.Position = pe;
                    if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != machine) throw new InvalidDataException(error);
                    int count = reader.ReadUInt16();
                    input.Position = pe + 20; int optionalLength = reader.ReadUInt16();
                    if (count < 1 || count > 96 || optionalLength < (magic == 0x10b ? 0xe0 : 0xf0) || pe + 24L + optionalLength + count * 40L > input.Length) throw new InvalidDataException(error);
                    input.Position = pe + 24;
                    if (reader.ReadUInt16() != magic) throw new InvalidDataException(error);
                    input.Position = pe + 24 + (magic == 0x10b ? 28 : 24);
                    ulong actualBase = magic == 0x10b ? reader.ReadUInt32() : reader.ReadUInt64();
                    if (actualBase != imageBase) throw new InvalidDataException(error);
                    var sections = new List<long[]>();
                    for (int i = 0; i < count; i++)
                    {
                        input.Position = pe + 24 + optionalLength + i * 40 + 8;
                        uint virtualSize = reader.ReadUInt32(), rva = reader.ReadUInt32(), rawSize = reader.ReadUInt32(), raw = reader.ReadUInt32();
                        if ((long)raw + rawSize > input.Length) throw new InvalidDataException(error);
                        sections.Add(new[] { (long)rva, Math.Max((long)virtualSize, rawSize), (long)raw, (long)rawSize });
                    }
                    foreach (var signature in signatures)
                    {
                        var matching = sections.Where(s => signature.Rva >= s[0] && (long)signature.Rva + signature.Length <= s[0] + s[1]).ToArray();
                        if (matching.Length != 1 || signature.Rva - matching[0][0] + signature.Length > matching[0][3]) throw new InvalidDataException(error);
                        input.Position = matching[0][2] + signature.Rva - matching[0][0];
                        byte[] bytes = reader.ReadBytes(signature.Length);
                        if (bytes.Length != signature.Length || HashHex(bytes) != signature.Hash) throw new InvalidDataException(error);
                    }
                }
                catch (EndOfStreamException) { throw new InvalidDataException(error); }
            }
        }
    }
}
