using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HeirowInstaller.Core;

public sealed record WindowsPublisherIdentity(string Subject, string Thumbprint);

/// <summary>Read-only Windows Authenticode verification. Never signs files or accesses a signing account.</summary>
public static class WindowsPublisherTrust
{
    public static WindowsPublisherIdentity Verify(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows installer trust must be verified on Windows.");
        path = Path.GetFullPath(path);
        using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var info = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path, Handle = guard.SafeFileHandle.DangerousGetHandle() };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, RevocationChecks = 1, UnionChoice = 1,
            File = pointer, StateAction = 1,
            // Whole-chain revocation excluding the root; disable obsolete MD2/MD4 digests.
            ProviderFlags = 0x80 | 0x2000
        };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0)
                throw new InvalidDataException("Windows publisher verification failed. Sign this Windows installer with your own publicly trusted code-signing identity before hosting it on SocketJack.");
#pragma warning disable SYSLIB0057 // Reads the Authenticode signer rather than a standalone certificate file.
            using var embedded = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var signer = new X509Certificate2(embedded);
            if (signer.SubjectName.RawData.AsSpan().SequenceEqual(signer.IssuerName.RawData) ||
                !signer.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.3")))
                throw new InvalidDataException("A self-signed or non-code-signing certificate does not establish a trusted Windows publisher.");
            // WinVerifyTrust checks timestamp validity, including signatures made before leaf expiry.
            return new(signer.Subject, signer.Thumbprint);
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr Handle, KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData, SipClientData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UrlReference;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
