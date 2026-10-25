using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace ChuniLanFour
{
    public static class RuntimeSupport
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returned);
        public static bool IsElevated
        {
            get
            {
                using (var identity = WindowsIdentity.GetCurrent())
                { int value, returned; if (!GetTokenInformation(identity.Token, 20, out value, sizeof(int), out returned)) throw new Win32Exception(Marshal.GetLastWin32Error()); return value != 0; }
            }
        }
        public static string PrivilegeStatus
        { get { try { return IsElevated ? "管理员权限" : "标准权限"; } catch (Exception ex) { return "权限状态读取失败：" + DescribeException(ex); } } }
        public static string LogPath
        { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChuniLanFour", "activity.log"); } }
        public static string DescribeException(Exception ex)
        {
            var output = new StringBuilder();
            for (int depth = 0; ex != null && depth < 8; depth++, ex = ex.InnerException)
            {
                if (ex is TargetInvocationException || (ex is AggregateException && ex.InnerException != null)) continue;
                if (output.Length > 0) output.Append(" → ");
                output.Append(ex.Message).Append(" [").Append(ex.GetType().Name).Append(" / 0x").Append(ex.HResult.ToString("X8"));
                var native = ex as Win32Exception; if (native != null) output.Append(" / Win32 ").Append(native.NativeErrorCode);
                output.Append(']');
            }
            return SafeText(output.ToString(), 2600);
        }
        internal static string SafeText(string value, int limit)
        {
            value = Regex.Replace(value ?? "", "(?i)(authtoken|authorization|bearer|identity\\.secret)(\\s*[:=]\\s*|\\s+)\\S+", "$1=[已隐藏]");
            value = Regex.Replace(value, "\\b[0-9a-fA-F]{64,}\\b", "[长密钥已隐藏]");
            return value.Length > limit ? value.Substring(0, limit) + "…（已截断）" : value;
        }
        public static void AppendLog(string text)
        {
            try
            {
                string path = LogPath; Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.WriteAllText(path, "", new UTF8Encoding(true));
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + SafeText(text, 10000) + Environment.NewLine, new UTF8Encoding(true));
            }
            catch { /* Log failures do not interrupt application operations. */ }
        }
    }
}
