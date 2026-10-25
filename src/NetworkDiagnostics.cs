using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ChuniLanFour
{
    /// <summary>Read-only diagnostics. Call BuildReport from Task.Run, never the WPF UI thread.</summary>
    public static class NetworkDiagnostics
    {
        private const int TotalBudgetMs = 30000;
        private static readonly string[] GameNames = { "chusanApp", "amdaemon" };

        public static string BuildReport(RoomConfig cfg)
        {
            var timer = Stopwatch.StartNew();
            var report = new StringBuilder();
            report.AppendLine("Tonari · 诊断报告");
            report.AppendLine("生成时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            report.AppendLine("内容：网络、游戏配置、端口和防火墙信息。");
            var room = cfg == null ? new RoomConfig() : new RoomConfig { RoomName = cfg.RoomName, NetworkId = cfg.NetworkId, Subnet = cfg.Subnet, Slot = cfg.Slot, GameIniPath = cfg.GameIniPath };
            Section(report, timer, "助手版本与 Windows 权限", token => IdentityReport(), 3000);
            Section(report, timer, "房间与本机选择", token => RoomReport(room), 1000);
            Section(report, timer, "正在运行的 IPv4 网卡", token => AdapterReport(token), 5500);
            Section(report, timer, "相关进程", token => ProcessReport(token), 2500);
            Section(report, timer, "游戏联机配置", token => IniReport(room, token), 2500);
            Section(report, timer, "游戏 PID 的 TCP/UDP 端点", token => EndpointReport(token), 5000);
            Section(report, timer, "游戏文件版本比对", token => HashReport(room, token), 10000);
            Section(report, timer, "Windows 防火墙规则", token => FirewallReport(room, token), 5000);
            report.AppendLine();
            report.AppendLine("采集用时：" + timer.Elapsed.TotalSeconds.ToString("0.0") + " 秒。");
            return report.ToString();
        }

        private static void Section(StringBuilder output, Stopwatch total, string title, Func<CancellationToken, string> read, int budgetMs)
        {
            output.AppendLine(); output.AppendLine("【" + title + "】");
            int remaining = TotalBudgetMs - (int)total.ElapsedMilliseconds;
            if (remaining <= 0) { output.AppendLine("未采集：已达到整体 30 秒时间上限。"); return; }
            var cancel = new CancellationTokenSource();
            var work = Task.Factory.StartNew(() =>
            {
                try { return read(cancel.Token); }
                catch (Exception ex) { return "未能完整读取：" + Error(ex); }
            }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            if (work.Wait(Math.Min(remaining, budgetMs)))
            {
                output.AppendLine(work.Result); cancel.Dispose();
            }
            else
            {
                cancel.Cancel();
                output.AppendLine("读取超时，已跳过此项。");
                work.ContinueWith(t => cancel.Dispose(), TaskScheduler.Default);
            }
        }

        private static string IdentityReport()
        {
            var output = new StringBuilder();
            Assembly assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            output.AppendLine("程序集版本：" + assembly.GetName().Version);
            try
            {
                string path = assembly.Location;
                output.AppendLine("助手 EXE：" + Clean(path));
                var version = FileVersionInfo.GetVersionInfo(path);
                output.AppendLine("EXE 文件版本：" + Clean(version.FileVersion));
                output.AppendLine("EXE 产品版本：" + Clean(version.ProductVersion));
            }
            catch (Exception ex) { output.AppendLine("EXE 版本读取失败：" + Error(ex)); }
            output.AppendLine("进程位数：" + (Environment.Is64BitProcess ? "64 位" : "32 位") + "；系统位数：" + (Environment.Is64BitOperatingSystem ? "64 位" : "32 位"));
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
                int elevated, returned;
                if (!GetTokenInformation(token, 20, out elevated, sizeof(int), out returned)) throw new Win32Exception(Marshal.GetLastWin32Error());
                output.AppendLine("当前 Windows 进程令牌已提升：" + (elevated != 0 ? "是" : "否") + "（TokenElevation 实测）");
            }
            catch (Exception ex) { output.AppendLine("令牌提升状态读取失败：" + Error(ex)); }
            finally { if (token != IntPtr.Zero) CloseHandle(token); }
            return output.ToString().TrimEnd();
        }

        private static string RoomReport(RoomConfig cfg)
        {
            var output = new StringBuilder();
            output.AppendLine("房间名称：" + Clean(cfg.RoomName)); output.AppendLine("网络 ID：" + Clean(cfg.NetworkId));
            output.AppendLine("本机槽位：" + cfg.Slot); output.AppendLine("房间网段：" + Clean(cfg.Subnet) + "/24");
            try { cfg.Validate(); output.AppendLine("本机期望地址：" + cfg.ExpectedIp + "/24"); }
            catch (Exception ex) { output.AppendLine("房间参数校验未通过：" + Error(ex)); }
            output.AppendLine("所选配置：" + Clean(cfg.GameIniPath));
            output.AppendLine("这里显示的是助手配置；真实地址见网卡项，游戏实际读取哪份配置需结合启动方式确认。");
            return output.ToString().TrimEnd();
        }

        private static string AdapterReport(CancellationToken token)
        {
            var output = new StringBuilder(); var metrics = new Dictionary<int, int>();
            try
            {
                string text = RunCommand("netsh.exe", "interface ipv4 show interfaces", 3500, token);
                foreach (string line in text.Split('\n'))
                {
                    var m = Regex.Match(line, @"^\s*(\d+)\s+(\d+)\s+(\d+)\s+"); int index, metric;
                    if (m.Success && Int32.TryParse(m.Groups[1].Value, out index) && Int32.TryParse(m.Groups[2].Value, out metric)) metrics[index] = metric;
                }
                if (metrics.Count == 0) output.AppendLine("接口 metric：netsh 输出未能解析；地址信息仍继续采集。");
            }
            catch (Exception ex) { output.AppendLine("接口 metric 未取得：" + Error(ex)); }
            int count = 0;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                token.ThrowIfCancellationRequested();
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                try
                {
                    var properties = adapter.GetIPProperties();
                    var addresses = properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
                    if (addresses.Count == 0) continue; count++;
                    output.AppendLine(Clean(adapter.Name) + " | " + Clean(adapter.Description));
                    int index = properties.GetIPv4Properties().Index, metric;
                    output.AppendLine("  ifIndex=" + index + "；metric=" + (metrics.TryGetValue(index, out metric) ? metric.ToString() : "未取得") + "；类型=" + adapter.NetworkInterfaceType);
                    foreach (var address in addresses) output.AppendLine("  IPv4=" + address.Address + "/" + Prefix(address.IPv4Mask) + "；掩码=" + address.IPv4Mask);
                }
                catch (Exception ex) { output.AppendLine("  网卡项读取失败：" + Error(ex)); }
            }
            if (count == 0) output.AppendLine("未发现处于 Up 状态且具有 IPv4 地址的接口。");
            return output.ToString().TrimEnd();
        }

        private static string ProcessReport(CancellationToken token)
        {
            var output = new StringBuilder();
            foreach (string name in new[] { "chusanApp", "amdaemon", "zerotier-one_x64" })
            {
                token.ThrowIfCancellationRequested();
                Process[] processes;
                try { processes = Process.GetProcessesByName(name); }
                catch (Exception ex) { output.AppendLine(name + "：读取失败，" + Error(ex)); continue; }
                if (processes.Length == 0) output.AppendLine(name + "：未运行");
                foreach (var process in processes)
                {
                    using (process)
                    {
                        try { output.AppendLine(name + "：运行中，PID=" + process.Id + "；路径=" + Clean(process.MainModule.FileName)); }
                        catch (Exception ex) { output.AppendLine(name + "：PID=" + process.Id + "；路径不可读或进程已退出，" + Error(ex)); }
                    }
                }
            }
            return output.ToString().TrimEnd();
        }

        private static string IniReport(RoomConfig cfg, CancellationToken token)
        {
            string path = SelectedIni(cfg); var output = new StringBuilder();
            byte[] bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException("配置超过 4 MiB，已停止读取。");
                bytes = new byte[(int)file.Length]; int offset = 0;
                while (offset < bytes.Length) { token.ThrowIfCancellationRequested(); int n = file.Read(bytes, offset, bytes.Length - offset); if (n == 0) break; offset += n; }
                if (offset != bytes.Length) throw new IOException("文件在读取时发生变化或读取不完整。");
            }
            string encoding; string text = DecodeIni(bytes, out encoding);
            output.AppendLine("文件：" + Clean(path)); output.AppendLine("读取编码：" + encoding);
            var allowed = new HashSet<string>(new[] { "netenv.enable", "netenv.addrSuffix", "netenv.routerSuffix", "keychip.subnet", "gpio.dipsw1", "system.dipsw1" }, StringComparer.OrdinalIgnoreCase);
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string section = ""; int lineNumber = 0;
            foreach (string raw in Regex.Split(text, "\\r\\n|\\n|\\r"))
            {
                token.ThrowIfCancellationRequested(); lineNumber++;
                string line = raw.Trim(' ', '\t'); if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    var match = Regex.Match(line, @"^\[([^\]]+)\][ \t]*(?:[;#].*)?$"); section = match.Success ? match.Groups[1].Value.Trim(' ', '\t') : ""; continue;
                }
                int equal = line.IndexOf('='); if (equal < 1) continue;
                string key = line.Substring(0, equal).Trim(' ', '\t'), full = section + "." + key;
                if (!allowed.Contains(full)) continue;
                string value = line.Substring(equal + 1); int comment = value.IndexOfAny(new[] { ';', '#' }); if (comment >= 0) value = value.Substring(0, comment);
                int occurrences; seen.TryGetValue(full, out occurrences); seen[full] = occurrences + 1;
                output.AppendLine("第 " + lineNumber + " 行 [" + Clean(section) + "] " + Clean(key) + "=" + Clean(value.Trim(' ', '\t')));
            }
            foreach (string key in allowed) if (!seen.ContainsKey(key)) output.AppendLine(key + "：未配置");
            foreach (var pair in seen) if (pair.Value > 1) output.AppendLine("注意：" + pair.Key + " 重复出现 " + pair.Value + " 次，实际取值取决于游戏加载器。");
            if (seen.ContainsKey("gpio.dipsw1") && seen.ContainsKey("system.dipsw1")) output.AppendLine("注意：gpio 与 system 均包含 dipsw1，应核实实际 hook 分支。");
            return output.ToString().TrimEnd();
        }

        private static string HashReport(RoomConfig cfg, CancellationToken token)
        {
            string directory = Path.GetDirectoryName(SelectedIni(cfg)); var output = new StringBuilder();
            output.AppendLine("游戏目录文件：");
            foreach (string name in new[] { "chusanApp.exe", "amdaemon.exe", "chusanhook.dll", "chusanamhook.dll" })
            {
                token.ThrowIfCancellationRequested(); string path = Path.Combine(directory, name);
                try
                {
                    if (!File.Exists(path)) { output.AppendLine(name + "：文件不存在（本版本可能使用不同 hook 文件名）"); continue; }
                    var before = new FileInfo(path); long size = before.Length; DateTime modified = before.LastWriteTimeUtc;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan))
                    using (var sha = SHA256.Create())
                    {
                        byte[] buffer = new byte[65536]; int length;
                        while ((length = stream.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, length, buffer, 0); }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        var after = new FileInfo(path);
                        if (after.Length != size || after.LastWriteTimeUtc != modified) { output.AppendLine(name + "：读取期间文件改变，哈希不可用于比对。"); continue; }
                        output.AppendLine(name + " | " + size + " bytes | SHA256=" + BitConverter.ToString(sha.Hash).Replace("-", ""));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { output.AppendLine(name + "：读取失败，" + Error(ex)); }
            }
            return output.ToString().TrimEnd();
        }

        private static string FirewallReport(RoomConfig cfg, CancellationToken token)
        {
            string directory = Path.GetDirectoryName(SelectedIni(cfg));
            string game = Path.Combine(directory, "chusanApp.exe"), daemon = Path.Combine(directory, "amdaemon.exe"), hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()))).Replace("-", "").Substring(0, 12);
            string prefix = "ChuniLanFour-" + hash + "-";
            var expected = new HashSet<string>(new[] { prefix + "Game", prefix + "Daemon", prefix + "Ping" }, StringComparer.OrdinalIgnoreCase);
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var output = new StringBuilder();
            object policy = null, rules = null; IEnumerator enumerator = null;
            try
            {
                policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true));
                int current = Convert.ToInt32(Get(policy, "CurrentProfileTypes")); output.AppendLine("当前活动防火墙配置文件位：" + current + "（域=1，专用=2，公用=4）");
                foreach (int profile in new[] { 1, 2, 4 })
                    if ((current & profile) != 0)
                        try { output.AppendLine("配置文件 " + profile + " 防火墙：" + (Convert.ToBoolean(Get(policy, "FirewallEnabled", profile)) ? "已启用" : "已关闭")); }
                        catch (Exception ex) { output.AppendLine("配置文件 " + profile + " 开关读取失败：" + Error(ex)); }
                rules = Get(policy, "Rules"); enumerator = ((IEnumerable)rules).GetEnumerator(); int matched = 0, scanned = 0;
                while (enumerator.MoveNext())
                {
                    token.ThrowIfCancellationRequested(); object rule = enumerator.Current;
                    try
                    {
                        if (++scanned > 20000) { output.AppendLine("规则数量超过读取上限；此项不完整。"); break; }
                        string name = Convert.ToString(Get(rule, "Name")), app = Convert.ToString(Get(rule, "ApplicationName"));
                        bool target = expected.Contains(name), gameRule = IsGameProgram(app);
                        if (!target && !gameRule) continue;
                        matched++; if (target) found.Add(name);
                        bool enabled = Convert.ToBoolean(Get(rule, "Enabled")); int action = Convert.ToInt32(Get(rule, "Action")), profiles = Convert.ToInt32(Get(rule, "Profiles"));
                        output.AppendLine(Clean(name));
                        output.AppendLine("  " + (enabled ? "启用" : "禁用") + "；动作=" + (action == 0 ? "显式阻止" : action == 1 ? "允许" : action.ToString()) + "；方向=" + (Convert.ToInt32(Get(rule, "Direction")) == 1 ? "入站" : "出站") + "；配置文件位=" + profiles + "；适用当前配置文件=" + ((profiles & current) != 0 ? "是" : "否"));
                        output.AppendLine("  程序=" + (String.IsNullOrEmpty(app) ? "所有程序" : Clean(app)));
                        output.AppendLine("  远程网段=" + Clean(Convert.ToString(Get(rule, "RemoteAddresses"))) + "；本地地址=" + Clean(Convert.ToString(Get(rule, "LocalAddresses"))) + "；协议=" + Get(rule, "Protocol"));
                        if (gameRule && !PathEquals(app, game) && !PathEquals(app, daemon)) output.AppendLine("  注意：规则指向其他目录，不是当前所选配置同目录的游戏文件。");
                        if (enabled && action == 0) output.AppendLine("  发现启用的显式阻止候选；请结合方向、地址、协议和配置文件判断是否覆盖本次联机。");
                    }
                    catch (Exception ex) { output.AppendLine("某条候选规则读取失败：" + Error(ex)); }
                    finally { Release(rule); }
                    if (matched >= 120) { output.AppendLine("相关规则超过 120 条，已截断；不能据此认定不存在其他阻止规则。"); break; }
                }
                foreach (string name in expected) output.AppendLine("助手目标规则 " + name + "：" + (found.Contains(name) ? "已找到" : "本次未找到（若读取被截断则结果不完整）"));
                if (matched == 0) output.AppendLine("未找到指向这两个游戏程序的规则，也未找到本目录对应的助手规则。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { output.AppendLine("防火墙采集未完整完成：" + Error(ex)); }
            finally { Release(enumerator); Release(rules); Release(policy); }
            return output.ToString().TrimEnd();
        }

        private static string EndpointReport(CancellationToken token)
        {
            var ids = new HashSet<int>();
            foreach (string name in GameNames) foreach (var process in Process.GetProcessesByName(name)) using (process) { try { ids.Add(process.Id); } catch (InvalidOperationException) { } }
            if (ids.Count == 0) return "游戏与 amdaemon 均未运行，没有对应 PID 需要查询。";
            string text = RunCommand("netstat.exe", "-ano", 4000, token); var output = new StringBuilder(); int count = 0;
            var udp50201 = new List<string>(); int tcpListeners = 0;
            output.AppendLine("采集时游戏 PID：" + String.Join(", ", ids.OrderBy(i => i).Select(i => i.ToString()).ToArray()));
            output.AppendLine("格式：协议 本地端点 远程端点 [TCP 状态] PID");
            foreach (string line in text.Split('\n'))
            {
                token.ThrowIfCancellationRequested(); string[] values = Regex.Split(line.Trim(), @"\s+"); int pid;
                if (values.Length < 4 || (values[0] != "TCP" && values[0] != "UDP") || !Int32.TryParse(values[values.Length - 1], out pid) || !ids.Contains(pid)) continue;
                if (values[0] == "UDP" && values[1].EndsWith(":50201", StringComparison.Ordinal)) udp50201.Add(values[1] + "（PID " + pid + "）");
                if (values[0] == "TCP" && values.Length >= 5 && values[values.Length - 2] == "LISTENING") tcpListeners++;
                output.AppendLine(String.Join("  ", values)); if (++count >= 200) { output.AppendLine("端点超过 200 条，已截断。"); break; }
            }
            if (count == 0) output.AppendLine("本次未看到这些 PID 的 TCP/UDP 端点；可能尚未绑定、由其他进程处理，或进程刚退出。");
            output.AppendLine("重点 UDP 50201：" + (udp50201.Count == 0 ? "本次未观察到游戏 PID 绑定此端口。" : "已观察到本地绑定 " + String.Join("；", udp50201.ToArray())));
            output.AppendLine("游戏 PID 的 TCP LISTENING 数：" + tcpListeners + "；TCP 接续情况请结合上方端点与状态比对。" + (count >= 200 ? "（统计可能因截断而不完整）" : ""));
            return output.ToString().TrimEnd();
        }

        private static string RunCommand(string filename, string arguments, int timeoutMs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(Path.Combine(system, filename), arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); var timer = Stopwatch.StartNew();
                try
                {
                    while (!process.WaitForExit(100)) { token.ThrowIfCancellationRequested(); if (timer.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException(filename + " 超时。"); }
                    token.ThrowIfCancellationRequested(); if (!Task.WaitAll(new Task[] { stdout, stderr }, 1000)) throw new TimeoutException(filename + " 输出读取超时。");
                    if (process.ExitCode != 0) throw new InvalidOperationException(filename + " 返回代码 " + process.ExitCode + "：" + Clean(stderr.Result));
                    if (stdout.Result.Length > 4 * 1024 * 1024) throw new InvalidDataException(filename + " 输出过大，未解析。");
                    return stdout.Result;
                }
                finally { try { if (!process.HasExited) process.Kill(); } catch { } }
            }
        }
        private static string SelectedIni(RoomConfig cfg)
        {
            if (String.IsNullOrWhiteSpace(cfg.GameIniPath)) throw new InvalidOperationException("尚未选择 segatools.ini。");
            string path = Path.GetFullPath(cfg.GameIniPath);
            if (!String.Equals(Path.GetFileName(path), "segatools.ini", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("所选文件名不是 segatools.ini，已拒绝读取其他文件内容。");
            return path;
        }
        private static string DecodeIni(byte[] bytes, out string encoding)
        {
            if (bytes.Length >= 4 && ((bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0) || (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff))) throw new InvalidDataException("暂不支持 UTF-32，已跳过此文件。");
            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { encoding = "UTF-16 LE BOM"; return new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2); }
            if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) { encoding = "UTF-16 BE BOM"; return new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2); }
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) { encoding = "UTF-8 BOM"; return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3); }
            if (bytes.Contains((byte)0))
            {
                bool little = false, big = false;
                for (int i = 0; i + 1 < bytes.Length; i += 2) { if (bytes[i] == '[' && bytes[i + 1] == 0) little = true; if (bytes[i] == 0 && bytes[i + 1] == '[') big = true; }
                if (little == big || bytes.Length % 2 != 0) throw new InvalidDataException("无法确认含空字节配置的编码。");
                encoding = big ? "UTF-16 BE（无 BOM，按 INI 符号识别）" : "UTF-16 LE（无 BOM，按 INI 符号识别）"; return new UnicodeEncoding(big, false, true).GetString(bytes);
            }
            try { string decoded = new UTF8Encoding(false, true).GetString(bytes); encoding = "UTF-8（无 BOM）"; return decoded; }
            catch (DecoderFallbackException) { encoding = "单字节保留模式（GBK/ANSI 等）；只解析 ASCII 白名单键"; return Encoding.GetEncoding(28591).GetString(bytes); }
        }
        private static bool IsGameProgram(string path)
        {
            try { string name = Path.GetFileName(Environment.ExpandEnvironmentVariables(path ?? "").Trim('"')); return String.Equals(name, "chusanApp.exe", StringComparison.OrdinalIgnoreCase) || String.Equals(name, "amdaemon.exe", StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }
        private static bool PathEquals(string a, string b) { try { return String.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(a).Trim('"')), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); } catch { return false; } }
        private static int Prefix(System.Net.IPAddress mask) { int count = 0; foreach (byte value in mask.GetAddressBytes()) for (int bit = 0; bit < 8; bit++) if ((value & (1 << bit)) != 0) count++; return count; }
        private static string Clean(string value) { if (String.IsNullOrEmpty(value)) return "（空）"; string text = Regex.Replace(value, @"[\x00-\x1f\x7f]", " "); return text.Length > 600 ? text.Substring(0, 600) + "…（截断）" : text; }
        private static string Error(Exception ex) { while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException; return Clean(ex.Message) + " [" + ex.GetType().Name + " / 0x" + ex.HResult.ToString("X8") + "]"; }
        private static object Get(object target, string name, params object[] args) { return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args); }
        private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) { try { Marshal.FinalReleaseComObject(value); } catch { } } }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returned);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }
}
