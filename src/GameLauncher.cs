using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace ChuniLanFour
{
    public sealed class GameLaunchInspection
    {
        public string BatPath { get; internal set; }
        public string GameDirectory { get; internal set; }
        public string BatHash { get; internal set; }
        public string Summary { get; internal set; }
        public bool CanUseConnect { get; internal set; }
        public string ConnectReason { get; internal set; }
        public bool CanUseDistribution { get; internal set; }
        public string DistributionReason { get; internal set; }
        internal string Subnet, Text;
        internal Encoding Encoding;
        internal List<LaunchFile> Files;
        internal List<LaunchCommand> ConnectCommands, DaemonCommands;
    }
    public sealed class PreparedGameLaunch
    {
        public string BatPath { get; internal set; }
        public string GameDirectory { get; internal set; }
        public string Summary { get; internal set; }
        internal List<LaunchFile> Files;
    }
    internal sealed class LaunchFile { internal string Path, Hash; }
    internal sealed class LaunchToken { internal string Value; internal int Start, End; internal bool Quoted; }
    internal sealed class LaunchCommand
    {
        internal int InsertAt;
        internal List<string> Modules = new List<string>();
    }
    public sealed class GameLaunchSettings
    {
        public string BatPath { get; set; }
        public string IniPath { get; set; }
        public string BinDirectory { get; set; }
        public string PreparedIniHash { get; set; }
        public bool UseConnect { get; set; }
        public bool UseDistribution { get; set; }
        public bool StandaloneMode { get; set; }
        public string StandaloneIniPath { get; set; }
        public string StandaloneSubnet { get; set; }
        public string StandaloneIniHash { get; set; }
        public GameLaunchSettings() { BatPath = IniPath = BinDirectory = PreparedIniHash = StandaloneIniPath = StandaloneSubnet = StandaloneIniHash = ""; }
        private static string LocalPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChuniLanFour", "game-launch.json"); } }
        public static GameLaunchSettings Load() { return Load(LocalPath); }
        public void Save() { Save(LocalPath); }
        internal static GameLaunchSettings Load(string path)
        {
            if (!File.Exists(path)) return new GameLaunchSettings();
            if (new FileInfo(path).Length > 32768) throw new InvalidDataException("本机游戏启动设置文件过大。");
            var result = new JavaScriptSerializer().Deserialize<GameLaunchSettings>(File.ReadAllText(path, Encoding.UTF8));
            if (result == null) throw new InvalidDataException("本机游戏启动设置格式无效。");
            result.BatPath = result.BatPath ?? "";
            result.IniPath = result.IniPath ?? "";
            result.BinDirectory = result.BinDirectory ?? "";
            result.PreparedIniHash = result.PreparedIniHash ?? "";
            result.StandaloneIniPath = result.StandaloneIniPath ?? "";
            result.StandaloneSubnet = result.StandaloneSubnet ?? "";
            result.StandaloneIniHash = result.StandaloneIniHash ?? "";
            return result;
        }
        internal void Save(string path)
        {
            string full = Path.GetFullPath(path), dir = Path.GetDirectoryName(full);
            Directory.CreateDirectory(dir); string temp = Path.Combine(dir, ".game-launch-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
                if (File.Exists(full)) File.Replace(temp, full, null); else File.Move(temp, full);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public static class GameLauncher
    {
        internal const string ConnectDllHash = "1312E4D4DE3B35E60FF2410C944AF828CAD24A7A04E33172F6483606754AFAD7";
        internal const string DistributionDllHash = "D4C4A57652FB6F4FDAE12FE3736A58A63E1DF5F42B0F38BA89877B64C3D4AB10";
        internal const string DaemonHash = "014992A5AF1B62B5FD42D2CB0C19A38ABA4B5437A3EF82D304DE2C93065ED70D";
        internal const string AmHookHash = "993962945DAD215C510069B0FB80A55695AAA19D964E0C6F55C231873358AB5B";
        private static readonly string[] DerivedMarkers = { "CHUNI_CONNECT_MODE", "connect_compat.dll", "daemon_registry_fix.dll", "CHUNI_LAN_MANAGED", "chuni-connect-v2-", "chuni-connect-trial-" };
        private static InvalidOperationException Error(string text) { return new InvalidOperationException(text); }
        internal static string Hash(byte[] data) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", ""); }
        private static string Hash(Stream stream) { stream.Position = 0; using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
        private static string SafePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[] { '\0', '\r', '\n', '"', '%', '!' }) >= 0) throw Error("路径不能为空或含有 %、!、引号、换行。");
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.Length > 200) throw Error("请选择长度不超过 200 字符的本地路径。");
            return full;
        }
        private static byte[] Capture(string path, List<LaunchFile> files)
        {
            path = SafePath(path); byte[] data;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length > 536870912) throw Error("文件超过 512 MB：" + Path.GetFileName(path));
                using (var memory = new MemoryStream()) { file.CopyTo(memory); data = memory.ToArray(); }
            }
            var old = files.FirstOrDefault(x => String.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            string hash = Hash(data);
            if (old == null) files.Add(new LaunchFile { Path = path, Hash = hash });
            else if (old.Hash != hash) throw Error("检查期间文件发生变化，请重新读取 BAT。");
            return data;
        }
        private static Encoding Decode(byte[] raw, out string text)
        {
            if (raw.Length > 1048576) throw Error("BAT 文件超过 1 MB。");
            if (raw.Length >= 2 && ((raw[0] == 255 && raw[1] == 254) || (raw[0] == 254 && raw[1] == 255))) throw Error("BAT 编码须为 UTF-8 或 GBK。");
            if (raw.Length >= 3 && raw[0] == 239 && raw[1] == 187 && raw[2] == 191) { var e = new UTF8Encoding(true, true); text = e.GetString(raw, 3, raw.Length - 3); return e; }
            try { var e = new UTF8Encoding(false, true); text = e.GetString(raw); return e; }
            catch (DecoderFallbackException) { var e = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); text = e.GetString(raw); return e; }
        }
        public static GameLaunchInspection Inspect(string batPath, string subnet)
        {
            string full = SafePath(batPath);
            if (!String.Equals(Path.GetExtension(full), ".bat", StringComparison.OrdinalIgnoreCase)) throw Error("请选择 .bat 原始启动文件。");
            var files = new List<LaunchFile>(); var bytes = Capture(full, files); string text; Encoding encoding = Decode(bytes, out text);
            if (text.IndexOf('\0') >= 0) throw Error("BAT 含空字符，无法读取。");
            foreach (string marker in DerivedMarkers) if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) throw Error("所选文件已包含联机补丁，请选择原始 start.bat。");
            var info = new GameLaunchInspection { BatPath = full, GameDirectory = Path.GetDirectoryName(full), BatHash = Hash(bytes), Text = text, Encoding = encoding, Subnet = subnet ?? "", Files = files };
            try
            {
                if (subnet != "192.168.139.0") throw Error("连接补丁适用于 192.168.139.0/24 网段。");
                info.ConnectCommands = ParseCommands(text, info.GameDirectory, false);
                VerifyMachine(Capture(Path.Combine(info.GameDirectory, "inject_x86.exe"), files), 0x14c);
                ValidateConnectImage(Capture(Path.Combine(info.GameDirectory, "chusanApp.exe"), files));
                CaptureModules(info.ConnectCommands, files);
                info.CanUseConnect = true; info.ConnectReason = "支持设置同步与店内匹配。";
            }
            catch (Exception ex) { info.CanUseConnect = false; info.ConnectReason = ex.Message; }
            try
            {
                info.DaemonCommands = ParseCommands(text, info.GameDirectory, true);
                VerifyMachine(Capture(Path.Combine(info.GameDirectory, "inject_x64.exe"), files), 0x8664);
                var daemon = Capture(Path.Combine(info.GameDirectory, "amdaemon.exe"), files);
                var hook = Capture(Path.Combine(info.GameDirectory, "chusanamhook.dll"), files);
                if (Hash(daemon) != DaemonHash || Hash(hook) != AmHookHash) throw Error("amdaemon 或 chusanamhook 版本与配信补丁不匹配。");
                string expected = Path.Combine(info.GameDirectory, "chusanamhook.dll");
                foreach (var command in info.DaemonCommands)
                    if (command.Modules.Count(x => String.Equals(x, expected, StringComparison.OrdinalIgnoreCase)) != 1) throw Error("每条 amdaemon 启动命令必须且只能加载一次游戏目录内的 chusanamhook.dll。");
                CaptureModules(info.DaemonCommands, files);
                info.CanUseDistribution = true; info.DistributionReason = "支持当前配信服务版本。";
            }
            catch (Exception ex) { info.CanUseDistribution = false; info.DistributionReason = ex.Message; }
            info.Summary = "启动脚本：" + info.BatPath + "\r\n修正连接等待：" + info.ConnectReason + "\r\n配信启动修复：" + info.DistributionReason;
            return info;
        }
        private static void CaptureModules(List<LaunchCommand> commands, List<LaunchFile> files)
        { foreach (var module in commands.SelectMany(x => x.Modules).Distinct(StringComparer.OrdinalIgnoreCase)) Capture(module, files); }
        private static bool IsIgnored(string line)
        {
            if (Regex.IsMatch(line, @"^\s*(?:@?rem(?:\s|$)|::)", RegexOptions.IgnoreCase)) return true;
            if (!Regex.IsMatch(line, @"^\s*@?echo(?:\s|\.|$)", RegexOptions.IgnoreCase)) return false;
            bool quotes = false;
            foreach (char c in line) { if (c == '"') quotes = !quotes; else if (!quotes && "&|<>".IndexOf(c) >= 0) return false; }
            return true;
        }
        private static List<LaunchToken> Tokens(string line)
        {
            var result = new List<LaunchToken>(); int pos = 0;
            while (pos < line.Length)
            {
                while (pos < line.Length && Char.IsWhiteSpace(line[pos])) pos++;
                if (pos == line.Length) break; int start = pos; bool quoted = false, inQuotes = false; var value = new StringBuilder();
                while (pos < line.Length)
                {
                    char c = line[pos];
                    if (c == '"') { inQuotes = !inQuotes; quoted = true; pos++; continue; }
                    if (!inQuotes && Char.IsWhiteSpace(c)) break;
                    if (c == '^' || c == '!' || (!inQuotes && "&|<>()".IndexOf(c) >= 0)) throw Error("注入命令含组合、重定向、续行或延迟展开，不能可靠自动改写。");
                    value.Append(c); pos++;
                }
                if (inQuotes) throw Error("注入命令的引号没有闭合。");
                result.Add(new LaunchToken { Value = value.ToString(), Start = start, End = pos, Quoted = quoted });
            }
            return result;
        }
        private static bool LocalExecutable(string value, string name)
        { if (value.StartsWith("@", StringComparison.Ordinal)) value = value.Substring(1); if (value.StartsWith("%~dp0", StringComparison.OrdinalIgnoreCase)) value = value.Substring(5); return String.Equals(value, name, StringComparison.OrdinalIgnoreCase); }
        private static string ModulePath(string directory, string token)
        {
            if (token.StartsWith("%~dp0", StringComparison.OrdinalIgnoreCase)) token = Path.Combine(directory, token.Substring(5));
            if (token.IndexOf('%') >= 0) throw Error("注入模块路径含无法静态确认的变量，不能自动改写。");
            return SafePath(Path.IsPathRooted(token) ? token : Path.Combine(directory, token));
        }
        private static List<LaunchCommand> ParseCommands(string text, string directory, bool daemon)
        {
            var result = new List<LaunchCommand>(); string injector = daemon ? "inject_x64.exe" : "inject_x86.exe", executable = daemon ? "amdaemon.exe" : "chusanApp.exe";
            foreach (Match lineMatch in Regex.Matches(text, @"[^\r\n]+"))
            {
                string line = lineMatch.Value; if (IsIgnored(line)) continue;
                if (line.TrimEnd().EndsWith("^", StringComparison.Ordinal)) throw Error("BAT 使用续行命令，暂不支持自动加入补丁。");
                if (Regex.IsMatch(line, @"^\s*@?(?:pushd|cd)(?:\s|$)", RegexOptions.IgnoreCase) && !Regex.IsMatch(line, @"^\s*@?(?:pushd|cd\s+/d)\s+(?:%~dp0|""%~dp0"")\s*$", RegexOptions.IgnoreCase)) throw Error("BAT 会切换到另一个工作目录，无法保证检查的游戏就是实际启动的游戏。");
                if (Regex.IsMatch(line, @"^\s*@?[a-z]:\s*$", RegexOptions.IgnoreCase)) throw Error("BAT 含独立切换盘符，无法可靠确认实际游戏目录。");
                if (line.IndexOf(injector, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var tokens = Tokens(line); int at = 0; bool start = false, hasTitle = false;
                if (daemon && tokens.Count > 0 && String.Equals(tokens[0].Value.TrimStart('@'), "start", StringComparison.OrdinalIgnoreCase))
                {
                    start = true; at++;
                    while (at < tokens.Count)
                    {
                        if (tokens[at].Quoted && tokens[at].Value == "" && !hasTitle) { hasTitle = true; at++; }
                        else if (String.Equals(tokens[at].Value, "/min", StringComparison.OrdinalIgnoreCase)) at++;
                        else break;
                    }
                }
                if (at >= tokens.Count || !LocalExecutable(tokens[at].Value, injector)) throw Error("发现不支持的 " + injector + " 启动格式；只支持独立直接命令及标准 start /min 守护进程命令。");
                if (start && tokens[at].Quoted && !hasTitle) throw Error("带引号的 start 命令缺少空窗口标题，不能可靠识别。");
                if (tokens.Any(x => x.Value.TrimStart('@').StartsWith("%~dp0", StringComparison.OrdinalIgnoreCase) && !x.Quoted)) throw Error("注入命令中的 %~dp0 路径必须加双引号，避免中文、空格或 & 路径被错误解释。");
                at++; var command = new LaunchCommand();
                while (at < tokens.Count && !LocalExecutable(tokens[at].Value, executable))
                {
                    if (String.Equals(tokens[at].Value, "-d", StringComparison.OrdinalIgnoreCase)) { at++; continue; }
                    if (String.Equals(tokens[at].Value, "-k", StringComparison.OrdinalIgnoreCase) && at + 1 < tokens.Count)
                    { command.Modules.Add(ModulePath(directory, tokens[at + 1].Value)); at += 2; continue; }
                    throw Error("注入参数格式不能可靠识别：" + injector);
                }
                if (at == tokens.Count) throw Error("注入命令没有找到 " + executable + "。");
                command.InsertAt = lineMatch.Index + tokens[at].Start; at++;
                if (!daemon && at != tokens.Count) throw Error("游戏注入命令末尾还有未知参数，不能自动改写。");
                if (daemon)
                {
                    if (at < tokens.Count && tokens[at].Value == "-f") at++;
                    if (at >= tokens.Count || tokens[at].Value != "-c" || at + 1 >= tokens.Count) throw Error("amdaemon 需使用标准 -f -c 配置文件启动格式。");
                    for (at++; at < tokens.Count; at++)
                    {
                        string value = tokens[at].Value;
                        if (!value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || value.StartsWith("-", StringComparison.Ordinal) || value.IndexOf('%') >= 0) throw Error("amdaemon 配置参数格式无法可靠识别。");
                    }
                }
                result.Add(command);
            }
            if (result.Count == 0) throw Error("没有找到支持的 " + injector + " → " + executable + " 启动命令。");
            return result;
        }
        private static byte[] Resource(string name, string expected)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) throw Error("助手缺少嵌入模块：" + name);
                using (var memory = new MemoryStream()) { stream.CopyTo(memory); byte[] data = memory.ToArray(); if (Hash(data) != expected) throw Error("助手中的补丁校验失败，请重新获取完整助手。"); return data; }
            }
        }
        internal static string Transform(GameLaunchInspection info, string folder, bool connect, bool distribution)
        {
            if (!Regex.IsMatch(folder, @"\Achuni-lan-launch-[0-9a-f]{12}\z")) throw Error("受管目录名无效。");
            var additions = new List<KeyValuePair<int, string>>();
            if (connect) foreach (var command in info.ConnectCommands) additions.Add(new KeyValuePair<int, string>(command.InsertAt, "-k \"%~dp0" + folder + "\\connect_compat.dll\" "));
            if (distribution) foreach (var command in info.DaemonCommands) additions.Add(new KeyValuePair<int, string>(command.InsertAt, "-k \"%~dp0" + folder + "\\daemon_registry_fix.dll\" "));
            var result = new StringBuilder(info.Text); foreach (var item in additions.OrderByDescending(x => x.Key)) result.Insert(item.Key, item.Value);
            // This standard cwd line otherwise misinterprets an ampersand in %~dp0.
            string body = Regex.Replace(result.ToString(), @"(?im)^([ \t]*(?:@?pushd|@?cd[ \t]+/d)[ \t]+)%~dp0([ \t]*)(\r?)$", "$1\"%~dp0\"$2$3");
            string nl = info.Text.Contains("\r\n") ? "\r\n" : "\n";
            return "@echo off" + nl + "setlocal DisableDelayedExpansion" + nl + "rem CHUNI_LAN_MANAGED" + nl + (connect ? "set \"CHUNI_CONNECT_MODE=compat\"" + nl : "") + body;
        }
        public static PreparedGameLaunch Prepare(GameLaunchInspection inspected, bool connect, bool distribution, int slot, string subnet)
        { return PrepareCore(inspected, connect, distribution, slot, subnet, IsGameRunning); }
        internal static PreparedGameLaunch PrepareCore(GameLaunchInspection inspected, bool connect, bool distribution, int slot, string subnet, Func<bool> running, string gateName = null)
        {
            if (inspected == null || inspected.Files == null) throw Error("请先读取原始 BAT。");
            if (slot < 1 || slot > 4) throw Error("本机编号必须是 1 至 4。");
            if (subnet != inspected.Subnet) throw Error("房间网段已变化，请重新读取 BAT。");
            if (connect && !inspected.CanUseConnect) throw Error(inspected.ConnectReason);
            if (distribution && slot != 1) throw Error("配信启动修复适用于 1 号基准机。");
            if (distribution && !inspected.CanUseDistribution) throw Error(inspected.DistributionReason);
            using (var gate = AcquireLaunchGate(gateName))
            using (var locked = LockFiles(inspected.Files))
            {
                RefuseRunning(running);
                byte[] connectDll = connect ? Resource("connect_compat.dll", ConnectDllHash) : null;
                byte[] distributionDll = distribution ? Resource("daemon_registry_fix.dll", DistributionDllHash) : null;
                string id = Guid.NewGuid().ToString("N").Substring(0, 12), folder = "chuni-lan-launch-" + id;
                string directory = Path.Combine(inspected.GameDirectory, folder), bat = Path.Combine(inspected.GameDirectory, "start-chuni-lan-" + id + ".bat");
                if (directory.Length + 60 >= 260 || bat.Length > 200 || directory.Length + 24 > 200) throw Error("游戏目录路径过长，请缩短路径。");
                if (File.Exists(bat) || Directory.Exists(directory)) throw Error("生成路径已存在，请重试。");
                string output = Transform(inspected, folder, connect, distribution); var files = CloneFiles(inspected.Files);
                CleanPreviousLaunches(inspected.GameDirectory, inspected.BatPath, running);
                RefuseRunning(running);
                if (!connect && !distribution) return new PreparedGameLaunch { BatPath = inspected.BatPath, GameDirectory = inspected.GameDirectory, Summary = "未启用补丁。", Files = files };
                Directory.CreateDirectory(directory);
                if (connect) WriteNew(Path.Combine(directory, "connect_compat.dll"), connectDll, files);
                if (distribution) WriteNew(Path.Combine(directory, "daemon_registry_fix.dll"), distributionDll, files);
                var record = new LaunchRecord { Format = "CHUNITHM-LAN-1", Entry = Path.GetFileName(bat), Source = inspected.BatPath };
                WriteNew(Path.Combine(directory, "launch.json"), new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(record)), files);
                byte[] content = inspected.Encoding.GetPreamble().Concat(inspected.Encoding.GetBytes(output)).ToArray(); WriteNew(bat, content, files);
                string summary = "修正连接等待：" + (connect ? "开启" : "关闭") + "；配信启动修复：" + (distribution ? "开启" : "关闭") + "。\r\n运行目录：" + directory;
                return new PreparedGameLaunch { BatPath = bat, GameDirectory = inspected.GameDirectory, Summary = summary, Files = files };
            }
        }
        private sealed class LaunchRecord { public string Format { get; set; } public string Entry { get; set; } public string Source { get; set; } }
        private sealed class CleanupFile { internal string Path, Hash; }
        private sealed class CleanupPlan
        {
            internal readonly List<CleanupFile> Files = new List<CleanupFile>();
            internal readonly List<string> Directories = new List<string>();
        }
        private static void RefuseRunning(Func<bool> running)
        { if (running()) throw Error("请先关闭游戏和 amdaemon。"); }
        private static void VerifyDirectory(string directory)
        {
            string full = Path.GetFullPath(directory);
            for (var current = new DirectoryInfo(full); current != null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw Error("启动目录不能使用符号链接或目录联接。");
        }
        private static void VerifyChild(string root, string child)
        {
            if (!String.Equals(Path.GetDirectoryName(Path.GetFullPath(child)), root, StringComparison.OrdinalIgnoreCase)) throw Error("启动文件路径无效。");
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw Error("旧启动文件包含链接，请移除该链接后重试：" + Path.GetFileName(child));
        }
        private static bool IsManagedBat(string path, string folder)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1048576) return false;
            string text; Decode(File.ReadAllBytes(path), out text);
            return Regex.IsMatch(text, @"(?im)^rem CHUNI_LAN_MANAGED(?: - original BAT retained)?\r?$") &&
                Regex.IsMatch(text, "-k \\\"%~dp0" + Regex.Escape(folder) + @"\\(?:connect_compat|daemon_registry_fix)\.dll"" ", RegexOptions.IgnoreCase);
        }
        private static bool HasLaunchRecord(string path, string entry)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 32768) return false;
            try
            {
                var record = new JavaScriptSerializer().Deserialize<LaunchRecord>(File.ReadAllText(path, Encoding.UTF8));
                return record != null && record.Format == "CHUNITHM-LAN-1" && record.Entry == entry &&
                    String.Equals(Path.GetDirectoryName(Path.GetFullPath(record.Source)), Path.GetDirectoryName(Path.GetDirectoryName(path)), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { if (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException) return false; throw; }
        }
        private static bool KnownModule(string path)
        {
            string name = Path.GetFileName(path), expected = String.Equals(name, "connect_compat.dll", StringComparison.OrdinalIgnoreCase) ? ConnectDllHash : String.Equals(name, "daemon_registry_fix.dll", StringComparison.OrdinalIgnoreCase) ? DistributionDllHash : null;
            if (expected == null || !File.Exists(path) || new FileInfo(path).Length > 1048576) return false;
            return Hash(File.ReadAllBytes(path)) == expected;
        }
        private static void AddCleanupFile(CleanupPlan plan, string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                plan.Files.Add(new CleanupFile { Path = path, Hash = Hash(stream) });
        }
        private static void CleanPreviousLaunches(string directory, string sourceBat, Func<bool> running)
        {
            string root = Path.GetFullPath(directory);
            if (root.Length > Path.GetPathRoot(root).Length) root = root.TrimEnd(Path.DirectorySeparatorChar);
            VerifyDirectory(root); var plan = new CleanupPlan();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFileSystemEntries(root))
            {
                string name = Path.GetFileName(path);
                var match = Regex.Match(name, @"\A(?:start-chuni-lan-([0-9a-f]{12})\.bat|chuni-lan-launch-([0-9a-f]{12}))\z", RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                VerifyChild(root, path); ids.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
            }
            foreach (string id in ids)
            {
                string folder = "chuni-lan-launch-" + id, folderPath = Path.Combine(root, folder);
                string entry = "start-chuni-lan-" + id + ".bat", entryPath = Path.Combine(root, entry);
                bool ownsBat = File.Exists(entryPath) && !String.Equals(entryPath, sourceBat, StringComparison.OrdinalIgnoreCase) && IsManagedBat(entryPath, folder);
                bool ownsDirectory = false;
                string[] children = new string[0];
                if (Directory.Exists(folderPath))
                {
                    VerifyChild(root, folderPath); children = Directory.GetFileSystemEntries(folderPath);
                    foreach (string child in children) VerifyChild(folderPath, child);
                    ownsDirectory = ownsBat || HasLaunchRecord(Path.Combine(folderPath, "launch.json"), entry) || children.Any(KnownModule);
                }
                if (ownsBat) AddCleanupFile(plan, entryPath);
                if (!ownsDirectory) continue;
                foreach (string child in children)
                {
                    if (Directory.Exists(child)) continue;
                    string name = Path.GetFileName(child);
                    bool generatedLog = Regex.IsMatch(name, @"\A(?:connect-[0-9]{8}-[0-9]{6}-pid[0-9]+|daemon-registry-fix-[0-9]+)\.log\z", RegexOptions.IgnoreCase);
                    if (KnownModule(child) || generatedLog || (name == "launch.json" && HasLaunchRecord(child, entry))) AddCleanupFile(plan, child);
                }
                plan.Directories.Add(folderPath);
            }
            RefuseRunning(running);
            foreach (var item in plan.Files)
            {
                RefuseRunning(running); VerifyDirectory(Path.GetDirectoryName(item.Path));
                VerifyChild(Path.GetDirectoryName(item.Path), item.Path);
                using (var file = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (Hash(file) != item.Hash) throw Error("旧启动文件已变化，请重试。");
                    File.Delete(item.Path);
                }
            }
            foreach (string path in plan.Directories)
            {
                RefuseRunning(running); VerifyDirectory(path);
                if (Directory.GetFileSystemEntries(path).Length == 0) Directory.Delete(path, false);
            }
        }
        private static List<LaunchFile> CloneFiles(List<LaunchFile> files) { return files.Select(x => new LaunchFile { Path = x.Path, Hash = x.Hash }).ToList(); }
        private static void WriteNew(string path, byte[] data, List<LaunchFile> files)
        { using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(data, 0, data.Length); files.Add(new LaunchFile { Path = path, Hash = Hash(data) }); }
        private sealed class FileLease : IDisposable
        {
            internal readonly List<FileStream> Streams = new List<FileStream>(); internal Semaphore Gate;
            public void Dispose() { foreach (var stream in Streams) stream.Dispose(); Streams.Clear(); if (Gate != null) { Gate.Release(); Gate.Dispose(); Gate = null; } }
        }
        private static FileLease AcquireLaunchGate(string name)
        {
            var gate = new Semaphore(1, 1, name ?? @"Local\CHUNITHM_LAN_GameLaunch");
            if (!gate.WaitOne(0)) { gate.Dispose(); throw Error("游戏启动入口正在运行。"); }
            return new FileLease { Gate = gate };
        }
        private static FileLease LockFiles(List<LaunchFile> files)
        {
            var lease = new FileLease();
            try
            {
                foreach (var item in files)
                {
                    var file = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read); lease.Streams.Add(file);
                    if (Hash(file) != item.Hash) throw Error("读取后文件已被修改，请重新读取 BAT：" + Path.GetFileName(item.Path));
                }
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        public static bool IsGameRunning()
        {
            foreach (string name in new[] { "chusanApp", "amdaemon" })
            {
                var processes = Process.GetProcessesByName(name); bool found = processes.Length > 0;
                foreach (var process in processes) process.Dispose(); if (found) return true;
            }
            return false;
        }
        public static Process Start(PreparedGameLaunch prepared)
        {
            return StartCore(prepared, IsGameRunning, Process.Start, delegate(Process process, IDisposable lease)
            {
                // Capture an independent wait handle before returning. UI disposal of the
                // returned Process can then never end the file lease or crash this worker.
                Process monitor;
                try { monitor = Process.GetProcessById(process.Id); }
                catch (ArgumentException) { lease.Dispose(); process.Dispose(); return; }
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { monitor.WaitForExit(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    finally { monitor.Dispose(); process.Dispose(); lease.Dispose(); }
                });
            });
        }
        internal static Process StartCore(PreparedGameLaunch prepared, Func<bool> running, Func<ProcessStartInfo, Process> starter, Action<Process, IDisposable> keepLease, string gateName = null)
        {
            if (prepared == null || prepared.Files == null) throw Error("请先准备游戏启动入口。");
            var gate = new Semaphore(1, 1, gateName ?? @"Local\CHUNITHM_LAN_GameLaunch");
            if (!gate.WaitOne(0)) { gate.Dispose(); throw Error("游戏启动入口正在运行。"); }
            FileLease lease = null;
            try
            {
                if (running()) throw Error("请先关闭游戏和 amdaemon。");
                lease = LockFiles(prepared.Files); lease.Gate = gate;
                string bat = SafePath(prepared.BatPath), directory = SafePath(prepared.GameDirectory);
                if (!prepared.Files.Any(x => String.Equals(x.Path, bat, StringComparison.OrdinalIgnoreCase))) throw Error("启动入口不在已校验文件中，请重新准备。");
                var start = BuildStartInfo(bat, directory);
                if (running()) throw Error("准备启动时检测到游戏已运行，本次启动已取消。");
                Process process = starter(start); if (process == null) throw Error("Windows 没有返回游戏启动进程。");
                // Read-only handles stay open until the original launcher exits, closing the
                // validation/use gap for the BAT, executable and injected modules.
                keepLease(process, lease); lease = null; gate = null; return process;
            }
            catch { if (lease != null) { lease.Dispose(); gate = null; } if (gate != null) { gate.Release(); gate.Dispose(); } throw; }
        }
        internal static ProcessStartInfo BuildStartInfo(string bat, string directory)
        {
            bat = SafePath(bat); directory = SafePath(directory);
            return new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Arguments = "/d /s /v:off /c \"\"" + bat + "\"\"", WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = false };
        }
        private static void VerifyMachine(byte[] data, ushort machine) { var pe = new LaunchPe(data); if (pe.Machine != machine) throw Error("启动程序位数不匹配。"); }
        internal static readonly byte[] ConnectSignature = {
            0x8d,0x45,0xd4,0xc7,0x45,0xd4,0x01,0,0,0,0x50,0x68,0x7e,0x66,0x04,0x80,0xff,0x76,0x1c,0xff,0x15,0xf4,0xab,0x21,0x02,0x6a,0x10,0x8d,0x86,0x04,0x01,0,0,0x50,0xff,0x76,0x1c,0xff,0x15,0x04,0xac,0x21,0x02,0x83,0xf8,0xff,0x75,0x5c,0xff,0x15,0x38,0xac,0x21,0x02,0x8b,0xf8,0x8b,0xcf,0x81,0xe9,0x33,0x27,0,0,0x74,0x5e,0x83,0xe9,0x02,0x74,0x59,0x83,0xe9,0x13,0x74,0x40 };
        internal static void ValidateConnectImage(byte[] data)
        {
            var pe = new LaunchPe(data); if (pe.Machine != 0x14c || pe.Magic != 0x10b) throw Error("连接补丁需要 32 位 chusanApp.exe。");
            var slots = pe.WinsockSlots(); uint[] expectedSlots = { 0x1e1abf4, 0x1e1ac04, 0x1e1ac38 }; string[] names = { "ioctlsocket", "connect", "WSAGetLastError", "closesocket" };
            foreach (string name in names) if (!slots.ContainsKey(name) || slots[name].Count != 1) throw Error("游戏 Winsock 导入缺失或重复，连接补丁不能安全启用。");
            for (int i = 0; i < 3; i++) if (slots[names[i]][0] != expectedSlots[i]) throw Error("游戏连接导入槽与补丁不匹配。");
            if (pe.ImageBase > UInt32.MaxValue - expectedSlots[2] || pe.ImageSize < 0x67796c + 76) throw Error("游戏 PE 地址范围不匹配。");
            int offset = pe.Offset(0x67796c, 76, true); var expected = (byte[])ConnectSignature.Clone(); int[] operands = { 21, 39, 50 };
            for (int i = 0; i < 3; i++) Array.Copy(BitConverter.GetBytes((uint)pe.ImageBase + expectedSlots[i]), 0, expected, operands[i], 4);
            for (int i = 0; i < 76; i++) if (data[offset + i] != expected[i]) throw Error("游戏连接函数与补丁版本不匹配。");
        }
        private sealed class LaunchPe
        {
            private readonly byte[] data; private readonly int sections, sectionCount, optional; internal ushort Machine, Magic; internal ulong ImageBase; internal uint ImageSize;
            internal LaunchPe(byte[] bytes)
            {
                data = bytes; if (U16(0) != 0x5a4d) throw Error("文件不是有效的 PE 程序。"); int nt = checked((int)U32(0x3c));
                if (nt < 64 || U32(nt) != 0x4550) throw Error("PE 文件头无效。"); Machine = U16(nt + 4); sectionCount = U16(nt + 6); optional = nt + 24; Magic = U16(optional);
                int optionalSize = U16(nt + 20); if ((Magic != 0x10b && Magic != 0x20b) || optionalSize < (Magic == 0x10b ? 224 : 240) || sectionCount < 1 || sectionCount > 96) throw Error("PE 结构不受支持。");
                ImageBase = Magic == 0x10b ? U32(optional + 28) : U64(optional + 24); ImageSize = U32(optional + 56); sections = optional + optionalSize; Need(sections, sectionCount * 40);
            }
            private void Need(int offset, int size) { if (offset < 0 || size < 0 || offset > data.Length - size) throw Error("PE 文件截断或地址越界。"); }
            private ushort U16(int p) { Need(p, 2); return BitConverter.ToUInt16(data, p); }
            private uint U32(int p) { Need(p, 4); return BitConverter.ToUInt32(data, p); }
            private ulong U64(int p) { Need(p, 8); return BitConverter.ToUInt64(data, p); }
            internal int Offset(uint rva, int size, bool executable)
            {
                for (int i = 0; i < sectionCount; i++)
                {
                    int p = sections + i * 40; uint va = U32(p + 12), rawSize = U32(p + 16), raw = U32(p + 20);
                    if (rva < va || (ulong)(rva - va) + (uint)size > rawSize) continue;
                    if (executable && (U32(p + 36) & 0x20000000) == 0) throw Error("连接函数不在可执行节中。");
                    int off = checked((int)((ulong)raw + rva - va)); Need(off, size); return off;
                }
                throw Error("PE RVA 无法映射到文件数据。");
            }
            private string Ascii(uint rva)
            {
                int p = Offset(rva, 1, false), end = p; while (end < data.Length && end - p < 512 && data[end] != 0) end++;
                if (end >= data.Length || end - p == 512) throw Error("PE 导入名称无效。"); return Encoding.ASCII.GetString(data, p, end - p);
            }
            internal Dictionary<string, List<uint>> WinsockSlots()
            {
                var result = new Dictionary<string, List<uint>>(StringComparer.Ordinal); uint import = U32(optional + 96 + 8); if (import == 0) throw Error("游戏没有导入表。");
                for (uint descriptor = 0; descriptor < 1024; descriptor++)
                {
                    int p = Offset(checked(import + descriptor * 20), 20, false); uint name = U32(p + 12); if (name == 0) return result;
                    if (!String.Equals(Ascii(name), "ws2_32.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    uint original = U32(p), first = U32(p + 16); if (original == 0 || first == 0) throw Error("Winsock 导入结构缺失。");
                    for (uint i = 0; i < 4096; i++)
                    {
                        uint address = U32(Offset(checked(original + 4 * i), 4, false)); if (address == 0) break; string function = "";
                        if ((address & 0x80000000) != 0) { uint ordinal = address & 0xffff; function = ordinal == 4 ? "connect" : ordinal == 3 ? "closesocket" : ordinal == 10 ? "ioctlsocket" : ordinal == 111 ? "WSAGetLastError" : ""; }
                        else function = Ascii(checked(address + 2));
                        if (new[] { "connect", "closesocket", "ioctlsocket", "WSAGetLastError" }.Contains(function)) { List<uint> list; if (!result.TryGetValue(function, out list)) { list = new List<uint>(); result.Add(function, list); } list.Add(checked(first + 4 * i)); }
                        if (i == 4095) throw Error("Winsock 导入表没有结束标记。");
                    }
                }
                throw Error("PE 导入表没有结束标记。");
            }
        }
    }
}
