using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ChuniLanFour;

// Fixtures contain only the audited signature windows in synthetic, non-runnable PE files.
// No real game executable or saved game is required by this test suite.
internal static class TestRoleFixture
{
    internal static readonly int[] Offsets = { 0x4000, 0x4c00, 0x5c00, 0x5e00, 0x6000 };
    internal static readonly int[] Sizes = { 0xc00, 0x1000, 0x200, 0x200, 0x600 };
    internal static string BuildBin(string bin, int role = 0, int group = 1)
    {
        Directory.CreateDirectory(bin);
        WriteExecutables(bin);
        Directory.CreateDirectory(Path.Combine(bin, "appdata"));
        File.WriteAllBytes(Path.Combine(bin, "appdata", "appfile.dat"), SaveBytes(role, group));
        string ini = Path.Combine(bin, "segatools.ini");
        File.WriteAllText(ini, "[vfs]\r\nappdata=appdata\r\n", new UTF8Encoding(true));
        return ini;
    }
    internal static byte[] SaveBytes(int role, int group)
    {
        var bytes = new byte[0x800000];
        for (int i = 0; i < 0x4000; i++) bytes[i] = (byte)(i * 17 + 29);
        for (int b = 0; b < Offsets.Length; b++)
        {
            for (int i = 8; i < Sizes[b]; i++) bytes[Offsets[b] + i] = (byte)(i * 13 + b * 19);
            Put32(bytes, Offsets[b] + 4, 0x53444844);
        }
        bytes[0x6080] = (byte)group; bytes[0x6081] = (byte)role;
        for (int b = 0; b < Offsets.Length; b++)
        {
            FixCrc(bytes, Offsets[b], Sizes[b]);
            Array.Copy(bytes, Offsets[b], bytes, Offsets[b] + 0x3fe000, Sizes[b]);
        }
        return bytes;
    }
    internal static void FixCrc(byte[] bytes, int offset, int length)
    {
        uint crc = 0xffffffffU;
        for (int i = offset + 4; i < offset + length; i++)
        {
            crc ^= bytes[i];
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320U : crc >> 1;
        }
        Put32(bytes, offset, crc ^ 0xffffffffU);
    }
    internal static void Put32(byte[] bytes, int offset, uint value) { for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (8 * i)); }
    internal static void WriteExecutables(string bin)
    {
        File.WriteAllBytes(Path.Combine(bin, "chusanApp.exe"), Pe(false, new[] {
            Pair(0xb7560,"83ec0cc70424f00fcb01c7442404400a0000f30f7e0424c70424f428cb01c7442404c40e0000660fd6054044cb01f30f7e0424c704242c39cb01c744240474010000660fd6054c44cb01f30f7e0424c70424103bcb01c744240470000000660fd6055844cb01f30f7e0424660fd6056444cb01c70424e03fcb01c744240460040000f30f7e0424c7054844cb0102000000c7055444cb0102000000c7056044cb0102000000c7056c44cb0102000000660fd6057044cb01c7057844cb010200000083c40cc3"),
            Pair(0x181bbd0,"b005cb01f00fcb01400a00000100000000000000301acb01f428cb01c40e00000200000000000000b837cb012c39cb01740100000300000000000000a03acb01103bcb01700000000400000000000000803bcb01e03fcb01600400000000000000000000"),
            Pair(0x3e1c80,"b8803bcb01c3"),
            Pair(0x9e4265,"8b0d880cca01e8b69762ff8bc8e87f3667ff8bf80fb647780fb65f79"),
            Pair(0x3f2580,"8b442404568bf18a0088442408e8a7e2c6ff8a5424083ad073038856015ec204"),
            Pair(0x3f1dc0,"568b742408ba80000000578bf90fb60688078a46018847018a46028847028a4603884703")
        }));
        File.WriteAllBytes(Path.Combine(bin, "amdaemon.exe"), Pe(true, new[] {
            Pair(0x24aa09,"c744244401000000c7442448010000008944244ce8fe2808"),
            Pair(0x24aef1,"c744243c0100000089442444c744244001000000"),
            Pair(0x2cd220,"48895c2418574883ec30833ddf26690000488bfa488bd97510b8e4ffffff488b5c24504883c4305fc34885c90f84bc0000004885d20f84b30000008b5108488974244833f639710c74574c8d4704488d0da326690083c2fc4533c948896c2440e87bc402008b0f3bc1742d833d862a6900017c1f894c24204c8d05d9014300488d0dfa014300448bc8bab8000000e85d9dffffbee9ffffff488b6c24404883c704837b100074358b07448b4b14443bc8742a833d3f2a6900017c1c4c8d05de014300488d0d")
        }));
    }
    private static KeyValuePair<int, string> Pair(int rva, string hex) { return new KeyValuePair<int, string>(rva, hex); }
    private static byte[] Hex(string text) { return Enumerable.Range(0, text.Length / 2).Select(i => Convert.ToByte(text.Substring(i * 2, 2), 16)).ToArray(); }
    private static byte[] Pe(bool x64, KeyValuePair<int, string>[] signatures)
    {
        int[] pages = signatures.Select(s => s.Key & ~0xfff).Distinct().OrderBy(i => i).ToArray();
        var bytes = new byte[0x1000 * (1 + pages.Length)];
        using (var output = new MemoryStream(bytes)) using (var writer = new BinaryWriter(output))
        {
            writer.Write((ushort)0x5a4d); output.Position = 0x3c; writer.Write(0x80);
            output.Position = 0x80; writer.Write(0x4550); writer.Write((ushort)(x64 ? 0x8664 : 0x14c)); writer.Write((ushort)pages.Length);
            output.Position = 0x94; writer.Write((ushort)(x64 ? 0xf0 : 0xe0)); writer.Write((ushort)0x0002);
            output.Position = 0x98; writer.Write((ushort)(x64 ? 0x20b : 0x10b));
            output.Position = 0x98 + (x64 ? 24 : 28); if (x64) writer.Write(0x140000000UL); else writer.Write(0x400000U);
            output.Position = 0x98 + 32; writer.Write(0x1000); writer.Write(0x200);
            output.Position = 0x98 + 56; writer.Write(pages.Max() + 0x1000); writer.Write(0x1000);
            for (int i = 0; i < pages.Length; i++)
            {
                output.Position = 0x98 + (x64 ? 0xf0 : 0xe0) + i * 40;
                writer.Write(Encoding.ASCII.GetBytes(".test" + i + "\0\0"));
                writer.Write(0x1000); writer.Write(pages[i]); writer.Write(0x1000); writer.Write((i + 1) * 0x1000);
                output.Position += 12; writer.Write(0x60000020U);
            }
            foreach (var signature in signatures)
            {
                int page = Array.IndexOf(pages, signature.Key & ~0xfff);
                output.Position = (page + 1) * 0x1000 + (signature.Key & 0xfff); writer.Write(Hex(signature.Value));
            }
        }
        return bytes;
    }
}

internal static class GameTestRoleTests
{
    private static int passed;
    private static void Assert(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
    private static void Reject(Action action, string label)
    {
        bool rejected = false;
        try { action(); } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, label);
    }
    private static TestRoleInspection Apply(TestRoleInspection expected, int role, Action before = null, Action stopped = null, Action<string, string, string> replace = null)
    {
        return GameTestRole.ApplyCheckedCore(expected, role, stopped ?? (() => { }), before, replace ?? File.Replace);
    }
    private static string Hash(string path) { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path))); }
    private static void NoTemporary(string directory, string label) { Assert(!Directory.GetFiles(directory, ".chunilan-testrole-*").Any(), label); }
    private static void WriteIni(string path, Encoding encoding, string appdata) { File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes("; 配置\r\n[vfs]\r\nappdata=" + appdata + "\r\n")).ToArray()); }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException("Run through run-test-role-tests.ps1.");
            string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            string ini = TestRoleFixture.BuildBin(Path.Combine(root, "main"));
            string bin = Path.GetDirectoryName(ini), save = Path.Combine(bin, "appdata", "appfile.dat");
            byte[] original = File.ReadAllBytes(save); DateTime time = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc); File.SetLastWriteTimeUtc(save, time);
            var first = GameTestRole.Inspect(ini);
            Assert(first.Role == 0 && first.Group == 1 && first.RoleName == "基準機" && first.GroupName == "A", "inspect role and group");
            Assert(first.SavePath == save && first.BinDirectory == bin && first.FileHash == Hash(save), "inspection paths and full hash");
            Assert(File.ReadAllBytes(save).SequenceEqual(original) && File.GetLastWriteTimeUtc(save) == time, "inspection is read-only");
            var same = Apply(first, 0);
            Assert(same.FileHash == first.FileHash && File.GetLastWriteTimeUtc(save) == time, "same role is a no-op preserving timestamp");
            NoTemporary(Path.GetDirectoryName(save), "no-op creates no temporary file");
            var client = Apply(first, 1); byte[] modified = File.ReadAllBytes(save);
            var differences = Enumerable.Range(0, original.Length).Where(i => original[i] != modified[i]).ToArray();
            Assert(client.Role == 1 && client.Group == 1 && client.RoleName == "基準機に従う", "host to client preserves group");
            Assert(differences.Contains(0x6081) && differences.Contains(0x404081) && differences.All(i => i == 0x6081 || i == 0x404081 || (i >= 0x6000 && i < 0x6004) || (i >= 0x404000 && i < 0x404004)), "only two role bytes and their CRC bytes change");
            byte[] expected = (byte[])original.Clone(); expected[0x6081] = expected[0x404081] = 1; TestRoleFixture.FixCrc(expected, 0x6000, 0x600); TestRoleFixture.FixCrc(expected, 0x404000, 0x600);
            Assert(modified.SequenceEqual(expected), "CRC matches independent bitwise implementation");
            Assert(GameTestRole.Inspect(ini).FileHash == client.FileHash, "read-back accepts all record CRCs");
            Assert(Apply(client, 0).Role == 0 && File.ReadAllBytes(save).SequenceEqual(original), "client to host restores exact original bytes");
            NoTemporary(Path.GetDirectoryName(save), "successful writes leave no temporary or backup file");
            Assert(Directory.GetFiles(Path.GetDirectoryName(save)).Length == 1, "save directory contains only appfile.dat");
            Reject(() => Apply(first, 1), "stale file identity rejected even if bytes were restored");

            for (int group = 0; group <= 4; group++)
            {
                File.WriteAllBytes(save, TestRoleFixture.SaveBytes(0, group)); var snapshot = GameTestRole.Inspect(ini);
                Assert(Apply(snapshot, 1).Group == group, "preserve group " + group);
            }
            File.WriteAllBytes(save, original); var current = GameTestRole.Inspect(ini);
            Reject(() => Apply(current, 2), "invalid target role rejected");
            current.FileHash = "wrong"; Reject(() => Apply(current, 1), "wrong expected hash rejected"); current = GameTestRole.Inspect(ini);
            Reject(() => Apply(current, 1, stopped: () => { throw new InvalidOperationException("running fixture"); }), "initial running-process guard rejects");
            Assert(File.ReadAllBytes(save).SequenceEqual(original), "initial guard leaves bytes unchanged");
            int guardCalls = 0;
            Reject(() => Apply(current, 1, stopped: () => { if (++guardCalls == 2) throw new InvalidOperationException("started fixture"); }), "precommit running-process guard rejects");
            Assert(guardCalls == 2 && File.ReadAllBytes(save).SequenceEqual(original), "game start before commit leaves bytes unchanged");
            NoTemporary(Path.GetDirectoryName(save), "process-start failure cleans temporary file");
            Reject(() => Apply(current, 1, before: () => { throw new IOException("injected preparation failure"); }), "precommit exception surfaces");
            NoTemporary(Path.GetDirectoryName(save), "precommit exception cleans temporary file");
            Reject(() => Apply(current, 1, replace: (a, b, c) => { throw new IOException("injected replace failure"); }), "replacement failure surfaces");
            Assert(File.ReadAllBytes(save).SequenceEqual(original), "replacement failure preserves source");
            NoTemporary(Path.GetDirectoryName(save), "replacement failure cleans temporary file");
            using (var locked = new FileStream(save, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Reject(() => Apply(current, 1), "exclusive external lock rejects");
            NoTemporary(Path.GetDirectoryName(save), "external lock creates no temporary file");
            bool inPlaceWriteBlocked = false;
            Apply(current, 1, before: () => { try { using (var writer = new FileStream(save, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } } catch (IOException) { inPlaceWriteBlocked = true; } });
            Assert(inPlaceWriteBlocked, "source handle denies concurrent in-place writes");
            File.WriteAllBytes(save, original); current = GameTestRole.Inspect(ini);
            string replacement = Path.Combine(Path.GetDirectoryName(save), "external-replacement.dat");
            byte[] external = TestRoleFixture.SaveBytes(0, 2); File.WriteAllBytes(replacement, external);
            Reject(() => Apply(current, 1, before: () => File.Replace(replacement, save, null)), "external atomic replacement is detected before commit");
            Assert(File.ReadAllBytes(save).SequenceEqual(external), "external replacement is preserved");
            NoTemporary(Path.GetDirectoryName(save), "external replacement failure cleans temporary file");
            File.WriteAllBytes(save, original); current = GameTestRole.Inspect(ini);
            bool competingRejected = false;
            Apply(current, 1, before: () => { var worker = new Thread(() => { try { Apply(current, 1); } catch (InvalidOperationException) { competingRejected = true; } }); worker.Start(); worker.Join(); });
            Assert(competingRejected, "simultaneous helper writes are serialized by per-save mutex");
            File.WriteAllBytes(save, original); current = GameTestRole.Inspect(ini);
            Reject(() => Apply(current, 1, before: () => File.AppendAllText(ini, "; externally changed\r\n")), "INI changed after inspection is rejected");
            Assert(File.ReadAllBytes(save).SequenceEqual(original), "INI-change failure preserves save");
            WriteIni(ini, new UTF8Encoding(true), "appdata"); current = GameTestRole.Inspect(ini);
            bool readbackReported = false;
            try
            {
                Apply(current, 1, replace: (a, b, c) => { File.Replace(a, b, c); File.WriteAllBytes(b, new byte[10]); });
            }
            catch (TestRoleWriteException ex) { readbackReported = ex.ConfigurationMayHaveChanged; }
            Assert(readbackReported, "postcommit verification failure reports possible configuration change");
            NoTemporary(Path.GetDirectoryName(save), "readback failure cleans temporary file");

            Action<byte[]>[] corruptions = {
                bytes => bytes[0x6082] ^= 1,
                bytes => bytes[0x404082] ^= 1,
                bytes => { bytes[0x6004] ^= 1; TestRoleFixture.FixCrc(bytes, 0x6000, 0x600); },
                bytes => { bytes[0x6081] = bytes[0x404081] = 2; TestRoleFixture.FixCrc(bytes, 0x6000, 0x600); TestRoleFixture.FixCrc(bytes, 0x404000, 0x600); },
                bytes => { bytes[0x6080] = bytes[0x404080] = 5; TestRoleFixture.FixCrc(bytes, 0x6000, 0x600); TestRoleFixture.FixCrc(bytes, 0x404000, 0x600); },
                bytes => { bytes[0x404082] ^= 1; TestRoleFixture.FixCrc(bytes, 0x404000, 0x600); },
                bytes => bytes[0x4008] ^= 1
            };
            for (int i = 0; i < corruptions.Length; i++)
            {
                byte[] broken = (byte[])original.Clone(); corruptions[i](broken); File.WriteAllBytes(save, broken);
                Reject(() => GameTestRole.Inspect(ini), "invalid or inconsistent save fixture " + i);
                Assert(File.ReadAllBytes(save).SequenceEqual(broken), "inspection does not repair invalid fixture " + i);
            }
            File.WriteAllBytes(save, new byte[0x7fffff]); Reject(() => GameTestRole.Inspect(ini), "wrong save length rejected"); File.WriteAllBytes(save, original);
            string exe = Path.Combine(bin, "chusanApp.exe"), daemon = Path.Combine(bin, "amdaemon.exe");
            byte[] gameBytes = File.ReadAllBytes(exe), daemonBytes = File.ReadAllBytes(daemon);
            byte[] patched = (byte[])gameBytes.Clone(); patched[0x70] ^= 1; File.WriteAllBytes(exe, patched);
            Assert(GameTestRole.Inspect(ini).Role == 0, "unrelated executable patch keeps supported layout");
            patched[0x1000 + 0x560] ^= 1; File.WriteAllBytes(exe, patched); Reject(() => GameTestRole.Inspect(ini), "record registration signature change rejected");
            File.WriteAllBytes(exe, gameBytes); patched = (byte[])gameBytes.Clone(); patched[0x2000 + 0xc80] ^= 1; File.WriteAllBytes(exe, patched); Reject(() => GameTestRole.Inspect(ini), "role-object signature change rejected");
            File.WriteAllBytes(exe, gameBytes); patched = (byte[])daemonBytes.Clone(); patched[0x1000 + 0xa09] ^= 1; File.WriteAllBytes(daemon, patched); Reject(() => GameTestRole.Inspect(ini), "daemon CRC metadata signature change rejected");
            File.WriteAllBytes(daemon, daemonBytes); patched = (byte[])gameBytes.Clone(); patched[0x84] = 0x64; File.WriteAllBytes(exe, patched); Reject(() => GameTestRole.Inspect(ini), "wrong PE machine rejected"); File.WriteAllBytes(exe, gameBytes);

            string unicodeDirectory = Path.Combine(bin, "存档空间"); Directory.CreateDirectory(unicodeDirectory); string unicodeSave = Path.Combine(unicodeDirectory, "appfile.dat"); File.WriteAllBytes(unicodeSave, original);
            var encodings = new[] { new UTF8Encoding(false), new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), new UnicodeEncoding(false, false), new UnicodeEncoding(true, false), Encoding.Default };
            foreach (Encoding encoding in encodings)
            {
                WriteIni(ini, encoding, "存档空间"); string iniHash = Hash(ini); var snapshot = GameTestRole.Inspect(ini);
                Assert(snapshot.SavePath == unicodeSave && snapshot.Role == 0, "relative Unicode appdata path " + encoding.WebName + " BOM=" + encoding.GetPreamble().Length);
                Assert(Hash(ini) == iniHash, "INI encoding bytes preserved " + encoding.WebName);
            }
            WriteIni(ini, new UTF8Encoding(true), "\"" + unicodeDirectory + "\""); Assert(GameTestRole.Inspect(ini).SavePath == unicodeSave, "absolute quoted appdata path");
            WriteIni(ini, new UTF8Encoding(true), "..\\main\\存档空间"); Assert(GameTestRole.Inspect(ini).SavePath == unicodeSave, "relative parent path resolves against bin");
            string[] invalidIni = { "", "[vfs]\n", "[vfs]\nappdata=appdata\nAPPDATA=appdata", "[vfs]\nappdata=appdata\n[VFS]\n", "[vfs\nappdata=appdata", "appdata=appdata", "[vfs]\nappdata=C:appdata", "[vfs]\nappdata=\\appdata", "[vfs]\nappdata=appdata.", "[vfs]\nappdata=\"appdata", "[vfs]\nappdata=missing", "[vfs]\nappdata=appdata\n[x]\na=1\na=2" };
            for (int i = 0; i < invalidIni.Length; i++) { File.WriteAllText(ini, invalidIni[i], new UTF8Encoding(true)); Reject(() => GameTestRole.Inspect(ini), "invalid INI fixture " + i); }
            File.WriteAllBytes(ini, new byte[] { 0xff, 0xfe, 0, 0, 65, 0, 0, 0 }); Reject(() => GameTestRole.Inspect(ini), "UTF32 rejected");
            File.WriteAllBytes(ini, new byte[] { 0xef, 0xbb, 0xbf, 0xc0, 0xaf }); Reject(() => GameTestRole.Inspect(ini), "invalid UTF8 BOM payload rejected");
            WriteIni(ini, new UTF8Encoding(true), "appdata");
            string link = Path.Combine(root, "linked-save.dat");
            if (!CreateHardLink(link, save, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Reject(() => GameTestRole.Inspect(ini), "hardlinked save rejected"); File.Delete(link);
            string linkedBin = Path.Combine(root, "linked-bin");
            var junction = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/d /c mklink /J \"" + linkedBin + "\" \"" + bin + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = Process.Start(junction)) { process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit(); Assert(process.ExitCode == 0, "fixture directory junction created"); }
            Reject(() => GameTestRole.Inspect(Path.Combine(linkedBin, "segatools.ini")), "parent directory junction rejected");
            Assert(GameTestRole.Inspect(ini).Role == 0, "normal fixture remains readable after link tests");
            NoTemporary(Path.GetDirectoryName(save), "all failure paths leave no temporary files");
            Console.WriteLine("PASS: " + passed + " machine-state checks; isolated fixture root " + root);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
