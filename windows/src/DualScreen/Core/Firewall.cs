using System;
using System.Diagnostics;
using System.Linq;

namespace DualScreen.Core;

/// <summary>
/// Windows Defender Firewall integration. Incoming LAN connections need an inbound allow rule for DualScreen.exe;
/// without it Windows silently drops the phone's connection (remote/WebRTC still works). Reading the rules needs
/// no privileges; adding them runs in the elevated helper ("DualScreen.exe --driver firewall").
/// </summary>
public static class Firewall
{
    private const string RuleName = "DualScreen";

    /// <summary>True when an enabled inbound allow rule for this exe exists and no inbound block rule targets it.</summary>
    public static bool IsAllowed()
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            bool allow = false, block = false;
            foreach (dynamic rule in policy.Rules)
            {
                string? app = rule.ApplicationName;
                if (app == null || !string.Equals(app, AppPaths.ExePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!(bool)rule.Enabled || (int)rule.Direction != 1 /* inbound */) continue;
                if ((int)rule.Action == 0) block = true; else allow = true;
            }
            return allow && !block;
        }
        catch (Exception ex)
        {
            Log.Warn("Cannot read firewall rules: " + ex.Message);
            return true; // unknown: do not nag
        }
    }

    /// <summary>Runs elevated. Removes block rules Windows created for this exe and adds allow rules (TCP + discovery UDP).</summary>
    public static string Configure()
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        var stale = new System.Collections.Generic.List<string>();
        foreach (dynamic rule in policy.Rules)
        {
            string? app = rule.ApplicationName;
            if (app != null && string.Equals(app, AppPaths.ExePath, StringComparison.OrdinalIgnoreCase)) stale.Add((string)rule.Name);
        }
        foreach (string name in stale.Distinct()) policy.Rules.Remove(name);

        int port = SettingsStore.Current.LanPort;
        Netsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{AppPaths.ExePath}\" protocol=TCP localport={port} profile=any enable=yes");
        Netsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{AppPaths.ExePath}\" protocol=UDP localport={AppInfo.DiscoveryPort} profile=any enable=yes");
        Log.Info($"Firewall configured (removed {stale.Count} old rules)");
        return "Conexiones en red local permitidas en el firewall de Windows.";
    }

    private static void Netsh(string args)
    {
        var psi = new ProcessStartInfo("netsh.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("netsh: " + output.Trim());
    }
}
