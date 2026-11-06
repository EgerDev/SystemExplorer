using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SystemExplorer.Core;

[assembly: InternalsVisibleTo("SystemExplorer.ProcessSecurity.Smoke")]

namespace SystemExplorer.Windows;

public sealed class ImageSignatureVerifier : IImageSignatureVerifier
{
    internal const int CacheCapacity = 256;
    private readonly IImageTrustNative native;
    private readonly object gate = new();
    private readonly LinkedList<(CacheKey Key, ImageProvenance Value)> lru = new();
    private readonly Dictionary<CacheKey, LinkedListNode<(CacheKey Key, ImageProvenance Value)>> cache = new();
    private readonly record struct CacheKey(string Path, long Size, long Ticks, string Sha256);

    public ImageSignatureVerifier() : this(new ImageTrustNative()) { }
    internal ImageSignatureVerifier(IImageTrustNative native) => this.native = native;

    public Task<ImageProvenance> InspectAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => Inspect(path, cancellationToken), cancellationToken);

    internal static SignatureState MapHResult(int result, bool catalog = false) => result switch
    {
        0 => catalog ? SignatureState.CatalogTrusted : SignatureState.EmbeddedTrusted,
        unchecked((int)0x800B0100) or unchecked((int)0x800B0003) or unchecked((int)0x800B0001) => SignatureState.Unsigned,
        _ => SignatureState.NotTrusted
    };

    private ImageProvenance Inspect(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var attempted = false;
        long? size = null;
        DateTimeOffset? modified = null;
        string? company = null, product = null, description = null, version = null, productVersion = null;
        ImageProvenance Result(InspectionStatus status, string detail, SignatureState signature, int? hr = null,
            string? signer = null, string? catalog = null) => new(path, status, detail, size, modified,
                company, product, description, version, productVersion, signature, hr, signer, catalog, DateTimeOffset.UtcNow);
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException();
        }
        try
        {
            path = Path.GetFullPath(path);
            var info = new FileInfo(path);
            size = info.Length;
            modified = new DateTimeOffset(info.LastWriteTimeUtc);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            Check();
            // Content digest keys the cache so a replaced file with identical size and timestamp is re-verified.
            var digest = "";
            if (size <= 512L * 1024 * 1024)
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    Check();
                }
                digest = Convert.ToHexString(sha.GetHashAndReset());
            }
            file.Position = 0;
            Check();
            var key = new CacheKey(path.ToUpperInvariant(), size.Value, modified.Value.UtcTicks, digest);
            lock (gate)
            {
                if (digest.Length > 0 && cache.TryGetValue(key, out var cached))
                {
                    lru.Remove(cached);
                    lru.AddFirst(cached);
                    return cached.Value.Value;
                }
            }
            var metadata = FileVersionInfo.GetVersionInfo(path);
            static string? Present(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
            company = Present(metadata.CompanyName);
            product = Present(metadata.ProductName);
            description = Present(metadata.FileDescription);
            version = Present(metadata.FileVersion);
            productVersion = Present(metadata.ProductVersion);
            Check();
            attempted = true;
            var trust = native.Verify(path, file.SafeFileHandle.DangerousGetHandle(), null, null, IntPtr.Zero);
            Check();
            var state = MapHResult(trust.HResult);
            string? catalogPath = null;
            if (state == SignatureState.Unsigned)
            {
                if (size > 512L * 1024 * 1024)
                    return Result(InspectionStatus.Truncated, "Catalog hashing exceeds the 512 MiB input bound; trust was not established.", SignatureState.NotTrusted, trust.HResult);
                var catalog = native.VerifyCatalog(path, file.SafeFileHandle.DangerousGetHandle(), Check);
                Check();
                trust = catalog.Trust;
                catalogPath = catalog.Path;
                state = catalogPath is null ? SignatureState.Unsigned : MapHResult(trust.HResult, true);
            }
            var result = Result(InspectionStatus.Available,
                "Local signature validation only; online retrieval and revocation are disabled.", state, trust.HResult, trust.Signer, catalogPath);
            lock (gate)
            {
                if (cache.Remove(key, out var previous)) lru.Remove(previous);
                var node = lru.AddFirst((key, result));
                cache.Add(key, node);
                if (cache.Count > CacheCapacity)
                {
                    cache.Remove(lru.Last!.Value.Key);
                    lru.RemoveLast();
                }
            }
            return result;
        }
        catch (TimeoutException)
        {
            return Result(InspectionStatus.TimedOut, "Five-second cooperative budget reached; blocking native calls cannot be interrupted. Trust was not established.", attempted ? SignatureState.NotTrusted : SignatureState.NotChecked);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            return Result(error is UnauthorizedAccessException ? InspectionStatus.AccessDenied : InspectionStatus.Failed,
                $"Image could not be read: {error.Message}", SignatureState.Unreadable);
        }
        catch (Exception error) when (error is Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Result(InspectionStatus.Failed, $"Local signature inspection failed: {error.Message}", attempted ? SignatureState.NotTrusted : SignatureState.NotChecked);
        }
    }

    internal readonly record struct TrustResult(int HResult, string? Signer);
    internal readonly record struct CatalogResult(string? Path, TrustResult Trust);
    internal interface IImageTrustNative
    {
        TrustResult Verify(string path, IntPtr file, string? catalog, string? tag, IntPtr admin);
        CatalogResult VerifyCatalog(string path, IntPtr file, Action check);
    }

    private sealed class ImageTrustNative : IImageTrustNative
    {
        public TrustResult Verify(string path, IntPtr file, string? catalog, string? tag, IntPtr admin)
        {
            var information = IntPtr.Zero;
            var data = new NativeSecurityMethods.TrustData
            {
                Size = (uint)Marshal.SizeOf<NativeSecurityMethods.TrustData>(), Ui = 2, Choice = catalog is null ? 1u : 2u,
                StateAction = 1, Flags = 0x1000 | 0x10
            };
            try
            {
                if (catalog is null)
                {
                    var value = new NativeSecurityMethods.TrustFile { Size = (uint)Marshal.SizeOf<NativeSecurityMethods.TrustFile>(), Path = path, File = file };
                    information = Marshal.AllocHGlobal(Marshal.SizeOf<NativeSecurityMethods.TrustFile>());
                    Marshal.StructureToPtr(value, information, false);
                }
                else
                {
                    var value = new NativeSecurityMethods.TrustCatalog
                    {
                        Size = (uint)Marshal.SizeOf<NativeSecurityMethods.TrustCatalog>(), CatalogPath = catalog,
                        MemberTag = tag!, MemberPath = path, File = file, Admin = admin
                    };
                    information = Marshal.AllocHGlobal(Marshal.SizeOf<NativeSecurityMethods.TrustCatalog>());
                    Marshal.StructureToPtr(value, information, false);
                }
                data.Information = information;
                var hr = NativeSecurityMethods.WinVerifyTrust(new IntPtr(-1), NativeSecurityMethods.VerifyAction, ref data);
                return new(hr, ReadSigner(data.State));
            }
            finally
            {
                try
                {
                    if (data.Information != IntPtr.Zero)
                    {
                        data.StateAction = 2;
                        NativeSecurityMethods.WinVerifyTrust(new IntPtr(-1), NativeSecurityMethods.VerifyAction, ref data);
                    }
                }
                finally
                {
                    if (information != IntPtr.Zero)
                    {
                        if (catalog is null) Marshal.DestroyStructure<NativeSecurityMethods.TrustFile>(information);
                        else Marshal.DestroyStructure<NativeSecurityMethods.TrustCatalog>(information);
                        Marshal.FreeHGlobal(information);
                    }
                }
            }
        }

        public CatalogResult VerifyCatalog(string path, IntPtr file, Action check)
        {
            IntPtr admin = IntPtr.Zero, catalog = IntPtr.Zero;
            try
            {
                check();
                if (!NativeSecurityMethods.CryptCATAdminAcquireContext2(out admin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                check();
                uint size = 0;
                if (!NativeSecurityMethods.CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, null, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (size is 0 or > 128) throw new Win32Exception("Invalid catalog hash size.");
                var hash = new byte[size];
                check();
                if (!NativeSecurityMethods.CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, hash, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                check();
                catalog = NativeSecurityMethods.CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, IntPtr.Zero);
                if (catalog == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is not (0 or 1168)) throw new Win32Exception(error);
                    return new(null, new(unchecked((int)0x800B0100), null));
                }
                var info = new NativeSecurityMethods.CatalogInfo { Size = (uint)Marshal.SizeOf<NativeSecurityMethods.CatalogInfo>(), Path = "" };
                if (!NativeSecurityMethods.CryptCATAdminCatalogInfoFromContext(catalog, ref info, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                check();
                return new(info.Path, Verify(path, file, info.Path, Convert.ToHexString(hash.AsSpan(0, (int)size)), admin));
            }
            finally
            {
                if (catalog != IntPtr.Zero) NativeSecurityMethods.CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
                if (admin != IntPtr.Zero) NativeSecurityMethods.CryptCATAdminReleaseContext(admin, 0);
            }
        }

        private static string? ReadSigner(IntPtr state)
        {
            if (state == IntPtr.Zero) return null;
            var provider = NativeSecurityMethods.WTHelperProvDataFromStateData(state);
            if (provider == IntPtr.Zero) return null;
            var signer = NativeSecurityMethods.WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == IntPtr.Zero) return null;
            var certificate = NativeSecurityMethods.WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == IntPtr.Zero) return null;
            var context = Marshal.PtrToStructure<NativeSecurityMethods.ProviderCertificate>(certificate).Certificate;
            if (context == IntPtr.Zero) return null;
            var size = NativeSecurityMethods.CertGetNameStringW(context, 4, 0, IntPtr.Zero, null, 0);
            if (size is <= 1 or > 32768) return null;
            var name = new StringBuilder((int)size);
            return NativeSecurityMethods.CertGetNameStringW(context, 4, 0, IntPtr.Zero, name, size) > 1 ? name.ToString() : null;
        }
    }
}
