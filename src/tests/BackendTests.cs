using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ChuniLanFour;

internal static class BackendTests
{
    private static int passed;
    private static RoomConfig Config(int slot) { return new RoomConfig { NetworkId = "0123456789abcdef", Slot = slot }; }
    private static void Assert(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
    private static void Reject(Action action, string label) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidDataException) { rejected = true; } Assert(rejected, label); }
    private static void RejectOperation(Action action, string label) { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Assert(rejected, label); }
    private static byte[] Rewrite(byte[] bytes, RoomConfig cfg) { string role; return SetupActions.RewriteGameConfig(bytes, cfg, out role); }
    private static byte[] Restore(byte[] bytes, RoomConfig cfg) { string role; return SetupActions.RewriteHostConfig(bytes, cfg, out role); }
    private static byte[] WithBom(Encoding encoding, string text) { return encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray(); }
    private static string Hash(byte[] bytes) { using (var sha = System.Security.Cryptography.SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes)); }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException("Run through run-tests.ps1.");
            string root = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(root);
            string inspectionPath = Path.Combine(root, "inspection", "segatools.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(inspectionPath));
            DateTime inspectionTime = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var cfg = Config(1);
            for (int slot = 1; slot <= 4; slot++) Assert(cfg.AddressForSlot(slot) == "192.168.139." + (10 + slot), "slot " + slot + " address");
            Reject(() => cfg.AddressForSlot(5), "reject fifth slot");
            cfg.Subnet = "192.168.300.0"; Reject(cfg.Validate, "reject invalid subnet"); cfg.Subnet = "192.168.139.0";
            cfg.NetworkId = "123;malicious"; Reject(cfg.Validate, "reject CLI injection"); cfg.NetworkId = "0123456789abcdef";
            var missingRule = new TargetInvocationException(new FileNotFoundException("missing firewall rule"));
            Assert(SetupActions.IsMissingFirewallRule(missingRule) && SetupActions.IsMissingFirewallRule(new COMException("missing rule", unchecked((int)0x80070002))), "missing firewall Item accepts mapped .NET and COM errors");
            var deniedRule = new TargetInvocationException(new UnauthorizedAccessException("firewall access denied"));
            var otherRuleError = new TargetInvocationException(new COMException("firewall service unavailable", unchecked((int)0x800706D9)));
            Assert(!SetupActions.IsMissingFirewallRule(deniedRule) && !SetupActions.IsMissingFirewallRule(otherRuleError), "firewall lookup preserves access denied and other failures");
            string firewallFailure = SetupActions.DescribeFirewallFailure(otherRuleError);
            Assert(firewallFailure.Contains("800706D9") && firewallFailure.Contains("firewall service unavailable") && !firewallFailure.Contains("管理员"), "firewall failure exposes real cause without false privilege advice");
            Assert(SetupActions.DescribeFirewallFailure(deniedRule).Contains("80070005") && SetupActions.DescribeFirewallFailure(deniedRule).Contains("Windows 拒绝访问"), "firewall access denial has specific diagnosis");
            string cliFailure = ZeroTierSession.DescribeProcessFailure("control failed", "zerotier-one_x64.exe", "-q info", 7, "", "connection refused");
            Assert(cliFailure.Contains("7") && cliFailure.Contains("connection refused") && !cliFailure.Contains("管理员"), "CLI failure retains exit code and actual cause");
            string nativeFailure = RuntimeSupport.DescribeException(new InvalidOperationException("Cannot stop service", new System.ComponentModel.Win32Exception(5)));
            Assert(nativeFailure.Contains("Win32 5") && nativeFailure.Contains("Cannot stop service"), "service failure retains nested native code");
            Assert(!RuntimeSupport.SafeText("authtoken=do-not-export", 200).Contains("do-not-export"), "diagnostic text redacts credentials");
            string source = "; 中文注释保持不变\r\n[netenv]\r\nenable = 0  ; old\naddrSuffix\t=\t88\r\nrouterSuffix=254\r[keychip]\r\nsubnet = 192.168.8.0 # comment\r\nid=private-value\r\n[gpio]\r\ndipsw1=0\r\ndipsw2=1\r\ndipsw3=1\r\n[unknown]\r\nblob=\u00ff\u0081中文\r\n";
            var encodings = new[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.GetEncoding(936), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), new UnicodeEncoding(false, false), Encoding.GetEncoding(28591) };
            foreach (var encoding in encodings)
            {
                for (int slot = 1; slot <= 4; slot++)
                {
                    cfg.Slot = slot;
                    string expected = source.Replace("enable = 0", "enable = 1").Replace("addrSuffix\t=\t88", "addrSuffix\t=\t" + (10 + slot)).Replace("subnet = 192.168.8.0", "subnet = 192.168.139.0").Replace("dipsw1=0", "dipsw1=" + (slot == 1 ? "1" : "0"));
                    var changed = Rewrite(WithBom(encoding, source), cfg);
                    Assert(changed.SequenceEqual(WithBom(encoding, expected)), encoding.WebName + " BOM=" + encoding.GetPreamble().Length + " slot=" + slot + " exact bytes");
                    Assert(Rewrite(changed, cfg).SequenceEqual(changed), encoding.WebName + " slot=" + slot + " idempotent");
                    File.WriteAllBytes(inspectionPath, changed); File.SetLastWriteTimeUtc(inspectionPath, inspectionTime);
                    var inspection = SetupActions.InspectGameConfig(inspectionPath);
                    Assert(inspection.Role == (slot == 1 ? GameConfigRole.Host : GameConfigRole.Client) && inspection.Slot == slot && inspection.NetenvEnabled && inspection.Subnet == cfg.Subnet, encoding.WebName + " inspection identifies slot " + slot);
                    Assert(inspection.Hash == Hash(changed) && File.ReadAllBytes(inspectionPath).SequenceEqual(changed) && File.GetLastWriteTimeUtc(inspectionPath) == inspectionTime, encoding.WebName + " inspection preserves bytes and write time for slot " + slot);
                    Assert(inspection.Summary.Contains("192.168.139." + (10 + slot)) && !inspection.Summary.Contains("单机") && !String.IsNullOrWhiteSpace(inspection.Description), encoding.WebName + " inspection reports configured role without inferring standalone mode");
                }
                var offline = new RoomConfig { RoomName = "", NetworkId = "", Slot = 4, Subnet = "192.168.139.0" };
                string hostExpected = source.Replace("enable = 0", "enable = 1").Replace("addrSuffix\t=\t88", "addrSuffix\t=\t11").Replace("subnet = 192.168.8.0", "subnet = 192.168.139.0").Replace("dipsw1=0", "dipsw1=1");
                byte[] restored = Restore(WithBom(encoding, source), offline);
                Assert(restored.SequenceEqual(WithBom(encoding, hostExpected)), encoding.WebName + " offline host restoration preserves unrelated bytes");
                Assert(offline.Slot == 4 && offline.RoomName == "" && offline.NetworkId == "" && offline.Subnet == "192.168.139.0", encoding.WebName + " host restoration does not change room profile");
            }
            cfg.Slot = 1;
            var latin = Encoding.GetEncoding(28591);
            string opaque = "[gpio]\ndipsw1=0\n;opaque=" + new String(Enumerable.Range(128, 128).Select(i => (char)i).ToArray()) + "\n[netenv]\nenable=0\naddrSuffix=88\n[keychip]\nsubnet=192.168.8.0\n";
            string opaqueExpected = opaque.Replace("dipsw1=0", "dipsw1=1").Replace("enable=0", "enable=1").Replace("addrSuffix=88", "addrSuffix=11").Replace("subnet=192.168.8.0", "subnet=192.168.139.0");
            Assert(Rewrite(latin.GetBytes(opaque), cfg).SequenceEqual(latin.GetBytes(opaqueExpected)), "all high bytes preserved");
            string[] invalid = { "[gpio]\ndipsw1=0\n[gpio]\n", "[gpio]\ndipsw1=0\nDIPSW1=1", "[gpio]\n[system]\n", "[netenv]\nenable=0", "[gpio]\n[x]\na=1\na=2", "[gpio]\n[bad", "[gpio]\n[netenv]\n[NETENV]\n" };
            for (int i = 0; i < invalid.Length; i++) { string fixture = invalid[i]; Reject(() => Rewrite(latin.GetBytes(fixture), cfg), "reject ambiguous fixture " + i); }
            for (int i = 0; i < invalid.Length; i++)
            {
                File.WriteAllBytes(inspectionPath, latin.GetBytes(invalid[i]));
                if (i == 3) Assert(SetupActions.InspectGameConfig(inspectionPath).Role == GameConfigRole.Unknown, "missing role section remains unknown during inspection");
                else Reject(() => SetupActions.InspectGameConfig(inspectionPath), "inspection rejects ambiguous fixture " + i);
                Assert(File.ReadAllBytes(inspectionPath).SequenceEqual(latin.GetBytes(invalid[i])), "inspection preserves ambiguous fixture " + i);
            }
            string validSystem = "[NETENV] ; network\r\nENABLE = 1 # enabled\r\nADDRSUFFIX = 12 ; client\r\n[KEYCHIP]\r\nSUBNET = 192.168.139.0\r\n[SYSTEM]\r\nDIPSW1 = 0\r\n";
            File.WriteAllBytes(inspectionPath, WithBom(new UnicodeEncoding(true, false), validSystem));
            var systemInspection = SetupActions.InspectGameConfig(inspectionPath);
            Assert(systemInspection.Role == GameConfigRole.Client && systemInspection.Slot == 2 && systemInspection.Description.Contains("[system]"), "inspection supports system section, comments, case and BOM-less big-endian UTF16");
            string[] unknown = {
                validSystem.Replace("ADDRSUFFIX = 12 ; client\r\n", ""),
                validSystem.Replace("ADDRSUFFIX = 12", "ADDRSUFFIX = 11"),
                validSystem.Replace("DIPSW1 = 0", "DIPSW1 = 1"),
                validSystem.Replace("ENABLE = 1", "ENABLE = 0"),
                validSystem.Replace("SUBNET = 192.168.139.0", "SUBNET = 192.168.300.0"),
                validSystem.Replace("DIPSW1 = 0", "DIPSW1 = invalid"),
                validSystem.Replace("SUBNET = 192.168.139.0\r\n", ""),
                "[gpio]\n; empty role\n",
                ""
            };
            for (int i = 0; i < unknown.Length; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(unknown[i]); File.WriteAllBytes(inspectionPath, bytes);
                var result = SetupActions.InspectGameConfig(inspectionPath);
                Assert(result.Role == GameConfigRole.Unknown && result.Slot == 0 && result.Hash == Hash(bytes) && !String.IsNullOrWhiteSpace(result.Description), "incomplete or conflicting inspection remains unknown " + i);
            }
            File.WriteAllBytes(inspectionPath, Encoding.UTF8.GetBytes(validSystem.Replace("ENABLE = 1", "ENABLE = 0")));
            Assert(!SetupActions.InspectGameConfig(inspectionPath).NetenvEnabled, "inspection reports disabled netenv without guessing a role");
            foreach (string subnet in new[] { "192.168.0.0", "192.168.255.0" })
            {
                File.WriteAllBytes(inspectionPath, Encoding.UTF8.GetBytes(validSystem.Replace("192.168.139.0", subnet)));
                Assert(SetupActions.InspectGameConfig(inspectionPath).Subnet == subnet && SetupActions.InspectGameConfig(inspectionPath).Role == GameConfigRole.Client, "inspection accepts subnet boundary " + subnet);
            }
            File.WriteAllBytes(inspectionPath, new byte[] { 0xff, 0xfe, 0, 0, 0x5b, 0, 0, 0 });
            Reject(() => SetupActions.InspectGameConfig(inspectionPath), "inspection rejects UTF32 like the writer");
            Reject(() => SetupActions.InspectGameConfig(Path.Combine(root, "other.ini")), "inspection rejects a non-segatools filename");
            string missing = "[system]\r\ndipsw2=1";
            string missingExpected = "[system]\r\ndipsw2=1\r\ndipsw1=1\r\n[netenv]\r\nenable=1\r\naddrSuffix=11\r\n[keychip]\r\nsubnet=192.168.139.0\r\n";
            Assert(Rewrite(latin.GetBytes(missing), cfg).SequenceEqual(latin.GetBytes(missingExpected)), "missing safe sections and keys inserted");
            var offlineSubnet = new RoomConfig { Slot = 3, NetworkId = "", Subnet = "192.168.140.0" };
            Assert(latin.GetString(Restore(latin.GetBytes(missing), offlineSubnet)).Contains("subnet=192.168.140.0"), "host restoration preserves configured subnet");
            offlineSubnet.Subnet = "192.168.300.0"; Reject(() => Restore(latin.GetBytes(missing), offlineSubnet), "host restoration rejects invalid subnet");
            Assert(ZeroTierSession.Overlaps("192.168.139.12/24", cfg.Cidr), "detect same /24");
            Assert(ZeroTierSession.Overlaps("192.168.0.1/16", cfg.Cidr), "detect wider overlap");
            Assert(!ZeroTierSession.Overlaps("192.168.140.12/24", cfg.Cidr), "different /24 allowed");
            Assert(!ZeroTierSession.Overlaps("10.130.77.190/24", cfg.Cidr), "physical WLAN allowed");
            cfg.GameIniPath = Path.Combine(root, "segatools.ini"); cfg.Slot = 4;
            string shared = Path.Combine(root, "shared-room.json"), local = Path.Combine(root, "local-settings.json");
            cfg.ExportRoom(shared); cfg.Save(local);
            string sharedText = File.ReadAllText(shared);
            Assert(!sharedText.Contains("Slot") && !sharedText.Contains("GameIniPath") && !sharedText.Contains(root), "shared export excludes local details");
            var imported = RoomConfig.ImportRoom(shared); Assert(imported.Slot == 2 && imported.GameIniPath == "" && imported.NetworkId == cfg.NetworkId, "shared import resets local slot/path");
            var loaded = RoomConfig.Load(local); Assert(loaded.Slot == 4 && loaded.GameIniPath == cfg.GameIniPath, "local settings roundtrip");
            File.WriteAllBytes(cfg.GameIniPath, WithBom(new UTF8Encoding(true), source));
            bool gameRunning = System.Diagnostics.Process.GetProcessesByName("chusanApp").Length != 0 || System.Diagnostics.Process.GetProcessesByName("amdaemon").Length != 0;
            byte[] originalFixture = File.ReadAllBytes(cfg.GameIniPath);
            RejectOperation(() => SetupActions.ApplyConfig(cfg, cfg.GameIniPath, delegate { throw new InvalidOperationException("running"); }), "write blocked before opening file while game is running");
            Assert(File.ReadAllBytes(cfg.GameIniPath).SequenceEqual(originalFixture), "blocked apply preserves original file");
            int guardChecks = 0;
            RejectOperation(() => SetupActions.ApplyConfig(cfg, cfg.GameIniPath, delegate { if (++guardChecks == 2) throw new InvalidOperationException("running"); }), "game appearing before commit cancels apply");
            Assert(guardChecks == 2 && File.ReadAllBytes(cfg.GameIniPath).SequenceEqual(originalFixture), "cancelled commit preserves original file");
            Assert(Directory.GetFiles(root, "segatools.ini.tmp-*").Length == 0 && Directory.GetFiles(root, "segatools.ini.bak-*").Length == 0, "cancelled commit leaves no temporary or backup file");
            string originalHash = SetupActions.InspectGameConfig(cfg.GameIniPath).Hash;
            byte[] externalFixture = originalFixture.Concat(Encoding.UTF8.GetBytes("; external change\r\n")).ToArray();
            File.WriteAllBytes(cfg.GameIniPath, externalFixture);
            RejectOperation(() => SetupActions.ApplyConfig(cfg, cfg.GameIniPath, delegate { }, originalHash), "checked apply rejects a changed file after detection");
            Assert(File.ReadAllBytes(cfg.GameIniPath).SequenceEqual(externalFixture), "stale detection never overwrites external edits");
            File.WriteAllBytes(cfg.GameIniPath, originalFixture);
            guardChecks = 0;
            RejectOperation(() => SetupActions.ApplyConfig(cfg, cfg.GameIniPath, delegate {
                if (++guardChecks == 2)
                {
                    string replacement = Path.Combine(root, "external-replacement.ini");
                    File.WriteAllBytes(replacement, externalFixture);
                    File.Replace(replacement, cfg.GameIniPath, null);
                }
            }, originalHash), "checked apply rejects atomic replacement before commit");
            Assert(guardChecks == 2 && File.ReadAllBytes(cfg.GameIniPath).SequenceEqual(externalFixture), "pre-commit hash check preserves atomic external replacement");
            Assert(Directory.GetFiles(root, "segatools.ini.tmp-*").Length == 0 && Directory.GetFiles(root, "segatools.ini.bak-*").Length == 0, "failed hash checks leave no temporary or backup files");
            Reject(() => SetupActions.ApplyGameConfigChecked(cfg, null), "checked apply requires a detection hash");
            Reject(() => SetupActions.RestoreHostConfigChecked(cfg, "not-base64"), "checked restoration rejects malformed hash");
            Reject(() => SetupActions.ApplyGameConfigChecked(cfg, Convert.ToBase64String(new byte[1])), "checked apply rejects a non-SHA256 hash");
            File.WriteAllBytes(cfg.GameIniPath, originalFixture);
            if (!gameRunning)
            {
                byte[] before = File.ReadAllBytes(cfg.GameIniPath);
                SetupActions.ApplyGameConfigChecked(cfg, SetupActions.InspectGameConfig(cfg.GameIniPath).Hash);
                Assert(Directory.GetFiles(root, "segatools.ini.bak-*").Length == 0, "atomic apply creates no backup");
                Assert(File.ReadAllBytes(cfg.GameIniPath).SequenceEqual(Rewrite(before, cfg)), "atomic apply expected bytes");
                DateTime fixedTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(cfg.GameIniPath, fixedTime);
                SetupActions.ApplyGameConfigChecked(cfg, SetupActions.InspectGameConfig(cfg.GameIniPath).Hash);
                Assert(File.GetLastWriteTimeUtc(cfg.GameIniPath) == fixedTime, "unchanged apply does not rewrite file");
                var restoreProfile = new RoomConfig { RoomName = "", NetworkId = "", Slot = 4, GameIniPath = cfg.GameIniPath, Subnet = "192.168.139.0" };
                string result = SetupActions.RestoreHostConfigChecked(restoreProfile, SetupActions.InspectGameConfig(restoreProfile.GameIniPath).Hash);
                Assert(File.ReadAllBytes(cfg.GameIniPath).SequenceEqual(Restore(before, restoreProfile)), "restore host writes configuration without room credentials");
                Assert(result.Contains("基准机") && restoreProfile.Slot == 4 && restoreProfile.NetworkId == "", "restore host reports INI restoration and preserves profile");
                Assert(Directory.GetFiles(root, "segatools.ini.bak-*").Length == 0 && Directory.GetFiles(root, "segatools.ini.tmp-*").Length == 0, "apply and restore leave no backup or temporary file");
                File.SetLastWriteTimeUtc(cfg.GameIniPath, fixedTime);
                SetupActions.RestoreHostConfigChecked(restoreProfile, SetupActions.InspectGameConfig(restoreProfile.GameIniPath).Hash);
                Assert(File.GetLastWriteTimeUtc(cfg.GameIniPath) == fixedTime, "repeated host restoration does not rewrite file");
                string hostHash = SetupActions.InspectGameConfig(cfg.GameIniPath).Hash;
                SetupActions.ApplyGameConfigChecked(cfg, hostHash);
                var checkedClient = SetupActions.InspectGameConfig(cfg.GameIniPath);
                Assert(checkedClient.Role == GameConfigRole.Client && checkedClient.Slot == 4 && checkedClient.Hash != hostHash, "checked apply commits using matching detection hash");
                File.SetLastWriteTimeUtc(cfg.GameIniPath, fixedTime);
                SetupActions.ApplyGameConfigChecked(cfg, checkedClient.Hash);
                Assert(File.GetLastWriteTimeUtc(cfg.GameIniPath) == fixedTime, "matching checked apply preserves an already correct file");
                RejectOperation(() => SetupActions.ApplyGameConfigChecked(cfg, hostHash), "checked idempotent apply still rejects a stale hash");
                RejectOperation(() => SetupActions.RestoreHostConfigChecked(restoreProfile, hostHash), "public checked restoration rejects stale hash");
                Assert(SetupActions.InspectGameConfig(cfg.GameIniPath).Hash == checkedClient.Hash, "failed public checked writes preserve the detected client file");
                SetupActions.RestoreHostConfigChecked(restoreProfile, checkedClient.Hash);
                var checkedHost = SetupActions.InspectGameConfig(cfg.GameIniPath);
                Assert(checkedHost.Role == GameConfigRole.Host && checkedHost.Slot == 1 && restoreProfile.Slot == 4 && restoreProfile.NetworkId == "", "checked restoration works offline and preserves room profile");
                File.SetLastWriteTimeUtc(cfg.GameIniPath, fixedTime);
                SetupActions.RestoreHostConfigChecked(restoreProfile, checkedHost.Hash);
                Assert(File.GetLastWriteTimeUtc(cfg.GameIniPath) == fixedTime, "matching checked restoration preserves an already correct file");
                Assert(Directory.GetFiles(root, "segatools.ini.bak-*").Length == 0 && Directory.GetFiles(root, "segatools.ini.tmp-*").Length == 0, "checked public writes create no backups or temporary remnants");
            }
            else
            {
                RejectOperation(() => SetupActions.ApplyGameConfigChecked(cfg, originalHash), "public checked apply rejects running game");
                RejectOperation(() => SetupActions.RestoreHostConfigChecked(cfg, originalHash), "public checked restore rejects running game");
                Console.WriteLine("SKIP: fixture write while game is running.");
            }
            Console.WriteLine("ALL PASSED: " + passed + "; fixtures=" + root); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
