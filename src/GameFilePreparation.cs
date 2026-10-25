using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ChuniLanFour
{
    public enum FilePreparationState { Waiting, Working, Ready, Error }

    public sealed class GameFilePreparationException : Exception
    {
        public bool ConfigurationMayHaveChanged { get; private set; }
        public bool Standalone { get; private set; }
        public RoomConfig Target { get; private set; }
        internal GameFilePreparationException(string message, bool standalone, RoomConfig target, Exception inner)
            : base(message, inner)
        {
            ConfigurationMayHaveChanged = true;
            Standalone = standalone;
            Target = target;
        }
    }

    public sealed class GameFilesResult
    {
        public GameConfigInspection Config { get; internal set; }
        public GameLaunchInspection Launch { get; internal set; }
        public RoomConfig Target { get; internal set; }
        public bool Standalone { get; internal set; }
        public string IniHash { get; internal set; }
        public TestRoleInspection TestRole { get; internal set; }
        public string BinDirectory { get; internal set; }
        public bool IsComplete { get { return Config != null && Launch != null && TestRole != null && Target != null; } }
    }

    public static class GameFilePreparation
    {
        public static bool ResolveStandalone(GameConfigInspection detected, RoomConfig savedRoom, GameLaunchSettings preferences, string iniPath)
        {
            if (detected == null) throw new ArgumentNullException("detected");
            bool validRoom = ValidRoom(savedRoom);
            bool sameRecordedPath = preferences != null && SamePath(preferences.StandaloneIniPath, iniPath);
            if (detected.Role == GameConfigRole.Client) return false;
            if (detected.Role == GameConfigRole.Host)
            {
                if (preferences != null && preferences.StandaloneMode && sameRecordedPath &&
                    !String.IsNullOrEmpty(detected.Hash) && detected.Hash == preferences.StandaloneIniHash) return true;
                return !(validRoom && savedRoom.Slot == 1 && savedRoom.Subnet == detected.Subnet);
            }
            string recordedPath = preferences == null ? "" : String.IsNullOrWhiteSpace(preferences.IniPath) ? preferences.StandaloneIniPath : preferences.IniPath;
            if (preferences != null && SamePath(recordedPath, iniPath)) return preferences.StandaloneMode;
            return !(validRoom && (preferences == null || !preferences.StandaloneMode));
        }

        public static GameFilesResult InspectBin(RoomConfig savedRoom, string binPath, bool standalone, Action<string, FilePreparationState, string> progress)
        {
            return InspectBinCore(savedRoom, binPath, standalone, progress, GameLauncher.Inspect, GameTestRole.Inspect);
        }

        internal static GameFilesResult InspectBinCore(RoomConfig savedRoom, string binPath, bool standalone,
            Action<string, FilePreparationState, string> progress,
            Func<string, string, GameLaunchInspection> inspectLaunch, Func<string, TestRoleInspection> inspectTest)
        {
            return ScanBin(savedRoom, binPath, standalone, progress, inspectLaunch, inspectTest).Result;
        }

        public static GameFilesResult PrepareBin(RoomConfig savedRoom, string binPath, bool standalone, Action<string, FilePreparationState, string> progress)
        {
            return PrepareBinCore(savedRoom, binPath, standalone, progress, GameLauncher.Inspect, GameTestRole.Inspect,
                delegate(RoomConfig target, string hash, bool solo)
                {
                    return solo ? SetupActions.RestoreHostConfigChecked(target, hash) : SetupActions.ApplyGameConfigChecked(target, hash);
                }, GameTestRole.ApplyChecked,
                delegate { if (GameLauncher.IsGameRunning()) throw new InvalidOperationException("请先关闭游戏和 amdaemon，再保存配置。"); });
        }

        internal static GameFilesResult PrepareBinCore(RoomConfig savedRoom, string binPath, bool standalone,
            Action<string, FilePreparationState, string> progress,
            Func<string, string, GameLaunchInspection> inspectLaunch, Func<string, TestRoleInspection> inspectTest,
            Func<RoomConfig, string, bool, string> writeChecked, Func<TestRoleInspection, int, TestRoleInspection> writeTest, Action ensureStopped)
        {
            if (inspectLaunch == null || inspectTest == null || writeChecked == null || writeTest == null || ensureStopped == null) throw new ArgumentNullException("workflow");
            var scan = ScanBin(savedRoom, binPath, standalone, progress, inspectLaunch, inspectTest);
            if (!scan.Result.IsComplete)
                throw new InvalidOperationException("游戏文件检查未完成，请处理各项提示后重新保存。", scan.Errors.Count == 0 ? null : new AggregateException(scan.Errors));
            var result = scan.Result;
            string ini = result.Target.GameIniPath, stage = "ini";
            bool writeAttempted = false;
            try
            {
                result.Target = Target(savedRoom, result.Config, ini, standalone);
                stage = "bat";
                using (var script = LockLaunchScript(result.Launch.BatPath, result.Launch))
                {
                    stage = "ini";
                    ensureStopped();
                    LocalFile(ini);
                    var currentConfig = SetupActions.InspectGameConfig(ini);
                    if (currentConfig.Hash != result.Config.Hash) throw new InvalidOperationException("游戏配置已变化，请重新检查后保存。");
                    string roleSection;
                    // Validate the complete rewrite before either target file is changed.
                    SetupActions.RewriteGameConfig(File.ReadAllBytes(ini), result.Target, out roleSection);
                    stage = "test";
                    var currentTest = inspectTest(ini);
                    RequireSameTestSnapshot(result.TestRole, currentTest);
                    ensureStopped();
                    int targetRole = standalone || result.Target.Slot == 1 ? 0 : 1;
                    stage = "ini";
                    Report(progress, "ini", FilePreparationState.Working, "正在保存游戏配置…");
                    writeAttempted = true;
                    writeChecked(result.Target, result.Config.Hash, standalone);
                    var applied = SetupActions.InspectGameConfig(ini);
                    RequireTargetConfig(applied, result.Target);
                    stage = "test";
                    Report(progress, "test", FilePreparationState.Working, "正在保存…");
                    ensureStopped();
                    // The machine-state snapshot also binds the INI hash; refresh that binding after the INI commit.
                    var testBeforeWrite = inspectTest(ini);
                    RequireSameTestSnapshot(result.TestRole, testBeforeWrite);
                    var changedTest = writeTest(testBeforeWrite, targetRole);
                    var finalTest = inspectTest(ini);
                    if (changedTest == null || finalTest == null || finalTest.Role != targetRole || finalTest.Group != result.TestRole.Group ||
                        !SamePath(finalTest.SavePath, result.TestRole.SavePath) || !SamePath(finalTest.BinDirectory, result.BinDirectory) ||
                        finalTest.FileHash != changedTest.FileHash)
                        throw new IOException("机器状态写入校验失败，请重新保存配置。");
                    stage = "ini";
                    var finalConfig = SetupActions.InspectGameConfig(ini);
                    RequireTargetConfig(finalConfig, result.Target);
                    if (finalConfig.Hash != applied.Hash) throw new IOException("保存期间游戏配置发生变化，请重新检查后保存。");
                    result.Config = finalConfig; result.IniHash = finalConfig.Hash; result.TestRole = finalTest;
                    Report(progress, "ini", FilePreparationState.Ready, "配置已保存。\r\n" + finalConfig.Summary);
                    Report(progress, "bat", FilePreparationState.Ready, "兼容性检查完成");
                    Report(progress, "test", FilePreparationState.Ready, finalTest.RoleName + " · " + finalTest.GroupName + "（保存完成）");
                    return result;
                }
            }
            catch (Exception ex)
            {
                try { Report(progress, stage, FilePreparationState.Error, ex.Message); } catch { }
                if (writeAttempted) throw new GameFilePreparationException(ex.Message, standalone, result.Target, ex);
                throw;
            }
        }

        private sealed class BinScan
        {
            internal readonly GameFilesResult Result = new GameFilesResult();
            internal readonly List<Exception> Errors = new List<Exception>();
        }
        private static BinScan ScanBin(RoomConfig savedRoom, string binPath, bool standalone,
            Action<string, FilePreparationState, string> progress,
            Func<string, string, GameLaunchInspection> inspectLaunch, Func<string, TestRoleInspection> inspectTest)
        {
            if (inspectLaunch == null || inspectTest == null) throw new ArgumentNullException("workflow");
            var scan = new BinScan(); scan.Result.Standalone = standalone;
            string bin;
            try { bin = LocalDirectory(binPath); scan.Result.BinDirectory = bin; }
            catch (Exception ex)
            {
                foreach (string entry in new[] { "ini", "bat", "test" }) ReportFailure(scan, progress, entry, ex);
                return scan;
            }
            string ini = Path.Combine(bin, "segatools.ini"), bat = Path.Combine(bin, "start.bat");
            try
            {
                Report(progress, "ini", FilePreparationState.Working, "正在检测游戏配置…");
                LocalFile(ini); scan.Result.Config = SetupActions.InspectGameConfig(ini);
                scan.Result.IniHash = scan.Result.Config.Hash;
                Report(progress, "ini", FilePreparationState.Ready, scan.Result.Config.Summary + "\r\n" + scan.Result.Config.Description);
            }
            catch (Exception ex) { scan.Result.Config = null; ReportFailure(scan, progress, "ini", ex); }
            if (scan.Result.Config != null)
            {
                try { scan.Result.Target = Target(savedRoom, scan.Result.Config, ini, standalone || !ValidRoom(savedRoom)); }
                catch (Exception ex) { ReportFailure(scan, progress, "ini", ex); }
            }
            string subnet = scan.Result.Target != null ? scan.Result.Target.Subnet :
                savedRoom != null && ValidSubnet(savedRoom.Subnet) ? savedRoom.Subnet : "192.168.139.0";
            try
            {
                Report(progress, "bat", FilePreparationState.Working, "正在读取启动脚本…");
                LocalFile(bat); var launch = inspectLaunch(bat, subnet);
                if (launch == null || !SamePath(launch.BatPath, bat) || !SamePath(launch.GameDirectory, bin))
                    throw new InvalidOperationException("启动脚本的游戏目录不匹配。");
                using (var script = LockLaunchScript(bat, launch)) { }
                scan.Result.Launch = launch;
                Report(progress, "bat", FilePreparationState.Ready, "兼容性检查完成");
            }
            catch (Exception ex) { scan.Result.Launch = null; ReportFailure(scan, progress, "bat", ex); }
            try
            {
                Report(progress, "test", FilePreparationState.Working, "正在读取…");
                var test = inspectTest(ini);
                if (test == null || !SamePath(test.BinDirectory, bin)) throw new InvalidOperationException("机器状态存档的游戏目录不匹配。");
                scan.Result.TestRole = test;
                Report(progress, "test", FilePreparationState.Ready, test.RoleName + " · " + test.GroupName);
            }
            catch (Exception ex) { scan.Result.TestRole = null; ReportFailure(scan, progress, "test", ex); }
            return scan;
        }
        private static void ReportFailure(BinScan scan, Action<string, FilePreparationState, string> progress, string entry, Exception error)
        {
            scan.Errors.Add(error);
            try { Report(progress, entry, FilePreparationState.Error, error.Message); } catch { }
        }
        private static void RequireSameTestSnapshot(TestRoleInspection expected, TestRoleInspection current)
        {
            if (current == null || !SamePath(current.SavePath, expected.SavePath) || !SamePath(current.BinDirectory, expected.BinDirectory) ||
                current.FileHash != expected.FileHash || current.Role != expected.Role || current.Group != expected.Group)
                throw new InvalidOperationException("机器状态存档已变化，请重新检查后保存。");
        }
        private static void RequireTargetConfig(GameConfigInspection applied, RoomConfig target)
        {
            if (applied == null || applied.Role != (target.Slot == 1 ? GameConfigRole.Host : GameConfigRole.Client) || applied.Slot != target.Slot || applied.Subnet != target.Subnet)
                throw new IOException("写入后的游戏配置与目标机号不一致，请重新保存。");
        }
        private static FileStream LockLaunchScript(string bat, GameLaunchInspection launch)
        {
            var script = new FileStream(bat, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (script.Length > 1048576) throw new InvalidOperationException("BAT 文件超过 1 MB。");
                byte[] bytes = new byte[(int)script.Length]; int count = 0;
                while (count < bytes.Length)
                {
                    int read = script.Read(bytes, count, bytes.Length - count);
                    if (read == 0) throw new IOException("启动脚本读取不完整。");
                    count += read;
                }
                if (GameLauncher.Hash(bytes) != launch.BatHash) throw new InvalidOperationException("启动脚本已变化，请重新选择文件。");
                return script;
            }
            catch { script.Dispose(); throw; }
        }

        private static RoomConfig Target(RoomConfig savedRoom, GameConfigInspection detected, string ini, bool standalone)
        {
            if (!standalone)
            {
                if (savedRoom == null) throw new InvalidOperationException("请先保存房间网络与机号。");
                savedRoom.Validate();
            }
            string subnet = standalone ? ValidSubnet(detected.Subnet) ? detected.Subnet :
                savedRoom != null && ValidSubnet(savedRoom.Subnet) ? savedRoom.Subnet : "192.168.139.0" : savedRoom.Subnet;
            return new RoomConfig
            {
                RoomName = savedRoom == null ? "CHUNITHM 联机房间" : savedRoom.RoomName,
                NetworkId = savedRoom == null ? "" : savedRoom.NetworkId,
                Subnet = subnet,
                Slot = standalone ? 1 : savedRoom.Slot,
                GameIniPath = ini
            };
        }
        private static void Report(Action<string, FilePreparationState, string> progress, string target, FilePreparationState state, string message)
        { if (progress != null) progress(target, state, message); }
        private static bool ValidRoom(RoomConfig value)
        {
            if (value == null) return false;
            try { value.Validate(); return true; }
            catch (ArgumentException) { return false; }
        }
        private static bool ValidSubnet(string value)
        {
            if (!Regex.IsMatch(value ?? "", @"\A192\.168\.(0|[1-9][0-9]{0,2})\.0\z")) return false;
            return Int32.Parse(value.Split('.')[2]) <= 255;
        }
        private static bool SamePath(string first, string second)
        {
            if (String.IsNullOrWhiteSpace(first) || String.IsNullOrWhiteSpace(second)) return false;
            try { return String.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) { if (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) return false; throw; }
        }
        private static string LocalFile(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidOperationException("请选择本地游戏文件。");
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("游戏文件不能使用符号链接。");
            LocalDirectory(Path.GetDirectoryName(full));
            return full;
        }
        private static string LocalDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择游戏 bin 文件夹。");
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidOperationException("请选择本地游戏文件夹。");
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("未找到游戏 bin 文件夹。");
            for (var parent = new DirectoryInfo(full); parent != null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("游戏目录不能使用符号链接或目录联接。");
            if (full.Length > Path.GetPathRoot(full).Length) full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full;
        }
    }
}
