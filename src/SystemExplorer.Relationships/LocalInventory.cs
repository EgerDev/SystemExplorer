using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using SystemExplorer.Core;

namespace SystemExplorer.Relationships;

internal static class LocalInventory
{
    internal static IReadOnlyList<StartupItem> GetStartupItems(CancellationToken token)
    {
        var items = new List<StartupItem>();
        var views = Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 };
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in views)
        foreach (string keyName in new[] { "Run", "RunOnce" })
        {
            token.ThrowIfCancellationRequested();
            using var root = RegistryKey.OpenBaseKey(hive, view);
            string path = $@"Software\Microsoft\Windows\CurrentVersion\{keyName}";
            using var key = root.OpenSubKey(path, writable: false);
            if (key is null) continue;
            foreach (string name in key.GetValueNames())
            {
                token.ThrowIfCancellationRequested();
                if (items.Count >= 4096) throw new NotSupportedException("Startup inventory exceeds the 4096-entry bound; no complete inventory returned.");
                object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value is not string command) throw new InvalidDataException("Startup registry value is missing or not a command string; inventory may have changed.");
                items.Add(new StartupItem(name, command, $"{hive}\\{path} ({view})", hive == RegistryHive.CurrentUser ? "Current user" : "Machine"));
            }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            token.ThrowIfCancellationRequested();
            string path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrWhiteSpace(path)) throw new NotSupportedException("Windows did not provide a startup-folder path.");
            try { RequireLocalPath(path); }
            catch (DirectoryNotFoundException) { continue; }
            catch (FileNotFoundException) { continue; }
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                token.ThrowIfCancellationRequested();
                if (items.Count >= 4096) throw new NotSupportedException("Startup inventory exceeds the 4096-entry bound; no complete inventory returned.");
                // Inventory the entry itself; never resolve shortcuts, expand commands or execute anything.
                items.Add(new StartupItem(Path.GetFileName(entry), entry, $"Startup folder ({folder}); target not resolved",
                    folder == Environment.SpecialFolder.Startup ? "Current user" : "Machine"));
            }
        }
        token.ThrowIfCancellationRequested();
        return items.ToArray();
    }

    internal static PublisherInfo GetPublisher(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            string localPath = RequireLocalPath(path);
            using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 512L * 1024 * 1024) return new(null, "Unsupported: publisher verification is bounded to files <=512 MiB.");
            token.ThrowIfCancellationRequested();
            return Verify(file, localPath, token);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return new(null, "Access denied: publisher verification unavailable.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(null, "Unavailable: file no longer exists.");
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return new(null, "Unsupported: only fully qualified, fixed local-drive paths without reparse points are verified.");
        }
        catch (IOException)
        {
            return new(null, "Unavailable: file could not be read or locked for verification.");
        }
        catch (EntryPointNotFoundException)
        {
            return new(null, "Unsupported: local trust-provider API is unavailable.");
        }
    }

    private static PublisherInfo Verify(FileStream file, string path, CancellationToken token)
    {
        IntPtr pathMemory = Marshal.StringToHGlobalUni(path);
        IntPtr infoMemory = IntPtr.Zero;
        try
        {
            var info = new NativeMethods.WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WinTrustFileInfo>(),
                Path = pathMemory,
                File = file.SafeFileHandle.DangerousGetHandle()
            };
            infoMemory = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.WinTrustFileInfo>());
            Marshal.StructureToPtr(info, infoMemory, false);
            var data = new NativeMethods.WinTrustData
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WinTrustData>(),
                UiChoice = 2,
                UnionChoice = 1,
                FileInfo = infoMemory,
                StateAction = 1,
                ProviderFlags = 0x1000 | 0x80 | 0x2000
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            try
            {
                int status = NativeMethods.WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                token.ThrowIfCancellationRequested();
                if (status == 0)
                    return new(ReadVerifiedSigner(data.State), "Verified embedded signature (offline cache-only revocation; not a fresh online revocation check).");
                if (unchecked((uint)status) == 0x800B0100)
                    return new(null, "No embedded signature verified; catalog-signature verification is unsupported.");
                return new(null, $"Not verified offline (WinVerifyTrust 0x{unchecked((uint)status):X8}); revocation cache may be incomplete.");
            }
            finally
            {
                data.StateAction = 2;
                NativeMethods.WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
        }
        finally
        {
            if (infoMemory != IntPtr.Zero) Marshal.FreeHGlobal(infoMemory);
            Marshal.FreeHGlobal(pathMemory);
        }
    }

    private static string? ReadVerifiedSigner(IntPtr state)
    {
        IntPtr provider = NativeMethods.WTHelperProvDataFromStateData(state);
        if (provider == IntPtr.Zero) return null;
        IntPtr signerPointer = NativeMethods.WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signerPointer == IntPtr.Zero || Marshal.ReadInt32(signerPointer) < Marshal.SizeOf<NativeMethods.ProviderSignerPrefix>()) return null;
        var signer = Marshal.PtrToStructure<NativeMethods.ProviderSignerPrefix>(signerPointer);
        if (signer.CertificateCount == 0 || signer.Certificates == IntPtr.Zero ||
            Marshal.ReadInt32(signer.Certificates) < Marshal.SizeOf<NativeMethods.ProviderCertificatePrefix>()) return null;
        var certificate = Marshal.PtrToStructure<NativeMethods.ProviderCertificatePrefix>(signer.Certificates);
        if (certificate.Certificate == IntPtr.Zero) return null;
        uint count = NativeMethods.CertGetNameStringW(certificate.Certificate, 4, 0, IntPtr.Zero, null, 0);
        if (count is <= 1 or > 4096) return null;
        var name = new StringBuilder((int)count);
        uint written = NativeMethods.CertGetNameStringW(certificate.Certificate, 4, 0, IntPtr.Zero, name, count);
        return written > 1 && written <= count ? name.ToString() : null;
    }

    private static string RequireLocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.AsSpan(2).Contains(':'))
            throw new NotSupportedException("A fixed local-drive path is required.");
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)!;
        if (new DriveInfo(root).DriveType != DriveType.Fixed) throw new NotSupportedException("A fixed local drive is required.");
        string current = root;
        foreach (string component in fullPath[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("Reparse-point paths are not followed.");
        }
        return fullPath;
    }
}
