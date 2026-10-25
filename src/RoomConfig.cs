using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ChuniLanFour
{
    public sealed class RoomConfig
    {
        public string RoomName { get; set; }
        public string NetworkId { get; set; }
        public string Subnet { get; set; }
        public string GameIniPath { get; set; }
        public int Slot { get; set; }
        public RoomConfig() { RoomName = "CHUNITHM 联机房间"; NetworkId = ""; Subnet = "192.168.139.0"; Slot = 1; GameIniPath = ""; }
        public void Validate()
        {
            if (String.IsNullOrWhiteSpace(RoomName) || RoomName.Length > 80 || RoomName.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("房间名称需要 1 至 80 个字符，不能包含换行。");
            if (!Regex.IsMatch(NetworkId ?? "", "\\A[0-9a-fA-F]{16}\\z"))
                throw new ArgumentException("网络 ID 必须是 ZeroTier 提供的 16 位十六进制字符。");
            if (!Regex.IsMatch(Subnet ?? "", "\\A192\\.168\\.(0|[1-9][0-9]{0,2})\\.0\\z"))
                throw new ArgumentException("房间网段应为 192.168.x.0，其中 x 为 0 至 255；使用 /24 网段。");
            int octet = Int32.Parse(Subnet.Split('.')[2]);
            if (octet > 255) throw new ArgumentException("网段第三段必须是 0 至 255。");
            if (Slot < 1 || Slot > 4) throw new ArgumentException("本机编号必须为 1 至 4。");
            if ((GameIniPath ?? "").IndexOf('\0') >= 0) throw new ArgumentException("配置文件路径无效。");
        }
        public string AddressForSlot(int slot)
        {
            if (slot < 1 || slot > 4) throw new ArgumentOutOfRangeException("slot", "机号必须为 1 至 4。");
            if (String.IsNullOrEmpty(Subnet) || Subnet.LastIndexOf('.') < 0) throw new ArgumentException("房间网段无效。");
            return Subnet.Substring(0, Subnet.LastIndexOf('.') + 1) + (10 + slot);
        }
        public string ExpectedIp { get { return AddressForSlot(Slot); } }
        public string Cidr { get { return Subnet + "/24"; } }
        public static RoomConfig Load(string path)
        {
            if (!File.Exists(path)) return new RoomConfig();
            var cfg = Read(path, false);
            cfg.Validate();
            return cfg;
        }
        public void Save(string path) { Validate(); Write(path, false); }
        public void ExportRoom(string path) { Validate(); Write(path, true); }
        public static RoomConfig ImportRoom(string path)
        {
            var cfg = Read(path, true);
            cfg.Slot = 2; cfg.GameIniPath = ""; cfg.Validate(); return cfg;
        }
        private static RoomConfig Read(string path, bool shared)
        {
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("房间文件过大，无法读取。");
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            if (data == null) throw new InvalidDataException("房间文件格式无效。");
            var cfg = new RoomConfig(); object value;
            if (data.TryGetValue("RoomName", out value)) cfg.RoomName = Convert.ToString(value);
            if (data.TryGetValue("NetworkId", out value)) cfg.NetworkId = Convert.ToString(value);
            if (data.TryGetValue("Subnet", out value)) cfg.Subnet = Convert.ToString(value);
            if (!shared)
            {
                if (data.TryGetValue("Slot", out value)) cfg.Slot = Convert.ToInt32(value);
                if (data.TryGetValue("GameIniPath", out value)) cfg.GameIniPath = Convert.ToString(value);
            }
            return cfg;
        }
        private void Write(string path, bool shared)
        {
            var data = new Dictionary<string, object> { { "RoomName", RoomName }, { "NetworkId", NetworkId.ToLowerInvariant() }, { "Subnet", Subnet } };
            if (!shared) { data.Add("Slot", Slot); data.Add("GameIniPath", GameIniPath ?? ""); }
            string full = Path.GetFullPath(path); string directory = Path.GetDirectoryName(full);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".room-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(data), new UTF8Encoding(false));
                if (File.Exists(full)) File.Replace(temporary, full, null); else File.Move(temporary, full);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
