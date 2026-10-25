using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ChuniLanFour
{
    public sealed class ZeroTierSession
    {
        private const string ServiceName = "ZeroTierOneService";
        private static readonly string CliPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"ZeroTier\One\zerotier-one_x64.exe");
        private readonly RoomConfig config;
        private List<PeerResult> lastPeers = new List<PeerResult>();
        public ZeroTierSession(RoomConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            config = new RoomConfig { RoomName = cfg.RoomName, NetworkId = cfg.NetworkId, Subnet = cfg.Subnet, Slot = cfg.Slot, GameIniPath = cfg.GameIniPath };
        }
        public Snapshot ReadSnapshot(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = new Snapshot { Updated = DateTime.Now.ToString("HH:mm:ss"), Startup = ReadStartupMode(), Service = "未安装", Network = "尚未查询", Settings = "尚未查询", Address = "未检测到本房间的 IPv4 地址", Note = "", NodeId = "", LocalIp = "", Peers = CopyPeers() };
            result.Installed = File.Exists(CliPath) && ServiceExists();
            if (!result.Installed) { result.Note = "请先安装 ZeroTier One，然后检测网络。"; return result; }
            try
            {
                using (var service = new ServiceController(ServiceName))
                {
                    service.Refresh(); result.ServiceRunning = service.Status == ServiceControllerStatus.Running;
                    result.Service = TranslateService(service.Status);
                }
                if (!result.ServiceRunning) { result.Network = "服务已停止"; result.Note = "请启动 ZeroTier 并连接房间网络。"; return result; }
                try { config.Validate(); }
                catch (ArgumentException ex) { result.Network = "房间尚未配置"; result.Note = ex.Message; return result; }
                var networks = GetNetworks(token);
                var network = networks.FirstOrDefault(n => Equal(GetString(n, "id"), config.NetworkId));
                var info = ParseObject(RunCli("-j info", token));
                string node = GetString(info, "address");
                if (System.Text.RegularExpressions.Regex.IsMatch(node, "\\A[0-9a-fA-F]{10}\\z")) result.NodeId = node;
                try { CheckConflicts(networks, network == null ? "" : GetString(network, "mac")); }
                catch (RoomConflictException ex) { result.Network = "网段冲突"; result.Note = ex.Message; return result; }
                if (network == null) { result.Network = "尚未加入此房间"; result.Note = "请在 ZeroTier 中加入网络：" + config.NetworkId + "，并请网络管理员授权。"; return result; }
                string status = GetString(network, "status"), type = GetString(network, "type");
                result.NetworkOk = status == "OK" && type == "PRIVATE";
                result.Network = TranslateNetwork(status) + " / " + type;
                result.SettingsOk = GetBool(network, "allowManaged") == true && GetBool(network, "allowDefault") == false && GetBool(network, "allowGlobal") == false && GetBool(network, "allowDNS") == false;
                result.Settings = "Managed=" + GetBool(network, "allowManaged") + "；Default=" + GetBool(network, "allowDefault") + "；Global=" + GetBool(network, "allowGlobal") + "；DNS=" + GetBool(network, "allowDNS") + "";
                var addresses = ReadRealAddresses(GetString(network, "mac"));
                result.Address = addresses.Count == 0 ? "等待 ZeroTier 网卡获取地址" : String.Join("；", addresses.ToArray());
                result.CorrectAddress = addresses.Contains(config.ExpectedIp + "/24");
                result.LocalIp = addresses.Any(a => a.StartsWith(config.ExpectedIp + "/", StringComparison.Ordinal)) ? config.ExpectedIp : (addresses.Count > 0 ? addresses[0].Split('/')[0] : "");
                if (!result.CorrectAddress) result.Note = "请让网络管理员在 ZeroTier Central 为本设备分配 " + config.ExpectedIp + "/24，并在 IPv4 Assignments 中设置对应地址段。";
                if (type != "PRIVATE") result.Note = "请将网络类型设为 Private。";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (!(ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NetworkInformationException || ex is UnauthorizedAccessException)) throw;
                result.Note = "读取服务或网络状态失败：" + RuntimeSupport.DescribeException(ex);
            }
            return result;
        }
        private void CheckConflicts(List<Dictionary<string, object>> networks, string selectedMac)
        {
            foreach (var network in networks)
            {
                if (Equal(GetString(network, "id"), config.NetworkId)) continue;
                if (GetBool(network, "allowManaged") == true)
                    foreach (string address in Strings(network, "assignedAddresses"))
                        if (Overlaps(address, config.Cidr)) throw new RoomConflictException("另一 ZeroTier 房间（" + GetString(network, "id") + "）也使用本房间网段。请在 ZeroTier 中退出旧网络，或为新房间选择不同网段。");
                object routesValue;
                if (GetBool(network, "allowManaged") == true && network.TryGetValue("routes", out routesValue) && routesValue is object[])
                    foreach (object item in (object[])routesValue)
                    {
                        var route = item as Dictionary<string, object>;
                        string target = route == null ? "" : GetString(route, "target");
                        if (RouteCanApply(network, target) && Overlaps(target, config.Cidr)) throw new RoomConflictException("另一 ZeroTier 房间的路由与本房间重叠。请在 ZeroTier 中退出旧网络，或改用不同网段。");
                    }
            }
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback || MacMatches(adapter, selectedMac)) continue;
                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (Overlaps(address.Address + "/" + Prefix(address.IPv4Mask), config.Cidr))
                        throw new RoomConflictException("网卡“" + adapter.Name + "”的地址 " + address.Address + " 与房间网段重叠。请为房间选择其他网段，或先断开冲突网络。");
                }
            }
        }
        internal static bool Overlaps(string first, string second)
        {
            uint a, b, ma, mb;
            if (!TryCidr(first, out a, out ma) || !TryCidr(second, out b, out mb)) return false;
            uint common = ma & mb; return (a & common) == (b & common);
        }
        private static bool RouteCanApply(Dictionary<string, object> network, string target)
        {
            if (target == "0.0.0.0/0" || GetBool(network, "allowManaged") != true) return false;
            uint address, mask;
            if (!TryCidr(target, out address, out mask)) return false;
            // This app uses 192.168.x.0/24. Wider public ranges require allowGlobal.
            bool privateRoute = (address & 0xffff0000U) == 0xc0a80000U && mask >= 0xffff0000U;
            return privateRoute || GetBool(network, "allowGlobal") == true;
        }
        private static bool TryCidr(string cidr, out uint address, out uint mask)
        {
            address = mask = 0; if (String.IsNullOrEmpty(cidr)) return false;
            string[] parts = cidr.Split('/'); IPAddress ip; int prefix;
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out ip) || ip.AddressFamily != AddressFamily.InterNetwork || !Int32.TryParse(parts[1], out prefix) || prefix < 0 || prefix > 32) return false;
            byte[] bytes = ip.GetAddressBytes(); address = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            mask = prefix == 0 ? 0 : UInt32.MaxValue << (32 - prefix); return true;
        }
        public Snapshot CheckPeers(CancellationToken token)
        {
            var snapshot = ReadSnapshot(token);
            if (!snapshot.ServiceRunning || !snapshot.NetworkOk || !snapshot.CorrectAddress) { snapshot.Note = "请先连接 Private 房间并确认本机真实地址为 " + config.ExpectedIp + "/24，再检测其他设备。"; return snapshot; }
            var tasks = Enumerable.Range(1, 4).Where(s => s != config.Slot).Select(s => Task.Run(() => PingPeer(s, token), token)).ToArray();
            Task.WaitAll(tasks, token);
            lastPeers = tasks.Select(t => t.Result).ToList(); snapshot.Peers = CopyPeers();
            snapshot.Note = "设备检测完成。";
            snapshot.Updated = DateTime.Now.ToString("HH:mm:ss"); return snapshot;
        }
        private PeerResult PingPeer(int slot, CancellationToken token)
        {
            var result = new PeerResult { Slot = slot, Ip = config.AddressForSlot(slot) }; long total = 0;
            using (var ping = new Ping())
                for (int i = 0; i < 3; i++)
                {
                    token.ThrowIfCancellationRequested();
                    try { var reply = ping.Send(IPAddress.Parse(result.Ip), 1500); if (reply.Status == IPStatus.Success) { result.Replies++; total += reply.RoundtripTime; } }
                    catch (PingException) { }
                }
            token.ThrowIfCancellationRequested(); result.AverageMs = result.Replies > 0 ? (double)total / result.Replies : 0;
            result.CheckedAt = DateTime.Now.ToString("HH:mm:ss");
            result.Text = (result.Replies > 0 ? result.Replies + "/3 回应 · 平均 " + result.AverageMs.ToString("0.#") + " ms" : "0/3 回应 · 未上线或通信受阻") + "（" + result.CheckedAt + "）"; return result;
        }
        private List<PeerResult> CopyPeers() { return lastPeers.Select(p => new PeerResult { Slot = p.Slot, Ip = p.Ip, Text = p.Text, Replies = p.Replies, AverageMs = p.AverageMs, CheckedAt = p.CheckedAt }).ToList(); }
        private static bool ServiceExists()
        { using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName, false)) return key != null; }
        private static string ReadStartupMode()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName, false))
            {
                if (key == null) return "未安装"; int start = Convert.ToInt32(key.GetValue("Start", -1));
                return start == 3 ? "手动启动" : start == 2 ? "自动启动" : start == 4 ? "已禁用" : "未知";
            }
        }
        private static List<Dictionary<string, object>> GetNetworks(CancellationToken token)
        {
            object[] parsed;
            string json = RunCli("-j listnetworks", token);
            try { parsed = new JavaScriptSerializer().DeserializeObject(json) as object[]; }
            catch (ArgumentException ex) { throw new InvalidOperationException("ZeroTier 返回的网络状态不是有效 JSON：" + ex.Message, ex); }
            if (parsed == null) throw new InvalidOperationException("ZeroTier 网络状态格式无法识别。");
            return parsed.OfType<Dictionary<string, object>>().ToList();
        }
        private static Dictionary<string, object> ParseObject(string text)
        { try { return new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object> ?? new Dictionary<string, object>(); } catch { throw new InvalidOperationException("ZeroTier 设备状态格式无法识别。"); } }
        private static string RunCli(string arguments, CancellationToken token)
        {
            if (arguments != "-j info" && arguments != "-j listnetworks")
                throw new InvalidOperationException("不支持的 ZeroTier 查询。");
            return RunProcess(CliPath, "-q " + arguments, token, 10000, "ZeroTier 状态读取失败");
        }
        private static string RunProcess(string path, string arguments, CancellationToken token, int timeout, string error)
        {
            token.ThrowIfCancellationRequested();
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(path, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(path) };
                process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync(); var timer = Stopwatch.StartNew();
                try
                {
                    while (!process.WaitForExit(100)) { token.ThrowIfCancellationRequested(); if (timer.ElapsedMilliseconds > timeout) throw new InvalidOperationException(Path.GetFileName(path) + " 响应超时（" + timeout + " ms）。"); }
                    token.ThrowIfCancellationRequested();
                    if (!Task.WaitAll(new Task[] { output, errors }, 2000)) throw new InvalidOperationException(Path.GetFileName(path) + " 已结束，但读取命令输出超时。");
                    if (process.ExitCode != 0) throw new InvalidOperationException(DescribeProcessFailure(error, path, arguments, process.ExitCode, output.Result, errors.Result));
                    return output.Result;
                }
                finally { try { if (!process.HasExited) process.Kill(); } catch { } }
            }
        }
        internal static string DescribeProcessFailure(string action, string path, string arguments, int exitCode, string output, string error)
        {
            string detail = String.Join("\n", new[] { error.Trim(), output.Trim() }.Where(s => s.Length > 0).ToArray());
            return action + "：" + Path.GetFileName(path) + " " + arguments + "，退出码 " + exitCode + ".\n" +
                (detail.Length == 0 ? "命令未返回错误文字。" : RuntimeSupport.SafeText(detail, 1600));
        }
        private static List<string> ReadRealAddresses(string mac)
        {
            var result = new List<string>(); if (String.IsNullOrEmpty(mac)) return result;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                if (MacMatches(adapter, mac) && adapter.OperationalStatus == OperationalStatus.Up)
                    foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork) result.Add(address.Address + "/" + Prefix(address.IPv4Mask));
            return result.Distinct().ToList();
        }
        private static int Prefix(IPAddress mask) { int count = 0; foreach (byte b in mask.GetAddressBytes()) for (int bit = 0; bit < 8; bit++) if ((b & (1 << bit)) != 0) count++; return count; }
        private static bool MacMatches(NetworkInterface adapter, string mac) { return !String.IsNullOrEmpty(mac) && Equal(adapter.GetPhysicalAddress().ToString(), mac.Replace(":", "").Replace("-", "")); }
        private static bool Equal(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static string GetString(Dictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : ""; }
        private static bool? GetBool(Dictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) && value is bool ? (bool?)value : null; }
        private static IEnumerable<string> Strings(Dictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) && value is object[] ? ((object[])value).Select(Convert.ToString) : Enumerable.Empty<string>(); }
        private static string TranslateService(ServiceControllerStatus status) { return status == ServiceControllerStatus.Running ? "正在运行" : status == ServiceControllerStatus.Stopped ? "已停止" : status == ServiceControllerStatus.StartPending ? "正在启动" : status == ServiceControllerStatus.StopPending ? "正在停止" : status.ToString(); }
        private static string TranslateNetwork(string status) { return status == "OK" ? "网络就绪" : status == "ACCESS_DENIED" ? "等待网络管理员授权" : status == "REQUESTING_CONFIGURATION" ? "正在获取配置" : status == "NOT_FOUND" ? "未找到网络" : status == "PORT_ERROR" ? "虚拟网卡错误" : String.IsNullOrEmpty(status) ? "未知状态" : status; }
        public sealed class Snapshot
        {
            public bool Installed { get; set; } public bool ServiceRunning { get; set; } public bool NetworkOk { get; set; } public bool CorrectAddress { get; set; } public bool SettingsOk { get; set; }
            public string Service { get; set; } public string Startup { get; set; } public string Network { get; set; } public string Settings { get; set; } public string Address { get; set; } public string LocalIp { get; set; } public string NodeId { get; set; } public string Updated { get; set; } public string Note { get; set; }
            public List<PeerResult> Peers { get; set; }
            public Snapshot() { Peers = new List<PeerResult>(); }
        }
        public sealed class PeerResult
        { public int Slot { get; set; } public string Ip { get; set; } public string Text { get; set; } public int Replies { get; set; } public double AverageMs { get; set; } public string CheckedAt { get; set; } }
        private sealed class RoomConflictException : InvalidOperationException { internal RoomConflictException(string message) : base(message) { } }
    }
}
