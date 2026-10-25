using System;
using System.Reflection;
using System.Threading;
using ChuniLanFour;

internal static class ZeroTierReadOnlyTests
{
    public static int Main()
    {
        int count = 0;
        Type type = typeof(ZeroTierSession);
        foreach (string name in new[] { "StartAndConnect", "StopService", "StopServiceByUser", "LeaveRoom", "EnsureManualStartup" })
        {
            if (type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance) != null)
                throw new Exception("ZeroTier mutation API still exists: " + name);
            count++;
        }
        MethodInfo run = type.GetMethod("RunCli", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (string command in new[] { "join 0123456789abcdef", "leave 0123456789abcdef", "set 0123456789abcdef allowManaged=1", "-j info & whoami", "-j peers" })
        {
            try { run.Invoke(null, new object[] { command, CancellationToken.None }); throw new Exception("Mutating or unknown command accepted"); }
            catch (TargetInvocationException ex)
            {
                if (!(ex.InnerException is InvalidOperationException) || !ex.InnerException.Message.Contains("ZeroTier")) throw;
                count++;
            }
        }
        foreach (string command in new[] { "-j info", "-j listnetworks" })
        {
            // Cancellation happens before Process.Start. This validates the two read-only
            // commands are accepted without contacting or changing the user's service.
            var cancelled = new CancellationToken(true);
            try { run.Invoke(null, new object[] { command, cancelled }); throw new Exception("Cancellation ignored"); }
            catch (TargetInvocationException ex) { if (!(ex.InnerException is OperationCanceledException)) throw; count++; }
        }
        Console.WriteLine("PASS: " + count + " ZeroTier read-only contract checks; no service/network commands executed.");
        return 0;
    }
}
