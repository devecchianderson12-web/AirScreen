using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using DualScreen.Core;

namespace DualScreen.Display;

/// <summary>
/// Installs/removes the DualScreen Indirect Display Driver. A virtual monitor that Windows treats as real
/// REQUIRES a kernel-trusted driver package; this cannot be avoided. DualScreen.exe stays portable: the driver
/// package is embedded in the exe, extracted on demand and installed in an elevated child process
/// ("DualScreen.exe --driver install"), with a single UAC prompt. Uninstall is symmetrical.
/// </summary>
public enum DriverStatus { NotInstalled, Ok, Outdated, Failed, AccessDenied }

public static class DriverInstaller
{
    private static readonly Guid DisplayClassGuid = new("4d36e968-e325-11ce-bfc1-08002be10318");

    public static bool HasEmbeddedPackage =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames().Any(n => n.EndsWith("DualScreenIdd.inf", StringComparison.OrdinalIgnoreCase));

    /// <summary>DriverVer of the package embedded in this exe (e.g. "20.25.47.321"), or null.</summary>
    public static string? EmbeddedVersion { get; } = ReadEmbeddedVersion();

    private static string? ReadEmbeddedVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string? name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("DualScreenIdd.inf", StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using var r = new StreamReader(asm.GetManifestResourceStream(name)!);
            for (string? line; (line = r.ReadLine()) != null;)
            {
                var m = Regex.Match(line, @"^\s*DriverVer\s*=\s*[^,]*,\s*([0-9.]+)", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Version of the DualScreen driver bound to the device (readable without admin rights).</summary>
    public static string? InstalledVersion()
    {
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return null;
            foreach (var sub in cls.GetSubKeyNames())
            {
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("MatchingDeviceId") is string id && id.Equals(DriverClient.HardwareId, StringComparison.OrdinalIgnoreCase))
                        return k.GetValue("DriverVersion") as string;
                }
                catch { /* some driver keys are not readable */ }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Not installed / working / older than the one in this exe / stopped by Windows / no permission.</summary>
    public static DriverStatus GetStatus()
    {
        bool node = DeviceExists();
        bool iface = DriverClient.IsInstalled();
        if (!node && !iface) return DriverStatus.NotInstalled;
        if (!iface) return DriverStatus.Failed; // device present but not started (e.g. stopped after crashes)
        if (DriverClient.AccessDenied) return DriverStatus.AccessDenied;
        string? installed = InstalledVersion();
        if (EmbeddedVersion != null && installed != null && installed != EmbeddedVersion) return DriverStatus.Outdated;
        return DriverStatus.Ok;
    }

    /// <summary>Runs the elevated helper. Returns (success, message).</summary>
    public static (bool ok, string message) RunElevated(string action)
    {
        try
        {
            var psi = new ProcessStartInfo(AppPaths.ExePath, $"--driver {action}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            string resultFile = Path.Combine(AppPaths.DriverDir, "result.txt");
            string msg = File.Exists(resultFile) ? File.ReadAllText(resultFile) : "";
            return (p.ExitCode == 0, msg);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Se canceló la solicitud de permisos de administrador.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Entry point of the elevated helper process.</summary>
    public static int ElevatedMain(string action)
    {
        string resultFile = Path.Combine(AppPaths.DriverDir, "result.txt");
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Se requieren permisos de administrador.");
            string msg = action switch
            {
                "install" => Install(),
                "uninstall" => Uninstall(),
                _ => throw new ArgumentException("Acción desconocida: " + action),
            };
            File.WriteAllText(resultFile, msg);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Driver " + action + " failed", ex);
            File.WriteAllText(resultFile, ex.Message);
            return 1;
        }
        finally { Log.Flush(); }
    }

    private static string ExtractPackage()
    {
        string dir = Path.Combine(AppPaths.DriverDir, "package");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var asm = Assembly.GetExecutingAssembly();
        foreach (string name in asm.GetManifestResourceNames().Where(n => n.StartsWith("driver/", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var f = File.Create(Path.Combine(dir, name["driver/".Length..]));
            s.CopyTo(f);
        }
        string inf = Path.Combine(dir, "DualScreenIdd.inf");
        if (!File.Exists(inf)) throw new FileNotFoundException("Este ejecutable no contiene el paquete del controlador (compilación sin driver).");
        return inf;
    }

    private static string Install()
    {
        string inf = ExtractPackage();

        // 0) The package is signed with DualScreen's own certificate (shipped next to the INF). DualScreenIdd is a
        //    user-mode (UMDF) driver, so trusting that certificate on this PC is enough for Windows to accept it:
        //    no test mode and Secure Boot stays on.
        TrustPackageCertificate(Path.GetDirectoryName(inf)!);

        // 1) Stage the package in the driver store (validates the signature/catalog).
        var (code, output) = RunTool("pnputil.exe", $"/add-driver \"{inf}\" /install");
        Log.Info("pnputil: " + output);
        if (code != 0 && code != 259 /* already up to date */ && code != 3010 /* reboot */)
            throw new InvalidOperationException(ExplainPnpUtilFailure(output));

        // 2) Create the root-enumerated device node if it does not exist (like "devcon install").
        if (!DeviceExists())
        {
            Guid cls = DisplayClassGuid;
            IntPtr set = Native.SetupDiCreateDeviceInfoList(ref cls, IntPtr.Zero);
            if (set == Native.INVALID_HANDLE_VALUE) throw new Win32Exception();
            try
            {
                var dev = new Native.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };
                if (!Native.SetupDiCreateDeviceInfo(set, "Display", ref cls, "DualScreen Virtual Display", IntPtr.Zero, Native.DICD_GENERATE_ID, ref dev))
                    throw new Win32Exception();
                byte[] hwid = Encoding.Unicode.GetBytes(DriverClient.HardwareId + "\0\0");
                if (!Native.SetupDiSetDeviceRegistryProperty(set, ref dev, Native.SPDRP_HARDWAREID, hwid, hwid.Length))
                    throw new Win32Exception();
                if (!Native.SetupDiCallClassInstaller(Native.DIF_REGISTERDEVICE, set, ref dev))
                    throw new Win32Exception();
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
        }

        // 2b) Normal users must be able to open the control interface (also repairs nodes created by 0.2.1 or older).
        ForEachDevice((set, dev) =>
        {
            byte[] sddl = Encoding.Unicode.GetBytes(DeviceSddl + "\0");
            if (!Native.SetupDiSetDeviceRegistryProperty(set, ref dev, Native.SPDRP_SECURITY_SDS, sddl, sddl.Length))
                Log.Warn($"Setting device security failed ({Marshal.GetLastWin32Error()})");
        });

        // 3) Bind the driver to the device node (this also restarts it, applying the new security).
        if (!Native.UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, DriverClient.HardwareId, inf, Native.INSTALLFLAG_FORCE, out bool reboot))
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"No se pudo instalar el controlador (error 0x{err:X}). {ExplainWin32(err)}");
        }
        // Keep only the package just installed: older DualScreen packages stay in the driver store otherwise.
        var current = Regex.Match(output, @"oem\d+\.inf", RegexOptions.IgnoreCase);
        foreach (string published in PublishedDualScreenPackages())
            if (!current.Success || !published.Equals(current.Value, StringComparison.OrdinalIgnoreCase))
                RunTool("pnputil.exe", $"/delete-driver {published}");
        Log.Info("Driver installed" + (reboot ? " (reboot required)" : ""));
        return reboot ? "Controlador instalado. Reinicia Windows para completar." : "Controlador instalado correctamente.";
    }

    private const string CertSubject = "CN=DualScreen Test Driver";
    private const string DeviceSddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)";

    private static void TrustPackageCertificate(string dir)
    {
        string cer = Path.Combine(dir, "DualScreenTest.cer");
        if (!File.Exists(cer)) return; // signed with a publicly trusted certificate: nothing to add
        using var cert = new X509Certificate2(cer);
        if (!string.Equals(cert.Subject, CertSubject, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El certificado del paquete del controlador no es el esperado.");
        foreach (var name in new[] { StoreName.Root, StoreName.TrustedPublisher })
        {
            using var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            if (store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count == 0) store.Add(cert);
        }
        Log.Info($"Driver certificate trusted ({cert.Thumbprint})");
    }

    private static void RemovePackageCertificates()
    {
        foreach (var name in new[] { StoreName.Root, StoreName.TrustedPublisher })
        {
            try
            {
                using var store = new X509Store(name, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                foreach (var c in store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, CertSubject, false))
                    store.Remove(c);
            }
            catch (Exception ex) { Log.Warn("Removing driver certificate failed: " + ex.Message); }
        }
    }

    private static string Uninstall()
    {
        int removed = 0;
        ForEachDevice((set, dev) =>
        {
            if (Native.DiUninstallDevice(IntPtr.Zero, set, ref dev, 0, out _)) removed++;
        });
        // Remove the package(s) from the driver store.
        foreach (string published in PublishedDualScreenPackages())
            RunTool("pnputil.exe", $"/delete-driver {published} /uninstall /force");
        RemovePackageCertificates();
        Log.Info($"Driver uninstalled ({removed} device nodes)");
        return "Controlador desinstalado.";
    }

    /// <summary>
    /// Published names (oemNN.inf) of DualScreen packages in the driver store. pnputil prints one block per
    /// package separated by blank lines; only blocks whose original name is dualscreenidd.inf are taken
    /// (matching across blocks could delete an unrelated driver).
    /// </summary>
    private static List<string> PublishedDualScreenPackages()
    {
        var (_, list) = RunTool("pnputil.exe", "/enum-drivers");
        var result = new List<string>();
        foreach (string block in Regex.Split(list.Replace("\r", ""), @"\n\s*\n"))
        {
            if (!block.Contains("dualscreenidd.inf", StringComparison.OrdinalIgnoreCase)) continue;
            var m = Regex.Match(block, @"oem\d+\.inf", RegexOptions.IgnoreCase);
            if (m.Success) result.Add(m.Value);
        }
        return result;
    }

    private static bool DeviceExists()
    {
        bool found = false;
        ForEachDevice((_, _) => found = true);
        return found;
    }

    private static void ForEachDevice(Action<IntPtr, Native.SP_DEVINFO_DATA> action)
    {
        Guid cls = DisplayClassGuid;
        IntPtr set = Native.SetupDiGetClassDevs(ref cls, null, IntPtr.Zero, 0);
        if (set == Native.INVALID_HANDLE_VALUE) return;
        try
        {
            var dev = new Native.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };
            for (int i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref dev); i++)
            {
                Native.SetupDiGetDeviceRegistryProperty(set, ref dev, Native.SPDRP_HARDWAREID, out _, null, 0, out int size);
                if (size <= 0) continue;
                var buf = new byte[size];
                if (!Native.SetupDiGetDeviceRegistryProperty(set, ref dev, Native.SPDRP_HARDWAREID, out _, buf, size, out _)) continue;
                string ids = Encoding.Unicode.GetString(buf);
                if (ids.Split('\0').Any(id => id.Equals(DriverClient.HardwareId, StringComparison.OrdinalIgnoreCase)))
                    action(set, dev);
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
    }

    private static (int code, string output) RunTool(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }

    private static string ExplainPnpUtilFailure(string output)
    {
        if (output.Contains("signature", StringComparison.OrdinalIgnoreCase) || output.Contains("firma", StringComparison.OrdinalIgnoreCase)
            || output.Contains("certificado", StringComparison.OrdinalIgnoreCase) || output.Contains("certificate", StringComparison.OrdinalIgnoreCase)
            || output.Contains("0xe0000242", StringComparison.OrdinalIgnoreCase) || output.Contains("0x800b0109", StringComparison.OrdinalIgnoreCase))
            return "Windows rechazó la firma del controlador aunque se instaló su certificado. Detalle de pnputil:\n" + output.Trim();
        return "pnputil falló: " + output.Trim();
    }

    private static string ExplainWin32(int err) => err switch
    {
        unchecked((int)0xE0000242) => "La firma del controlador no es de confianza (consulta docs/DRIVER.md).",
        unchecked((int)0x800B0109) => "El certificado raíz del controlador no es de confianza (consulta docs/DRIVER.md).",
        unchecked((int)0xE000020B) => "No existe el nodo de dispositivo.",
        _ => "",
    };
}
