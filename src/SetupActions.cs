using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ChuniLanFour
{
    public enum GameConfigRole { Unknown, Host, Client }

    public sealed class GameConfigInspection
    {
        public GameConfigRole Role { get; internal set; }
        public int Slot { get; internal set; }
        public string Subnet { get; internal set; }
        public bool NetenvEnabled { get; internal set; }
        public string Hash { get; internal set; }
        public string Summary { get; internal set; }
        public string Description { get; internal set; }
    }

    public static class SetupActions
    {
        public static GameConfigInspection InspectGameConfig(string path)
        {
            path = ValidateGameIniPath(path);
            byte[] bytes;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) bytes = ReadConfigBytes(input);
            var document = ByteDocument.Read(bytes);
            var lines = SplitLines(document.Text);
            var sections = ReadSections(lines);
            string roleSection = RoleSection(sections, false);
            string enable = ValueOf(lines, sections, "netenv", "enable");
            string suffix = ValueOf(lines, sections, "netenv", "addrSuffix");
            string subnet = ValueOf(lines, sections, "keychip", "subnet");
            string dipsw = roleSection == null ? null : ValueOf(lines, sections, roleSection, "dipsw1");
            int enableNumber, suffixNumber, dipswNumber;
            bool enabled = ReadNumber(enable, out enableNumber) && enableNumber == 1;
            bool suffixKnown = ReadNumber(suffix, out suffixNumber) && suffixNumber >= 11 && suffixNumber <= 14;
            bool dipswKnown = ReadNumber(dipsw, out dipswNumber) && (dipswNumber == 0 || dipswNumber == 1);
            var reasons = new List<string>();
            if (!enabled) reasons.Add(enable == null ? "缺少 [netenv] enable" : "[netenv] enable 未设为 1");
            if (!suffixKnown) reasons.Add(suffix == null ? "缺少 [netenv] addrSuffix" : "[netenv] addrSuffix 须为 11 至 14");
            if (!ValidSubnet(subnet)) reasons.Add(subnet == null ? "缺少 [keychip] subnet" : "[keychip] subnet 须为有效的 192.168.x.0 子网");
            if (roleSection == null) reasons.Add("缺少 [gpio] 或 [system] 角色节");
            else if (!dipswKnown) reasons.Add(dipsw == null ? "缺少 [" + roleSection + "] dipsw1" : "[" + roleSection + "] dipsw1 须为 0 或 1");
            if (suffixKnown && dipswKnown && ((dipswNumber == 1 && suffixNumber != 11) || (dipswNumber == 0 && suffixNumber == 11)))
                reasons.Add("dipsw1 与 addrSuffix 的角色不一致");
            var result = new GameConfigInspection { Role = GameConfigRole.Unknown, Slot = 0, Subnet = subnet ?? "", NetenvEnabled = enabled, Hash = HashBytes(bytes) };
            if (reasons.Count == 0)
            {
                result.Role = dipswNumber == 1 ? GameConfigRole.Host : GameConfigRole.Client;
                result.Slot = suffixNumber - 10;
                result.Summary = result.Slot + " 号 · " + (result.Role == GameConfigRole.Host ? "基准机" : "接入机") + "（" + subnet.Substring(0, subnet.LastIndexOf('.') + 1) + suffixNumber + "/24）";
                result.Description = "[" + roleSection + "] dipsw1 = " + dipsw + "\r\n[netenv] enable = " + enable + "\r\n[netenv] addrSuffix = " + suffix + "\r\n[keychip] subnet = " + subnet;
            }
            else
            {
                result.Summary = "配置角色未确定";
                result.Description = String.Join("；", reasons.ToArray()) + "。";
            }
            return result;
        }
        public static string ApplyGameConfigChecked(RoomConfig cfg, string expectedHash)
        {
            ValidateExpectedHash(expectedHash);
            return ApplyConfig(cfg, ValidatePath(cfg), RefuseRunningGame, expectedHash);
        }
        public static string RestoreHostConfigChecked(RoomConfig cfg, string expectedHash)
        {
            ValidateExpectedHash(expectedHash);
            return RestoreHostConfigCore(cfg, expectedHash);
        }
        private static string RestoreHostConfigCore(RoomConfig cfg, string expectedHash)
        {
            var host = HostConfig(cfg);
            string result = ApplyConfig(host, ValidatePath(host, false), RefuseRunningGame, expectedHash);
            return "segatools 基准机配置已恢复。\r\n" + result;
        }
        private static RoomConfig HostConfig(RoomConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            return new RoomConfig { Slot = 1, Subnet = cfg.Subnet, GameIniPath = cfg.GameIniPath };
        }
        internal static byte[] RewriteHostConfig(byte[] bytes, RoomConfig cfg, out string role)
        {
            return RewriteGameConfig(bytes, HostConfig(cfg), out role);
        }
        internal static string ApplyConfig(RoomConfig cfg, string path, Action ensureStopped, string expectedHash = null)
        {
            ensureStopped();
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            string role; byte[] original, changed;
            try
            {
                // Read under a write-denying handle; commit with an atomic replacement.
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    original = ReadConfigBytes(input);
                    RequireExpectedHash(original, expectedHash);
                    changed = RewriteGameConfig(original, cfg, out role);
                    if (original.SequenceEqual(changed)) return "配置已符合当前机号。\r\n" + Summary(cfg, role);
                    File.WriteAllBytes(temporary, changed);
                    ensureStopped();
                    if (expectedHash != null)
                    {
                        using (var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) RequireExpectedHash(ReadConfigBytes(current), expectedHash);
                    }
                    File.Replace(temporary, path, null);
                }
                if (!File.ReadAllBytes(path).SequenceEqual(changed)) throw new IOException("配置写入校验失败，请检查文件权限和磁盘状态。");
                return "配置已应用。\r\n" + Summary(cfg, role);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static string ValidatePath(RoomConfig cfg, bool requireRoom = true)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            if (requireRoom) cfg.Validate(); else ValidateGameSettings(cfg);
            return ValidateGameIniPath(cfg.GameIniPath);
        }
        private static string ValidateGameIniPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("请先选择游戏正在使用的 segatools.ini。");
            path = Path.GetFullPath(path);
            if (!String.Equals(Path.GetFileName(path), "segatools.ini", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择名为 segatools.ini 的文件。");
            if (!File.Exists(path)) throw new FileNotFoundException("未找到所选 segatools.ini。", path);
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("配置文件超过 4 MiB，请确认所选文件。");
            return path;
        }
        private static byte[] ReadConfigBytes(FileStream input)
        {
            if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("配置文件超过 4 MiB，请确认选中 segatools.ini。");
            var bytes = new byte[(int)input.Length]; int offset = 0;
            while (offset < bytes.Length) { int count = input.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new IOException("配置文件读取不完整。"); offset += count; }
            return bytes;
        }
        private static string HashBytes(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes));
        }
        private static void ValidateExpectedHash(string expectedHash)
        {
            byte[] hash = null;
            try { if (expectedHash != null) hash = Convert.FromBase64String(expectedHash); }
            catch (FormatException) { }
            if (hash == null || hash.Length != 32) throw new ArgumentException("请先检测配置文件，再应用配置。", "expectedHash");
        }
        private static void RequireExpectedHash(byte[] bytes, string expectedHash)
        {
            if (expectedHash != null && !String.Equals(HashBytes(bytes), expectedHash, StringComparison.Ordinal))
                throw new InvalidOperationException("配置文件在检测后发生变化，请重新检测再应用。");
        }
        private static void RefuseRunningGame()
        {
            foreach (string name in new[] { "chusanApp", "amdaemon" })
            {
                var processes = Process.GetProcessesByName(name); bool running = processes.Length != 0;
                foreach (var process in processes) process.Dispose();
                if (running) throw new InvalidOperationException(name + " 正在运行。请退出游戏后再应用配置。");
            }
        }
        private static void ValidateGameSettings(RoomConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            if (cfg.Slot < 1 || cfg.Slot > 4) throw new ArgumentException("机号须为 1 至 4。");
            if (!ValidSubnet(cfg.Subnet))
                throw new ArgumentException("子网须为 192.168.x.0，其中 x 为 0 至 255。");
        }
        private static bool ValidSubnet(string subnet)
        {
            return Regex.IsMatch(subnet ?? "", @"\A192\.168\.(0|[1-9][0-9]{0,2})\.0\z") && Int32.Parse(subnet.Split('.')[2]) <= 255;
        }
        private static string Summary(RoomConfig cfg, string role)
        {
            return cfg.Slot + " 号 · " + (cfg.Slot == 1 ? "基准机" : "接入机") + "（" + cfg.ExpectedIp + "/24）\r\n[netenv] enable = 1\r\n[netenv] addrSuffix = " + (10 + cfg.Slot) + "\r\n[keychip] subnet = " + cfg.Subnet + "\r\n[" + role + "] dipsw1 = " + (cfg.Slot == 1 ? "1" : "0");
        }
        internal static byte[] RewriteGameConfig(byte[] bytes, RoomConfig cfg, out string role)
        {
            ValidateGameSettings(cfg);
            var document = ByteDocument.Read(bytes); var lines = SplitLines(document.Text);
            string newline = lines.Select(l => l.Ending).FirstOrDefault(e => e.Length != 0) ?? "\r\n";
            var sections = ReadSections(lines);
            role = RoleSection(sections, true);
            var desired = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase)
            {
                { "netenv", new List<KeyValuePair<string,string>> { new KeyValuePair<string,string>("enable", "1"), new KeyValuePair<string,string>("addrSuffix", (10 + cfg.Slot).ToString()) } },
                { "keychip", new List<KeyValuePair<string,string>> { new KeyValuePair<string,string>("subnet", cfg.Subnet) } },
                { role, new List<KeyValuePair<string,string>> { new KeyValuePair<string,string>("dipsw1", cfg.Slot == 1 ? "1" : "0") } }
            };
            var inserts = new Dictionary<int, List<string>>(); var append = new List<string>();
            foreach (var item in desired)
            {
                Section section;
                if (!sections.TryGetValue(item.Key, out section))
                {
                    append.Add("[" + item.Key + "]"); foreach (var key in item.Value) append.Add(key.Key + "=" + key.Value); continue;
                }
                foreach (var key in item.Value)
                {
                    int index;
                    if (section.Keys.TryGetValue(key.Key, out index)) lines[index].Text = ReplaceValue(lines[index].Text, key.Value);
                    else { List<string> values; if (!inserts.TryGetValue(section.End, out values)) { values = new List<string>(); inserts.Add(section.End, values); } values.Add(key.Key + "=" + key.Value); }
                }
            }
            var output = new StringBuilder();
            for (int i = 0; i <= lines.Count; i++)
            {
                List<string> values;
                if (inserts.TryGetValue(i, out values)) { EnsureNewline(output, newline); foreach (string value in values) output.Append(value).Append(newline); }
                if (i < lines.Count) output.Append(lines[i].Text).Append(lines[i].Ending);
            }
            if (append.Count != 0) { EnsureNewline(output, newline); foreach (string value in append) output.Append(value).Append(newline); }
            return document.Encode(output.ToString());
        }
        private static Dictionary<string, Section> ReadSections(List<Line> lines)
        {
            var sections = new Dictionary<string, Section>(StringComparer.OrdinalIgnoreCase);
            var current = new Section { Name = "", End = lines.Count };
            for (int i = 0; i < lines.Count; i++)
            {
                string text = lines[i].Text.Trim(' ', '\t');
                if (text.Length == 0 || text[0] == ';' || text[0] == '#') continue;
                if (text[0] == '[')
                {
                    var match = Regex.Match(text, @"^\[([^\]\r\n]+)\][ \t]*(?:[;#].*)?$");
                    if (!match.Success) throw new InvalidDataException("配置节标题格式无效。");
                    string name = match.Groups[1].Value.Trim(' ', '\t');
                    if (name.Length == 0 || sections.ContainsKey(name)) throw new InvalidDataException("配置存在空节名或重复节 [" + name + "]。");
                    current.End = i; current = new Section { Name = name, End = lines.Count }; sections.Add(name, current); continue;
                }
                int equals = text.IndexOf('='); if (equals < 0) continue;
                string key = text.Substring(0, equals).Trim(' ', '\t');
                if (key.Length == 0) throw new InvalidDataException("配置中存在空键名。");
                if (current.Keys.ContainsKey(key)) throw new InvalidDataException("配置 [" + current.Name + "] 中键 " + key + " 重复。");
                current.Keys.Add(key, i);
            }
            return sections;
        }
        private static string RoleSection(Dictionary<string, Section> sections, bool required)
        {
            bool gpio = sections.ContainsKey("gpio"), system = sections.ContainsKey("system");
            if (gpio && system) throw new InvalidDataException("配置同时包含 [gpio] 与 [system]，请确认 dipsw1 所在节。");
            if (!gpio && !system && required) throw new InvalidDataException("配置缺少 [gpio] 或 [system] 节。");
            return gpio ? "gpio" : system ? "system" : null;
        }
        private static string ValueOf(List<Line> lines, Dictionary<string, Section> sections, string sectionName, string key)
        {
            Section section; int index;
            if (!sections.TryGetValue(sectionName, out section) || !section.Keys.TryGetValue(key, out index)) return null;
            string line = lines[index].Text;
            string value = line.Substring(line.IndexOf('=') + 1);
            int comment = value.IndexOfAny(new[] { ';', '#' });
            return (comment < 0 ? value : value.Substring(0, comment)).Trim(' ', '\t');
        }
        private static bool ReadNumber(string value, out int result)
        {
            return Int32.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out result);
        }
        private static string ReplaceValue(string line, string desired)
        {
            int start = line.IndexOf('=') + 1; while (start < line.Length && (line[start] == ' ' || line[start] == '\t')) start++;
            int end = line.Length;
            for (int i = start; i < line.Length; i++) if (line[i] == ';' || line[i] == '#') { end = i; break; }
            int valueEnd = end; while (valueEnd > start && (line[valueEnd - 1] == ' ' || line[valueEnd - 1] == '\t')) valueEnd--;
            return line.Substring(0, start) + desired + line.Substring(valueEnd);
        }
        private static void EnsureNewline(StringBuilder text, string newline) { if (text.Length != 0 && text[text.Length - 1] != '\r' && text[text.Length - 1] != '\n') text.Append(newline); }
        private static List<Line> SplitLines(string text)
        {
            var result = new List<Line>(); int start = 0;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\r' || text[i] == '\n') { int end = i; if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; result.Add(new Line { Text = text.Substring(start, end - start), Ending = text.Substring(end, i - end + 1) }); start = i + 1; }
            if (start < text.Length) result.Add(new Line { Text = text.Substring(start), Ending = "" });
            return result;
        }
        private sealed class Line { internal string Text, Ending; }
        private sealed class Section { internal string Name; internal int End; internal Dictionary<string, int> Keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); }
        private sealed class ByteDocument
        {
            internal string Text; private byte[] preamble; private Encoding encoding;
            internal static ByteDocument Read(byte[] bytes)
            {
                if (bytes == null || bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("配置内容为空或过大。");
                int skip = 0; Encoding enc = Encoding.GetEncoding(28591);
                if (bytes.Length >= 4 && ((bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0) || (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff))) throw new InvalidDataException("暂不支持 UTF-32 配置，请先另存为 UTF-8。");
                if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { skip = 2; enc = new UnicodeEncoding(false, false, true); }
                else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) { skip = 2; enc = new UnicodeEncoding(true, false, true); }
                else if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) skip = 3;
                else if (bytes.Contains((byte)0))
                {
                    // Recognize BOM-less UTF-16 only when ASCII INI syntax establishes byte order.
                    if (bytes.Length % 2 != 0) throw new InvalidDataException("配置含有无法识别的空字节或编码。");
                    bool little = false, big = false;
                    for (int i = 0; i + 1 < bytes.Length; i += 2) { if (bytes[i] == '[' && bytes[i + 1] == 0) little = true; if (bytes[i] == 0 && bytes[i + 1] == '[') big = true; }
                    if (little == big) throw new InvalidDataException("无法确认无 BOM 配置的 UTF-16 字节序。");
                    enc = new UnicodeEncoding(big, false, true);
                }
                string text;
                try { text = enc.GetString(bytes, skip, bytes.Length - skip); }
                catch (DecoderFallbackException) { throw new InvalidDataException("配置编码无效。"); }
                if (text.IndexOf('\0') >= 0) throw new InvalidDataException("配置包含空字符。");
                return new ByteDocument { Text = text, preamble = bytes.Take(skip).ToArray(), encoding = enc };
            }
            internal byte[] Encode(string text) { return preamble.Concat(encoding.GetBytes(text)).ToArray(); }
        }
        public static string ConfigureFirewall(RoomConfig cfg)
        {
            string directory = Path.GetDirectoryName(ValidatePath(cfg));
            string game = Path.Combine(directory, "chusanApp.exe"), daemon = Path.Combine(directory, "amdaemon.exe");
            if (!File.Exists(game) || !File.Exists(daemon)) throw new FileNotFoundException("所选配置同目录必须同时存在 chusanApp.exe 与 amdaemon.exe，才能添加精确程序规则。");
            string hash; using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()))).Replace("-", "").Substring(0, 12);
            object policy = null, rules = null;
            try
            {
                policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)); rules = Get(policy, "Rules");
                UpsertRule(rules, "ChuniLanFour-" + hash + "-Game", game, cfg.Cidr, false);
                UpsertRule(rules, "ChuniLanFour-" + hash + "-Daemon", daemon, cfg.Cidr, false);
                UpsertRule(rules, "ChuniLanFour-" + hash + "-Ping", null, cfg.Cidr, true);
                return "防火墙规则已配置：" + cfg.Cidr + "\r\n" + game + "\r\n" + daemon + "\r\nICMPv4 Echo（Ping）";
            }
            catch (Exception ex) { throw new InvalidOperationException(DescribeFirewallFailure(ex), ex); }
            finally { Release(rules); Release(policy); }
        }
        private static void UpsertRule(object rules, string name, string program, string cidr, bool icmp)
        {
            object rule = null; bool exists = false;
            try
            {
                try { rule = Invoke(rules, "Item", new object[] { name }); exists = true; }
                catch (Exception ex) { if (!IsMissingFirewallRule(ex)) throw; }
                if (exists && !String.Equals(Convert.ToString(Get(rule, "Grouping")), "CHUNITHM 联机助手（四机版）", StringComparison.Ordinal)) throw new InvalidOperationException("防火墙规则名称冲突：" + name);
                if (!exists) rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true));
                Set(rule, "Name", name); Set(rule, "Description", "仅允许所选 CHUNITHM 房间网段的入站通信。"); Set(rule, "Grouping", "CHUNITHM 联机助手（四机版）");
                Set(rule, "Protocol", icmp ? 1 : 256); if (icmp) Set(rule, "IcmpTypesAndCodes", "8:*"); else Set(rule, "ApplicationName", program);
                Set(rule, "RemoteAddresses", cidr); Set(rule, "LocalAddresses", "*"); Set(rule, "Direction", 1); Set(rule, "Profiles", Int32.MaxValue); Set(rule, "Action", 1); Set(rule, "Enabled", true);
                if (!exists) Invoke(rules, "Add", new[] { rule });
            }
            finally { Release(rule); }
        }
        private static Exception UnwrapFirewallException(Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            return error;
        }
        internal static bool IsMissingFirewallRule(Exception error)
        {
            // COM's missing Item maps to FileNotFoundException on .NET Framework, not always COMException.
            return UnwrapFirewallException(error).HResult == unchecked((int)0x80070002);
        }
        internal static string DescribeFirewallFailure(Exception error)
        {
            var cause = UnwrapFirewallException(error);
            string message = "防火墙规则未全部配置完成。\r\n原因：" + cause.Message + "\r\n错误：" + cause.GetType().Name + "（HRESULT 0x" + cause.HResult.ToString("X8") + "）";
            if (cause.HResult == unchecked((int)0x80070005))
                message += "\r\nWindows 拒绝访问。请确认助手已取得管理员权限；若已提权，请检查防火墙策略或服务权限。";
            return message;
        }
        private static object Get(object target, string name) { return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null); }
        private static void Set(object target, string name, object value) { target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value }); }
        private static object Invoke(object target, string name, object[] args) { return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args); }
        private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    }
}
