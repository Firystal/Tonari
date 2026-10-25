using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ChuniLanFour;

internal static class GameLauncherTests
{
    private static int passed;
    private static readonly string TestGate = @"Local\CHUNITHM_LAN_Test_" + Guid.NewGuid().ToString("N");
    private static PreparedGameLaunch PrepareChecked(GameLaunchInspection inspected, bool connect, bool distribution, int slot, string subnet)
    { return GameLauncher.PrepareCore(inspected, connect, distribution, slot, subnet, () => false, TestGate); }
    private static Process StartChecked(PreparedGameLaunch prepared, Func<bool> running, Func<ProcessStartInfo, Process> starter, Action<Process, IDisposable> keepLease)
    { return GameLauncher.StartCore(prepared, running, starter, keepLease, TestGate); }
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
    private static void Reject(Action action, string name) { bool failed = false; try { action(); } catch (InvalidOperationException) { failed = true; } catch (IOException) { failed = true; } catch (ArgumentException) { failed = true; } Check(failed, name); }
    private static void W16(byte[] b, int at, ushort value) { Array.Copy(BitConverter.GetBytes(value), 0, b, at, 2); }
    private static void W32(byte[] b, int at, uint value) { Array.Copy(BitConverter.GetBytes(value), 0, b, at, 4); }
    private static void Ascii(byte[] b, int at, string value) { Array.Copy(Encoding.ASCII.GetBytes(value + "\0"), 0, b, at, value.Length + 1); }
    private static byte[] PeFixture(bool game, bool x64)
    {
        byte[] b = new byte[game ? 0x12200 : 0x400]; W16(b, 0, 0x5a4d); W32(b, 0x3c, 0x80); W32(b, 0x80, 0x4550); W16(b, 0x84, x64 ? (ushort)0x8664 : (ushort)0x14c); W16(b, 0x86, game ? (ushort)2 : (ushort)1);
        W16(b, 0x94, x64 ? (ushort)240 : (ushort)224); int op = 0x98; W16(b, op, x64 ? (ushort)0x20b : (ushort)0x10b); W32(b, op + (x64 ? 24 : 28), 0x400000); W32(b, op + 56, game ? 0x1e1c000u : 0x2000u);
        int section = op + (x64 ? 240 : 224); Ascii(b, section, ".text"); W32(b, section + 8, game ? 0x10000u : 0x200u); W32(b, section + 12, game ? 0x670000u : 0x1000u); W32(b, section + 16, game ? 0x10000u : 0x200u); W32(b, section + 20, 0x200); W32(b, section + 36, 0x60000020);
        if (!game) return b;
        section += 40; Ascii(b, section, ".idata"); W32(b, section + 8, 0x2000); W32(b, section + 12, 0x1e1a000); W32(b, section + 16, 0x2000); W32(b, section + 20, 0x10200); W32(b, section + 36, 0xc0000040); W32(b, op + 104, 0x1e1a100); W32(b, op + 108, 100);
        Array.Copy(GameLauncher.ConnectSignature, 0, b, 0x200 + 0x796c, 76); Ascii(b, 0x10200 + 0x200, "WS2_32.dll");
        uint[] slots = { 0x1e1abf4, 0x1e1abf8, 0x1e1ac04, 0x1e1ac38 }; uint[] ordinals = { 10, 3, 4, 111 };
        for (int i = 0; i < 4; i++) { int d = 0x10200 + 0x100 + i * 20; W32(b, d, (uint)(0x1e1a300 + i * 16)); W32(b, d + 12, 0x1e1a200); W32(b, d + 16, slots[i]); W32(b, 0x10200 + 0x300 + i * 16, 0x80000000u | ordinals[i]); }
        return b;
    }
    private const string Sample = "@echo off\r\nrem 中文原始注释\r\npushd %~dp0\r\nstart /min inject_x64.exe -d -k chusanamhook.dll amdaemon.exe -f -c config_common.json config_server.json\r\nif exist duolinguo.dll (\r\n    inject_x86.exe -d -k chusanhook.dll -k \"扩展 & 插件.dll\" chusanApp.exe\r\n) else (\r\n    inject_x86.exe -d -k chusanhook.dll chusanApp.exe\r\n)\r\n";
    private static byte[] Encode(Encoding e, string text) { return e.GetPreamble().Concat(e.GetBytes(text)).ToArray(); }
    private static GameLaunchInspection InspectionFixture(string bat)
    {
        var result = GameLauncher.Inspect(bat, "192.168.139.0");
        // Unit-test seam for transformation only: standalone packages must not
        // carry proprietary daemon files. Public Inspect below separately proves
        // that these synthetic daemon identities are rejected in production.
        // This modifies only the test's inspection object, not production guards.
        if (result.DaemonCommands == null || result.DaemonCommands.Count == 0) throw new Exception("Fixture daemon command failed parsing");
        result.CanUseDistribution = true; result.DistributionReason = "Transformation-only synthetic fixture"; return result;
    }
    private static PreparedGameLaunch Prepare(string bat, bool c, bool d, int slot) { return PrepareChecked(InspectionFixture(bat), c, d, slot, "192.168.139.0"); }
    private static string ArtifactDirectory(PreparedGameLaunch launch)
    { return Path.Combine(launch.GameDirectory, "chuni-lan-launch-" + Path.GetFileNameWithoutExtension(launch.BatPath).Substring("start-chuni-lan-".Length)); }
    private static void CheckCleanup(string bat, string directory, byte[] original)
    {
        var prior = Prepare(bat, true, true, 1);
        for (int i = 0; i < 3; i++)
        {
            string oldEntry = prior.BatPath, oldDirectory = ArtifactDirectory(prior);
            File.WriteAllText(Path.Combine(oldDirectory, "connect-20261025-121314-pid123.log"), "fixture");
            File.WriteAllText(Path.Combine(oldDirectory, "daemon-registry-fix-456.log"), "fixture");
            if (i == 0)
            {
                File.Delete(Path.Combine(oldDirectory, "launch.json"));
                File.WriteAllText(oldEntry, File.ReadAllText(oldEntry).Replace("rem CHUNI_LAN_MANAGED\r\n", "rem CHUNI_LAN_MANAGED - original BAT retained\r\n"), new UTF8Encoding(false));
            }
            prior = Prepare(bat, true, true, 1);
            Check(!File.Exists(oldEntry) && !Directory.Exists(oldDirectory), "previous entry modules and logs removed " + i);
            Check(Directory.GetFiles(directory, "start-chuni-lan-*.bat").Length == 1 && Directory.GetDirectories(directory, "chuni-lan-launch-*").Length == 1, "one generated launch remains " + i);
        }
        string lastDirectory = ArtifactDirectory(prior), lastEntry = prior.BatPath;
        Prepare(bat, false, false, 1);
        Check(!File.Exists(lastEntry) && !Directory.Exists(lastDirectory), "all patches disabled still cleans previous launch");
        Check(!Directory.GetFiles(directory).Any(p => Path.GetFileName(p).Contains(".bak")), "startup creates no backups");

        string foreign = Path.Combine(directory, "chuni-lan-launch-abcdef123456"), foreignEntry = Path.Combine(directory, "start-chuni-lan-abcdef123456.bat");
        Directory.CreateDirectory(foreign); File.WriteAllText(Path.Combine(foreign, "notes.txt"), "USER"); File.WriteAllText(foreignEntry, "@echo user file\r\n");
        Prepare(bat, false, false, 1);
        Check(File.ReadAllText(foreignEntry) == "@echo user file\r\n" && File.ReadAllText(Path.Combine(foreign, "notes.txt")) == "USER", "unmarked lookalike paths retained");

        prior = Prepare(bat, true, true, 1); lastDirectory = ArtifactDirectory(prior); lastEntry = prior.BatPath;
        File.WriteAllText(Path.Combine(lastDirectory, "user-notes.txt"), "KEEP");
        string userDirectory = Path.Combine(lastDirectory, "user-folder"); Directory.CreateDirectory(userDirectory); File.WriteAllText(Path.Combine(userDirectory, "document.txt"), "KEEP");
        Prepare(bat, false, false, 1);
        Check(!File.Exists(lastEntry) && !File.Exists(Path.Combine(lastDirectory, "connect_compat.dll")) && !File.Exists(Path.Combine(lastDirectory, "daemon_registry_fix.dll")), "owned files cleaned beside unrelated files");
        Check(File.ReadAllText(Path.Combine(lastDirectory, "user-notes.txt")) == "KEEP" && File.ReadAllText(Path.Combine(userDirectory, "document.txt")) == "KEEP", "unrelated files and subdirectories retained");

        prior = Prepare(bat, true, false, 1); lastDirectory = ArtifactDirectory(prior); lastEntry = prior.BatPath;
        File.Delete(lastEntry);
        Prepare(bat, false, false, 1);
        Check(!Directory.Exists(lastDirectory), "orphaned managed directory cleaned by record and module identity");

        prior = Prepare(bat, true, false, 1); lastDirectory = ArtifactDirectory(prior);
        File.Delete(prior.BatPath); File.Delete(Path.Combine(lastDirectory, "launch.json"));
        File.WriteAllText(Path.Combine(lastDirectory, "connect-20261025-121314-pid789.log"), "fixture");
        Prepare(bat, false, false, 1);
        Check(!Directory.Exists(lastDirectory), "legacy orphaned directory cleaned by module identity");

        prior = Prepare(bat, true, false, 1); lastDirectory = ArtifactDirectory(prior); lastEntry = prior.BatPath;
        var inspected = InspectionFixture(bat);
        Reject(() => GameLauncher.PrepareCore(inspected, false, false, 1, "192.168.139.0", () => true, TestGate), "running game prevents cleanup");
        Check(File.Exists(lastEntry) && File.Exists(Path.Combine(lastDirectory, "connect_compat.dll")), "running guard preserves previous launch");
        using (var gate = new Semaphore(1, 1, TestGate))
        {
            if (!gate.WaitOne(0)) throw new Exception("Test semaphore unexpectedly busy");
            try { Reject(() => Prepare(bat, false, false, 1), "active startup lease prevents cleanup"); }
            finally { gate.Release(); }
        }
        Check(File.Exists(lastEntry), "startup lease guard preserves entry");

        string outside = Path.Combine(Path.GetDirectoryName(directory), "junction-target"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep.txt"), "OUTSIDE");
        string junction = Path.Combine(directory, "chuni-lan-launch-012345abcdef");
        var command = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -Command \"New-Item -ItemType Junction -Path '" + junction.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' | Out-Null\"")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        using (var process = Process.Start(command)) { if (!process.WaitForExit(10000) || process.ExitCode != 0) throw new Exception("Junction fixture creation failed"); }
        try
        {
            Reject(() => Prepare(bat, false, false, 1), "reparse point rejected before cleanup");
            Check(File.ReadAllText(Path.Combine(outside, "keep.txt")) == "OUTSIDE" && File.Exists(lastEntry), "reparse point rejection leaves outside and owned files intact");
        }
        finally { Directory.Delete(junction, false); }
        Prepare(bat, false, false, 1);
        Check(File.ReadAllBytes(bat).SequenceEqual(original), "cleanup preserves original BAT bytes");
    }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException("Run through run-game-launcher-tests.ps1.");
            string root = Path.GetFullPath(args[0]);
            string directory = Path.Combine(root, "中文 & 空格 (A)"); Directory.CreateDirectory(directory); string bat = Path.Combine(directory, "原始 start.bat");
            byte[] game = PeFixture(true, false); GameLauncher.ValidateConnectImage(game); Check(true, "synthetic PE exact native signature and ordinal imports");
            var bad = (byte[])game.Clone(); bad[0x200 + 0x796c] ^= 1; Reject(() => GameLauncher.ValidateConnectImage(bad), "reject changed shared code");
            bad = (byte[])game.Clone(); W32(bad, 0x10200 + 0x100 + 2 * 20 + 16, 0x1e1ac08); Reject(() => GameLauncher.ValidateConnectImage(bad), "reject wrong connect IAT slot");
            bad = (byte[])game.Clone(); W32(bad, 0x10200 + 0x300 + 16, 0x80000004); Reject(() => GameLauncher.ValidateConnectImage(bad), "reject duplicated connect import");
            Reject(() => GameLauncher.ValidateConnectImage(new byte[7]), "reject truncated PE");
            File.WriteAllBytes(Path.Combine(directory, "chusanApp.exe"), game); File.WriteAllBytes(Path.Combine(directory, "inject_x86.exe"), PeFixture(false, false)); File.WriteAllBytes(Path.Combine(directory, "inject_x64.exe"), PeFixture(false, true));
            File.WriteAllBytes(Path.Combine(directory, "chusanhook.dll"), new byte[] { 1, 2, 3 }); File.WriteAllBytes(Path.Combine(directory, "扩展 & 插件.dll"), new byte[] { 4, 5, 6 });
            File.WriteAllBytes(Path.Combine(directory, "amdaemon.exe"), PeFixture(false, true)); File.WriteAllBytes(Path.Combine(directory, "chusanamhook.dll"), PeFixture(false, true));
            File.WriteAllBytes(bat, Encode(new UTF8Encoding(false), Sample)); byte[] original = File.ReadAllBytes(bat); var unsupportedVersion = GameLauncher.Inspect(bat, "192.168.139.0");
            Check(unsupportedVersion.CanUseConnect && !unsupportedVersion.CanUseDistribution && unsupportedVersion.DistributionReason.Contains("版本"), "synthetic daemon identity rejected by actual production guard");
            Reject(() => PrepareChecked(unsupportedVersion, false, true, 1, "192.168.139.0"), "unsupported daemon cannot be prepared through real Inspect");
            var inspect = InspectionFixture(bat);
            for (int mask = 0; mask < 4; mask++)
            {
                bool c = (mask & 1) != 0, d = (mask & 2) != 0; var prepared = PrepareChecked(inspect, c, d, 1, "192.168.139.0"); string result = File.ReadAllText(prepared.BatPath);
                Check(result.Contains("connect_compat.dll") == c && result.Contains("daemon_registry_fix.dll") == d, "base options " + mask);
                if (mask != 0) { Check(result.Contains("pushd \"%~dp0\"\r\nstart /min"), "cwd quote preserves newline " + mask); Check(result.Contains("-k chusanhook.dll -k \"扩展 & 插件.dll\""), "existing modules retained " + mask); Reject(() => GameLauncher.Inspect(prepared.BatPath, "192.168.139.0"), "derived BAT rejected " + mask); }
                else Check(prepared.BatPath == bat && result == Sample, "all off uses unchanged original BAT");
            }
            for (int slot = 2; slot <= 4; slot++)
            {
                int captured = slot; var prepared = PrepareChecked(inspect, true, false, slot, "192.168.139.0"); Check(File.ReadAllText(prepared.BatPath).Contains("connect_compat.dll"), "client connect allowed " + slot);
                Reject(() => PrepareChecked(inspect, false, true, captured, "192.168.139.0"), "client distribution refused " + slot);
            }
            Check(File.ReadAllBytes(bat).SequenceEqual(original), "original BAT byte-identical after all combinations");
            var otherNet = GameLauncher.Inspect(bat, "192.168.140.0"); Check(!otherNet.CanUseConnect && otherNet.ConnectReason.Contains("139"), "other subnet explicitly blocked for connect");
            Reject(() => PrepareChecked(otherNet, true, false, 1, "192.168.140.0"), "cannot prepare silent no-op subnet");
            Reject(() => PrepareChecked(inspect, false, false, 1, "192.168.140.0"), "changed subnet requires read again");
            foreach (Encoding encoding in new[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.GetEncoding(936) })
            {
                byte[] source = Encode(encoding, Sample); File.WriteAllBytes(bat, source); var prepared = Prepare(bat, true, true, 1); byte[] generated = File.ReadAllBytes(prepared.BatPath);
                Check(File.ReadAllBytes(bat).SequenceEqual(source), encoding.WebName + " original encoding unchanged");
                Check(encoding.GetString(generated).Contains("中文原始注释") && generated.Take(encoding.GetPreamble().Length).SequenceEqual(encoding.GetPreamble()), encoding.WebName + " output text and BOM retained");
            }
            File.WriteAllBytes(bat, original);
            CheckCleanup(bat, directory, original);
            int unsupportedIndex = 0;
            foreach (string unsupported in new[] { Sample.Replace("    inject_x86.exe", "    call inject_x86.exe"), Sample.Replace("    inject_x86.exe", "    start \"\" inject_x86.exe"), Sample.Replace("chusanApp.exe\r\n", "chusanApp.exe > log.txt\r\n"), Sample.Replace("pushd %~dp0", "cd /d D:\\other"), Sample.Replace("-k chusanhook.dll", "-k chusanhook.dll ^"), Sample.Replace("start /min inject_x64.exe", "start /min \"inject_x64.exe\""), Sample.Replace("    inject_x86.exe", "    echo starting & inject_x86.exe") })
            {
                File.WriteAllText(bat, unsupported, new UTF8Encoding(false)); var failed = GameLauncher.Inspect(bat, "192.168.139.0");
                Check(unsupportedIndex == 5 ? !failed.CanUseDistribution && failed.DistributionReason.Contains("标题") : !failed.CanUseConnect, "unsupported launch syntax not guessed " + unsupportedIndex); unsupportedIndex++;
            }
            File.WriteAllBytes(bat, original); inspect = GameLauncher.Inspect(bat, "192.168.139.0");
            File.AppendAllText(bat, "rem changed\r\n"); Reject(() => PrepareChecked(inspect, true, false, 1, "192.168.139.0"), "source changed between Inspect and Prepare"); File.WriteAllBytes(bat, original);
            inspect = GameLauncher.Inspect(bat, "192.168.139.0"); string module = Path.Combine(directory, "扩展 & 插件.dll"); File.WriteAllBytes(module, new byte[] { 8 }); Reject(() => PrepareChecked(inspect, true, false, 1, "192.168.139.0"), "existing injection changed between Inspect and Prepare"); File.WriteAllBytes(module, new byte[] { 4, 5, 6 });
            var pending = Prepare(bat, true, false, 1); bool started = false;
            Reject(() => StartChecked(pending, () => true, p => { started = true; return null; }, (p, lease) => lease.Dispose()), "duplicate game rejects startup"); Check(!started, "duplicate guard never called starter");
            File.AppendAllText(pending.BatPath, "rem tamper\r\n"); Reject(() => StartChecked(pending, () => false, p => { started = true; return null; }, (p, lease) => lease.Dispose()), "generated BAT modified before Start rejected");
            pending = Prepare(bat, false, false, 1); ProcessStartInfo capturedInfo = null;
            using (var process = StartChecked(pending, () => false, p => { capturedInfo = p; return Process.GetCurrentProcess(); }, (p, lease) =>
            {
                Reject(() => { using (var f = new FileStream(bat, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } }, "source cannot change during launch lease");
                Reject(() => StartChecked(pending, () => false, x => null, (x, l) => l.Dispose()), "second pending startup blocked before game appears"); lease.Dispose();
            })) Check(capturedInfo.WorkingDirectory == directory && capturedInfo.Arguments.Contains("\"\"" + bat + "\"\"") && !capturedInfo.UseShellExecute, "mock launch preserves cwd and double-quoted cmd invocation");
            Reject(() => GameLauncher.Inspect(Path.Combine(directory, "bad%PATH%.bat"), "192.168.139.0"), "percent path rejected"); Reject(() => GameLauncher.Inspect(Path.Combine(directory, "bad!.bat"), "192.168.139.0"), "delayed expansion path rejected");
            var settings = new GameLaunchSettings { BatPath = bat, UseConnect = true, UseDistribution = false, StandaloneMode = true, StandaloneIniPath = "fixture.ini", StandaloneSubnet = "192.168.139.0", StandaloneIniHash = "fixture-hash" }; string settingsPath = Path.Combine(root, "local-game.json"); settings.Save(settingsPath); var loaded = GameLaunchSettings.Load(settingsPath); Check(loaded.BatPath == bat && loaded.UseConnect && !loaded.UseDistribution && !File.ReadAllText(settingsPath).Contains("NetworkId"), "local launch preferences isolated from room export");
            Check(loaded.StandaloneMode && loaded.StandaloneIniPath == settings.StandaloneIniPath && loaded.StandaloneSubnet == settings.StandaloneSubnet && loaded.StandaloneIniHash == settings.StandaloneIniHash, "standalone launch state roundtrip");
            File.WriteAllText(settingsPath, "{\"BatPath\":\"old.bat\",\"UseConnect\":true}"); loaded = GameLaunchSettings.Load(settingsPath);
            Check(!loaded.StandaloneMode && loaded.StandaloneIniPath == "" && loaded.StandaloneSubnet == "" && loaded.StandaloneIniHash == "", "old launch preferences default to network mode");
            // This runs only an ASCII, harmless fixture BAT. It tests the same quoting
            // builder directly, never bypasses the public game's duplicate-process guard.
            string harmless = Path.Combine(directory, "quote & echo not-command.bat"), marker = Path.Combine(directory, "quoted-marker.txt"); File.WriteAllText(harmless, "@echo off\r\n> \"%~dp0quoted-marker.txt\" echo SAFE\r\n", Encoding.ASCII);
            var harmlessInfo = GameLauncher.BuildStartInfo(harmless, directory); harmlessInfo.CreateNoWindow = true; harmlessInfo.WindowStyle = ProcessWindowStyle.Hidden;
            using (var process = Process.Start(harmlessInfo)) { Check(process.WaitForExit(5000) && process.ExitCode == 0, "real harmless cmd quoting exits successfully"); }
            Check(File.Exists(marker) && File.ReadAllText(marker).Trim() == "SAFE", "Unicode spaces ampersand parentheses path safe in actual cmd");
            if (args.Length > 1 && !String.IsNullOrWhiteSpace(args[1])) { var actual = GameLauncher.Inspect(args[1], "192.168.139.0"); Check(actual.CanUseConnect && actual.CanUseDistribution, "optional actual original BAT read-only inspection supports both patches"); }
            else Console.WriteLine("SKIP optional actual BAT inspection; standalone tests use no game installation.");
            Console.WriteLine("ALL PASSED " + passed + "; fixture=" + root + "; no actual game started or stopped."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
