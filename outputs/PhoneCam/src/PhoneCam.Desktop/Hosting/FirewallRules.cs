using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PhoneCam.Desktop.Hosting;

/// <summary>
/// Inbound allow rules. Rules are per program (so Windows does not show its own prompt, whose "Cancel"
/// would create blocking rules) and per port. The rule name carries a hash of the exe path, so moving the
/// app produces a new rule set. LAN rules cover the private profile only; the iPhone USB network usually
/// lands in the public profile, so a second set allows the same ports there, but only on wired adapters and
/// only from the Personal Hotspot subnet.
/// </summary>
internal static class FirewallRules
{
    private sealed record Rule(string Name, string Program, string Protocol, string Ports, string Scope);

    private const string LanScope = "profile=private";
    private static readonly string UsbScope = $"profile=public interfacetype=lan remoteip={IphoneUsb.HotspotSubnet}";

    private static IEnumerable<Rule> Rules()
    {
        foreach ((string prefix, string scope) in new[] { ("PhoneCam", LanScope), ("PhoneCam USB", UsbScope) })
        {
            yield return new Rule($"{prefix} app [{PathTag(AppPaths.AppExe)}]", AppPaths.AppExe, "TCP", $"{Ports.PhonePage},{Ports.CertificateDownload}", scope);
            yield return new Rule($"{prefix} MediaMTX TCP [{PathTag(AppPaths.MediaMtxExe)}]", AppPaths.MediaMtxExe, "TCP", $"{Ports.Whip}", scope);
            yield return new Rule($"{prefix} MediaMTX UDP [{PathTag(AppPaths.MediaMtxExe)}]", AppPaths.MediaMtxExe, "UDP", $"{Ports.WebRtcUdp}", scope);
        }
    }

    public static bool AreConfigured() => Rules().All(rule => RuleExists(rule.Name));

    /// <summary>cmd lines for the elevated setup script (see <see cref="Elevated"/>).</summary>
    public static string SetupCommands()
    {
        var lines = new StringBuilder();
        foreach (Rule rule in Rules())
        {
            lines.Append($"netsh advfirewall firewall delete rule name=\"{rule.Name}\" >nul 2>&1\r\n");
            lines.Append($"netsh advfirewall firewall add rule name=\"{rule.Name}\" dir=in action=allow {rule.Scope} " +
                $"program=\"{rule.Program}\" protocol={rule.Protocol} localport={rule.Ports} enable=yes\r\n");
        }
        return lines.ToString();
    }

    private static string PathTag(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())))[..8];

    private static bool RuleExists(string name)
    {
        var startInfo = new ProcessStartInfo("netsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "advfirewall", "firewall", "show", "rule", $"name={name}" })
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}
