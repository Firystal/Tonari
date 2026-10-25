using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ChuniLanFour;

internal static class GameFilePreparationTests
{
    private static int passed;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private sealed class Progress { internal string Target, Message; internal FilePreparationState State; }
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL " + message); passed++; Console.WriteLine("PASS " + message); }
    private static Exception Reject(Action action, string message)
    {
        Exception rejected = null;
        try { action(); } catch (GameFilePreparationException ex) { rejected = ex; } catch (InvalidOperationException ex) { rejected = ex; } catch (IOException ex) { rejected = ex; } catch (ArgumentException ex) { rejected = ex; }
        Check(rejected != null, message); return rejected;
    }
    private static string IniText(int slot, string subnet)
    { return "; retained comment\r\n[netenv]\r\nenable=1\r\naddrSuffix=" + (10 + slot) + "\r\n[keychip]\r\nsubnet=" + subnet + "\r\n[gpio]\r\ndipsw1=" + (slot == 1 ? 1 : 0) + "\r\n"; }
    private static RoomConfig Room(string ini, int slot)
    { return new RoomConfig { RoomName = "Fixture", NetworkId = "0123456789abcdef", Slot = slot, Subnet = "192.168.139.0", GameIniPath = ini }; }
    private static GameLaunchInspection InspectFixture(string bat, string subnet)
    {
        return new GameLaunchInspection { BatPath = Path.GetFullPath(bat), GameDirectory = Path.GetDirectoryName(Path.GetFullPath(bat)),
            BatHash = GameLauncher.Hash(File.ReadAllBytes(bat)), CanUseConnect = true, CanUseDistribution = true, Subnet = subnet };
    }
    private static string TestFixturePath(string ini) { return Path.Combine(Path.GetDirectoryName(ini), "test-role.fixture"); }
    private static void WriteTestFixture(string ini, int role, int group)
    { File.WriteAllText(TestFixturePath(ini), role + "," + group, Utf8); }
    private static TestRoleInspection InspectTestFixture(string ini)
    {
        string path = TestFixturePath(ini); string[] fields = File.ReadAllText(path).Split(',');
        return new TestRoleInspection { SavePath = path, BinDirectory = Path.GetDirectoryName(ini), FileHash = GameLauncher.Hash(File.ReadAllBytes(path)),
            Role = Int32.Parse(fields[0]), Group = Int32.Parse(fields[1]), IniPath = ini, IniHash = GameLauncher.Hash(File.ReadAllBytes(ini)) };
    }
    private static TestRoleInspection ApplyTestFixture(TestRoleInspection expected, int role)
    {
        var current = InspectTestFixture(expected.IniPath);
        if (current.FileHash != expected.FileHash || current.IniHash != expected.IniHash) throw new InvalidOperationException("Stale machine-state fixture snapshot");
        WriteTestFixture(expected.IniPath, role, current.Group); return InspectTestFixture(expected.IniPath);
    }
    private static GameFilesResult PrepareBin(RoomConfig room, string bin, bool solo, List<Progress> events,
        Func<string, TestRoleInspection> inspectTest = null, Func<TestRoleInspection, int, TestRoleInspection> writeTest = null,
        Action stopped = null, Func<RoomConfig, string, bool, string> writeIni = null, Func<string, string, GameLaunchInspection> inspectBat = null)
    {
        Action guard = stopped ?? delegate { };
        return GameFilePreparation.PrepareBinCore(room, bin, solo,
            delegate(string target, FilePreparationState state, string message) { if (events != null) events.Add(new Progress { Target = target, State = state, Message = message }); },
            inspectBat ?? InspectFixture, inspectTest ?? InspectTestFixture,
            writeIni ?? delegate(RoomConfig target, string hash, bool standalone) { return SetupActions.ApplyConfig(target, target.GameIniPath, guard, hash); },
            writeTest ?? ApplyTestFixture, guard);
    }
    private static void BinWorkflowChecks(string root)
    {
        string bin = Path.Combine(root, "bin-workflow"); Directory.CreateDirectory(bin);
        string ini = Path.Combine(bin, "segatools.ini"), bat = Path.Combine(bin, "start.bat");
        Action reset = delegate { File.WriteAllText(ini, IniText(3, "192.168.139.0"), Utf8); File.WriteAllText(bat, "@echo off\r\nrem bin fixture\r\n", Utf8); WriteTestFixture(ini, 1, 2); };
        reset(); var room = Room(ini, 3); var events = new List<Progress>();
        Action<string, FilePreparationState, string> report = delegate(string target, FilePreparationState state, string message) { events.Add(new Progress { Target = target, State = state, Message = message }); };
        byte[] original = File.ReadAllBytes(ini), saveOriginal = File.ReadAllBytes(TestFixturePath(ini));
        DateTime iniTime = File.GetLastWriteTimeUtc(ini), testTime = File.GetLastWriteTimeUtc(TestFixturePath(ini));
        var inspected = GameFilePreparation.InspectBinCore(room, bin, false, report, InspectFixture, InspectTestFixture);
        Check(inspected.IsComplete && inspected.BinDirectory == bin && inspected.Config.Slot == 3 && inspected.TestRole.Role == 1 && inspected.TestRole.Group == 2, "bin inspection returns all three file snapshots");
        Check(new[] { "ini", "bat", "test" }.All(entry => events.Any(x => x.Target == entry && x.State == FilePreparationState.Ready)), "bin inspection independently reports three ready entries");
        Check(File.ReadAllBytes(ini).SequenceEqual(original) && File.ReadAllBytes(TestFixturePath(ini)).SequenceEqual(saveOriginal) && File.GetLastWriteTimeUtc(ini) == iniTime && File.GetLastWriteTimeUtc(TestFixturePath(ini)) == testTime, "selecting bin is strictly read-only");
        inspected = GameFilePreparation.InspectBinCore(room, bin, true, null, InspectFixture, InspectTestFixture);
        Check(inspected.Target.Slot == 1 && inspected.Config.Slot == 3 && inspected.TestRole.Role == 1 && File.ReadAllBytes(ini).SequenceEqual(original) && File.ReadAllBytes(TestFixturePath(ini)).SequenceEqual(saveOriginal), "draft standalone mode changes target only without writing INI or machine-state");
        var unconfigured = new RoomConfig();
        inspected = GameFilePreparation.InspectBinCore(unconfigured, bin, false, null, InspectFixture, InspectTestFixture);
        Check(inspected.IsComplete && inspected.Target.Slot == 1 && inspected.Target.Subnet == "192.168.139.0", "unconfigured room can inspect all files with a standalone fallback target");
        var invalidRoom = Reject(() => PrepareBin(unconfigured, bin, false, null), "unconfigured network deployment rejected before writes");
        Check(!(invalidRoom is GameFilePreparationException) && File.ReadAllBytes(ini).SequenceEqual(original) && File.ReadAllBytes(TestFixturePath(ini)).SequenceEqual(saveOriginal), "invalid room save preserves both configuration files");

        events.Clear(); File.WriteAllText(ini, "[gpio]\r\ndipsw1=1\r\n[gpio]\r\ndipsw1=0\r\n", Utf8);
        int batReads = 0, testReads = 0;
        inspected = GameFilePreparation.InspectBinCore(room, bin, false, report,
            delegate(string path, string subnet) { batReads++; return InspectFixture(path, subnet); },
            delegate(string path) { testReads++; return InspectTestFixture(path); });
        Check(!inspected.IsComplete && inspected.Config == null && inspected.Launch != null && inspected.TestRole != null && batReads == 1 && testReads == 1, "INI failure still attempts BAT and machine-state inspection");
        Check(events.Any(x => x.Target == "ini" && x.State == FilePreparationState.Error) && events.Any(x => x.Target == "bat" && x.State == FilePreparationState.Ready) && events.Any(x => x.Target == "test" && x.State == FilePreparationState.Ready), "partial inspection preserves successful item statuses");
        reset(); events.Clear();
        inspected = GameFilePreparation.InspectBinCore(room, bin, false, report, delegate { throw new IOException("BAT failure"); }, InspectTestFixture);
        Check(!inspected.IsComplete && inspected.Config != null && inspected.Launch == null && inspected.TestRole != null, "BAT failure still permits INI and machine-state results");
        inspected = GameFilePreparation.InspectBinCore(room, bin, false, report, InspectFixture, delegate { throw new IOException("machine-state failure"); });
        Check(!inspected.IsComplete && inspected.Config != null && inspected.Launch != null && inspected.TestRole == null, "machine-state failure keeps the overall inspection incomplete");
        var preflight = Reject(() => PrepareBin(room, bin, true, null, inspectTest: delegate { throw new IOException("machine-state failure"); }), "machine-state preflight failure prevents all configuration writes");
        Check(!(preflight is GameFilePreparationException) && File.ReadAllBytes(ini).SequenceEqual(original) && File.ReadAllBytes(TestFixturePath(ini)).SequenceEqual(saveOriginal), "three-item preflight fails without partial-write marker");

        var publicInspection = GameFilePreparation.InspectBin(room, bin, false, null);
        Check(publicInspection.Config != null && publicInspection.Launch != null && publicInspection.TestRole == null && !publicInspection.IsComplete, "public inspection enforces real machine-state layout validation");
        Reject(() => GameFilePreparation.PrepareBin(room, bin, true, null), "public deployment refuses an unverified machine-state layout");
        Check(File.ReadAllBytes(ini).SequenceEqual(original) && File.ReadAllBytes(TestFixturePath(ini)).SequenceEqual(saveOriginal), "production machine-state guard preserves fixture files");

        events.Clear(); var solo = PrepareBin(room, bin, true, events);
        Check(solo.IsComplete && solo.Standalone && solo.Config.Slot == 1 && solo.TestRole.Role == 0 && solo.TestRole.Group == 2, "explicit save applies standalone role to both INI and machine-state");
        Check(solo.IniHash == SetupActions.InspectGameConfig(ini).Hash && solo.TestRole.FileHash == InspectTestFixture(ini).FileHash && room.Slot == 3, "saved result hashes match both files and preserve prior room slot");
        var network = PrepareBin(room, bin, false, null);
        Check(network.Config.Slot == 3 && network.TestRole.Role == 1 && network.TestRole.Group == 2 && room.Slot == 3, "explicit network save restores saved client slot and follower machine-state role");
        Check(events.Any(x => x.Target == "test" && x.State == FilePreparationState.Ready && x.Message.Contains("保存完成")), "successful save reports final machine-state deployment status");

        reset(); var running = Reject(() => PrepareBin(room, bin, true, null, stopped: delegate { throw new InvalidOperationException("running"); }), "running game prevents both configuration writes");
        Check(!(running is GameFilePreparationException) && File.ReadAllBytes(ini).SequenceEqual(original) && File.ReadAllBytes(TestFixturePath(ini)).SequenceEqual(saveOriginal), "running guard fails before any write attempt");
        int inspections = 0;
        var race = Reject(() => PrepareBin(room, bin, true, null, inspectTest: delegate(string path)
        {
            if (++inspections == 2) WriteTestFixture(path, 1, 3);
            return InspectTestFixture(path);
        }), "changed machine-state snapshot rejected in final preflight");
        Check(!(race is GameFilePreparationException) && File.ReadAllBytes(ini).SequenceEqual(original) && InspectTestFixture(ini).Group == 3, "machine-state preflight race preserves INI and external machine-state changes");
        reset(); events.Clear();
        var partial = Reject(() => PrepareBin(room, bin, true, events, writeTest: delegate { throw new IOException("machine-state write failed"); }), "machine-state failure after INI commit propagates incomplete deployment") as GameFilePreparationException;
        Check(partial != null && partial.ConfigurationMayHaveChanged && partial.Target.Slot == 1 && SetupActions.InspectGameConfig(ini).Slot == 1 && InspectTestFixture(ini).Role == 1, "partial two-file deployment is explicitly marked and never rolled back blindly");
        Check(events.Last().Target == "test" && events.Last().State == FilePreparationState.Error && !events.Any(x => x.Target == "test" && x.Message.Contains("保存完成")), "failed machine-state save cannot report final success");
        reset();
        partial = Reject(() => PrepareBin(room, bin, true, null, writeTest: delegate(TestRoleInspection expected, int role)
        {
            ApplyTestFixture(expected, role); throw new IOException("machine-state post-commit failure");
        }), "machine-state post-commit failure remains incomplete") as GameFilePreparationException;
        Check(partial != null && partial.ConfigurationMayHaveChanged && InspectTestFixture(ini).Role == 0, "machine-state post-commit error preserves evidence of actual saved role");
        reset();
        partial = Reject(() => PrepareBin(room, bin, true, null, writeTest: delegate(TestRoleInspection expected, int role) { return InspectTestFixture(expected.IniPath); }), "machine-state readback role mismatch rejects overall success") as GameFilePreparationException;
        Check(partial != null && partial.ConfigurationMayHaveChanged && InspectTestFixture(ini).Role == 1, "machine-state readback mismatch returns partial-write failure");
        reset(); int testWrites = 0;
        partial = Reject(() => PrepareBin(room, bin, true, null,
            writeIni: delegate(RoomConfig target, string hash, bool standalone)
            {
                string outcome = SetupActions.ApplyConfig(target, target.GameIniPath, delegate { }, hash);
                WriteTestFixture(ini, 1, 4); return outcome;
            }, writeTest: delegate { testWrites++; throw new Exception("Unexpected machine-state overwrite"); }), "external machine-state change after INI commit prevents machine-state overwrite") as GameFilePreparationException;
        Check(partial != null && partial.ConfigurationMayHaveChanged && testWrites == 0 && InspectTestFixture(ini).Group == 4, "late machine-state race preserves external group and reports partial deployment");

        reset(); bool batLocked = false;
        PrepareBin(room, bin, true, null, writeTest: delegate(TestRoleInspection expected, int role)
        {
            try { using (var stream = new FileStream(bat, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } }
            catch (IOException) { batLocked = true; }
            return ApplyTestFixture(expected, role);
        });
        Check(batLocked, "BAT remains locked through machine-state save as well as INI save");
        Check(!Directory.GetFiles(bin).Any(x => Path.GetFileName(x).Contains(".bak") || Path.GetFileName(x).Contains(".tmp-")), "two-file save leaves no backups or temporary files");
    }
    private static void BinValidationChecks(string root)
    {
        string bin = Path.Combine(root, "bin-validation"); Directory.CreateDirectory(bin);
        string ini = Path.Combine(bin, "segatools.ini"), bat = Path.Combine(bin, "start.bat");
        Action reset = delegate { File.WriteAllText(ini, IniText(3, "192.168.139.0"), Utf8); File.WriteAllText(bat, "@echo off\r\nrem validation fixture\r\n", Utf8); WriteTestFixture(ini, 1, 2); };
        reset(); var room = Room(ini, 3); byte[] original = File.ReadAllBytes(ini); var events = new List<Progress>();
        int writes = 0;
        Func<RoomConfig, string, bool, string> noWrite = delegate { writes++; throw new Exception("Unexpected write"); };
        File.Delete(bat);
        Reject(() => PrepareBin(room, bin, true, events, writeIni: noWrite), "missing BAT prevents bin deployment");
        Check(writes == 0 && File.ReadAllBytes(ini).SequenceEqual(original), "missing BAT preserves configuration");
        reset(); events.Clear();
        var prevalidation = Reject(() => PrepareBin(room, bin, true, events, writeIni: noWrite, inspectBat: delegate(string path, string subnet)
        {
            var snapshot = InspectFixture(path, subnet); snapshot.GameDirectory = Path.Combine(root, "other"); return snapshot;
        }), "inspection from another game directory rejected before writing");
        Check(!(prevalidation is GameFilePreparationException) && writes == 0 && File.ReadAllBytes(ini).SequenceEqual(original), "directory validation has no partial-write marker");

        byte[] edited = null; reset(); events.Clear();
        Reject(() => PrepareBin(room, bin, true, events, inspectBat: delegate(string path, string subnet)
        {
            var snapshot = InspectFixture(path, subnet); File.AppendAllText(ini, "; external change\r\n", Utf8); edited = File.ReadAllBytes(ini); return snapshot;
        }), "bin expected hash rejects INI changes after inspection");
        Check(File.ReadAllBytes(ini).SequenceEqual(edited), "bin hash race preserves external INI changes");
        reset(); events.Clear();
        Reject(() => PrepareBin(room, bin, true, events, writeIni: noWrite, inspectBat: delegate(string path, string subnet)
        {
            var snapshot = InspectFixture(path, subnet); File.AppendAllText(path, "rem changed\r\n", Utf8); return snapshot;
        }), "bin deployment rejects BAT changed after inspection");
        Check(writes == 0 && File.ReadAllBytes(ini).SequenceEqual(original), "BAT race never writes INI");

        reset(); int stopChecks = 0;
        Reject(() => PrepareBin(room, bin, true, null, stopped: delegate { if (++stopChecks > 1) throw new InvalidOperationException("started during preparation"); }), "game starting during bin workflow blocks checked writer");
        Check(File.ReadAllBytes(ini).SequenceEqual(original), "late running guard leaves INI unchanged");
        var solo = PrepareBin(room, bin, true, null);
        Check(solo.Config.Slot == 1 && File.ReadAllText(ini).Contains("; retained comment"), "retry after game exits applies mode and retains comments");

        reset(); events.Clear();
        var partial = Reject(() => PrepareBin(room, bin, true, events, writeIni: delegate { return "ignored"; }), "bin INI readback mismatch cannot report success") as GameFilePreparationException;
        Check(partial != null && partial.ConfigurationMayHaveChanged && partial.Standalone && partial.Target.Slot == 1, "bin readback mismatch retains attempted target");
        Check(events.Last().State == FilePreparationState.Error && events.Last().Target == "ini", "bin INI readback mismatch reports configuration failure");
        reset();
        partial = Reject(() => PrepareBin(room, bin, true, null, writeIni: delegate(RoomConfig target, string hash, bool standalone)
        {
            SetupActions.ApplyConfig(target, target.GameIniPath, delegate { }, hash); throw new IOException("writer failed after commit");
        }), "bin INI writer exception after commit propagates") as GameFilePreparationException;
        Check(partial != null && partial.ConfigurationMayHaveChanged && partial.InnerException.Message == "writer failed after commit" && SetupActions.InspectGameConfig(ini).Slot == 1 && room.Slot == 3, "bin partial INI commit preserves original cause and saved room");

        reset(); File.WriteAllText(ini, "[gpio]\r\ndipsw1=1\r\n", Utf8);
        Check(SetupActions.InspectGameConfig(ini).Role == GameConfigRole.Unknown, "incomplete bin INI remains unknown before deployment");
        Check(PrepareBin(null, bin, true, null).Target.Subnet == "192.168.139.0", "standalone bin without room uses default subnet");
        File.WriteAllText(ini, IniText(2, "192.168.138.0"), Utf8);
        Check(PrepareBin(room, bin, true, null).Target.Subnet == "192.168.138.0", "standalone bin prefers detected subnet");
        File.WriteAllText(ini, "[gpio]\r\ndipsw1=1\r\n", Utf8); room.Subnet = "192.168.137.0";
        Check(PrepareBin(room, bin, true, null).Target.Subnet == "192.168.137.0", "standalone bin falls back to saved subnet");
        room.Subnet = "192.168.139.0"; reset();
        File.WriteAllText(bat, "@echo off\r\nrem CHUNI_LAN_MANAGED\r\n", Utf8);
        Reject(() => PrepareBin(room, bin, true, null, writeIni: noWrite, inspectBat: GameLauncher.Inspect), "derived BAT rejected by bin pipeline");
        Check(writes == 0 && File.ReadAllBytes(ini).SequenceEqual(original), "derived BAT cannot change INI");

        reset(); string junction = Path.Combine(root, "linked-validation");
        var command = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -Command \"New-Item -ItemType Junction -Path '" + junction.Replace("'", "''") + "' -Target '" + bin.Replace("'", "''") + "' | Out-Null\"")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        using (var process = Process.Start(command)) { if (!process.WaitForExit(10000) || process.ExitCode != 0) throw new Exception("Cannot create fixture junction"); }
        try
        {
            Reject(() => PrepareBin(room, junction, true, null, writeIni: noWrite), "junction bin rejected before deployment");
            Check(writes == 0 && File.ReadAllBytes(ini).SequenceEqual(original), "junction bin cannot change target INI");
        }
        finally { Directory.Delete(junction, false); }
    }
    private static void RealTestBackendIntegration(string root)
    {
        string bin = Path.Combine(root, "real-test-backend"), ini = TestRoleFixture.BuildBin(bin, 1, 3);
        File.AppendAllText(ini, IniText(3, "192.168.139.0"), Utf8);
        File.WriteAllText(Path.Combine(bin, "start.bat"), "@echo off\r\nrem non-runnable fixture\r\n", Utf8);
        string save = Path.Combine(bin, "appdata", "appfile.dat");
        var room = Room(ini, 3); byte[] originalIni = File.ReadAllBytes(ini), originalSave = File.ReadAllBytes(save);
        DateTime iniTime = File.GetLastWriteTimeUtc(ini), saveTime = File.GetLastWriteTimeUtc(save);
        var inspected = GameFilePreparation.InspectBin(room, bin, false, null);
        Check(inspected.IsComplete && inspected.TestRole.Role == 1 && inspected.TestRole.Group == 3, "public bin inspection accepts validated synthetic PE and CRC SAVE fixture");
        Check(File.ReadAllBytes(ini).SequenceEqual(originalIni) && File.ReadAllBytes(save).SequenceEqual(originalSave) && File.GetLastWriteTimeUtc(ini) == iniTime && File.GetLastWriteTimeUtc(save) == saveTime, "public three-file inspection preserves real-format fixture bytes and timestamps");
        Func<bool, GameFilesResult> saveMode = delegate(bool solo)
        {
            return GameFilePreparation.PrepareBinCore(room, bin, solo, null, GameLauncher.Inspect, GameTestRole.Inspect,
                delegate(RoomConfig target, string hash, bool mode) { return SetupActions.ApplyConfig(target, target.GameIniPath, delegate { }, hash); },
                delegate(TestRoleInspection expected, int role) { return GameTestRole.ApplyCheckedCore(expected, role, delegate { }, null, File.Replace); },
                delegate { });
        };
        var single = saveMode(true);
        Check(single.IsComplete && single.Config.Slot == 1 && single.TestRole.Role == 0 && single.TestRole.Group == 3, "real checked writers deploy INI and machine-state host role together");
        Check(GameTestRole.Inspect(ini).FileHash == single.TestRole.FileHash && SetupActions.InspectGameConfig(ini).Hash == single.IniHash, "real machine-state snapshot INI binding refreshes after INI deployment");
        var network = saveMode(false);
        Check(network.IsComplete && network.Config.Slot == 3 && network.TestRole.Role == 1 && network.TestRole.Group == 3 && room.Slot == 3, "real checked writers restore saved client role and preserve machine-state group");
        Check(File.ReadAllBytes(save).SequenceEqual(originalSave) && File.ReadAllBytes(ini).SequenceEqual(originalIni), "host then client round trip restores both real-format fixture files exactly");
        Check(Directory.GetFiles(Path.GetDirectoryName(save)).Length == 1 && !Directory.GetFiles(bin).Any(p => Path.GetFileName(p).Contains(".bak") || Path.GetFileName(p).Contains(".tmp-")), "real checked workflow creates no backup or leftover temporary files");
    }
    private static void ResolutionChecks(string ini)
    {
        var savedHost = Room(ini, 1); var savedClient = Room(ini, 3);
        var host = new GameConfigInspection { Role = GameConfigRole.Host, Slot = 1, Subnet = "192.168.139.0", Hash = "HASH" };
        var client = new GameConfigInspection { Role = GameConfigRole.Client, Slot = 3, Subnet = "192.168.139.0", Hash = "HASH" };
        var unknown = new GameConfigInspection { Role = GameConfigRole.Unknown, Subnet = "", Hash = "UNKNOWN", Summary = "配置角色未确定", Description = "缺少配置。" };
        var record = new GameLaunchSettings { StandaloneMode = true, StandaloneIniPath = ini, StandaloneIniHash = "HASH" };
        Check(!GameFilePreparation.ResolveStandalone(client, savedClient, record, ini), "detected client overrides old standalone preference");
        Check(!GameFilePreparation.ResolveStandalone(host, savedHost, null, ini), "host matches saved host room");
        Check(GameFilePreparation.ResolveStandalone(host, savedClient, null, ini), "host does not replace saved client slot");
        Check(GameFilePreparation.ResolveStandalone(host, null, null, ini), "unrecorded host defaults to standalone");
        Check(GameFilePreparation.ResolveStandalone(host, savedHost, record, ini.ToUpperInvariant()), "host uses matching standalone path and hash record");
        record.StandaloneIniHash = "STALE";
        Check(!GameFilePreparation.ResolveStandalone(host, savedHost, record, ini), "stale standalone host hash does not override matching host room");
        savedHost.Subnet = "192.168.138.0";
        Check(GameFilePreparation.ResolveStandalone(host, savedHost, record, ini), "host in another subnet is not claimed as saved network host");
        Check(GameFilePreparation.ResolveStandalone(unknown, savedClient, record, ini), "unknown config retains matching-path standalone mode");
        record.StandaloneMode = false;
        Check(!GameFilePreparation.ResolveStandalone(unknown, null, record, ini), "unknown config retains matching-path network preference");
        record.StandaloneIniPath = Path.Combine(Path.GetDirectoryName(ini), "different.ini");
        Check(!GameFilePreparation.ResolveStandalone(unknown, savedClient, record, ini), "unknown unrecorded config uses saved network preference");
        record.IniPath = ini;
        Check(!GameFilePreparation.ResolveStandalone(unknown, null, record, ini), "unknown config uses current INI path network record");
        record.IniPath = Path.Combine(Path.GetDirectoryName(ini), "current-other.ini"); record.StandaloneIniPath = ini;
        Check(GameFilePreparation.ResolveStandalone(unknown, null, record, ini), "current INI record supersedes stale standalone path");
        record.IniPath = ""; record.StandaloneIniPath = Path.Combine(Path.GetDirectoryName(ini), "different.ini");
        record.StandaloneMode = true;
        Check(GameFilePreparation.ResolveStandalone(unknown, savedClient, record, ini), "unknown unrecorded config respects standalone preference");
        Check(GameFilePreparation.ResolveStandalone(unknown, null, null, ini), "unknown config without room defaults to standalone");
        Check(unknown.Role == GameConfigRole.Unknown && unknown.Summary == "配置角色未确定" && unknown.Description == "缺少配置。", "mode resolution does not mislabel unknown inspection");
        Check(savedClient.Slot == 3 && savedHost.Slot == 1, "mode resolution preserves room slots");
    }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException("Run through run-preparation-tests.ps1.");
            string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            ResolutionChecks(Path.Combine(root, "segatools.ini"));
            BinWorkflowChecks(root);
            BinValidationChecks(root);
            RealTestBackendIntegration(root);
            Console.WriteLine("ALL PASSED " + passed + "; fixture=" + root + "; no real game or network operation.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
