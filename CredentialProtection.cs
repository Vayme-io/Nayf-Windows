using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NayfWindows;

/// <summary>
/// Encrypts small secrets before they are written to disk, using Windows DPAPI
/// (the crypt32 CryptProtectData family) tied to the signed-in Windows user.
/// Today the only caller is <see cref="AuthManager"/>, which stores the Supabase
/// access and refresh tokens.
///
/// This is the Windows counterpart to the Keychain on macOS, where the same
/// session lives under the service name com.vayme.nayf.auth.
///
/// The key is derived and held by Windows from the user's logon credentials, so
/// nothing app-side has to store or ship a key.
///
/// What this does and does not protect against, stated plainly so nobody assumes
/// more than it gives:
///
/// PROTECTED: the file is no longer readable text. A different Windows account on
/// the machine, a backup or a cloud-sync copy, a drive pulled out of the machine,
/// or anyone glancing at the folder gets ciphertext rather than a working refresh
/// token that would still be valid weeks later.
///
/// NOT PROTECTED: a process already running as this same user can call
/// CryptUnprotectData itself and read the token back. DPAPI's user scope draws its
/// boundary at the Windows account, not at the application. Defending across that
/// line would need a password the user types on every launch, which is a different
/// product decision and not one this change makes.
///
/// The bytes produced are opaque and versioned by Windows itself; they are not a
/// format this app has to maintain.
/// </summary>
internal static class CredentialProtection
{
    /// <summary>
    /// Extra entropy mixed into the encryption, so ciphertext written by Vayme can
    /// only be decrypted by a caller that supplies the same value. This is not a
    /// secret — it is compiled into the binary and anyone can read it out — but it
    /// stops another application on the machine from decrypting Vayme's file
    /// merely by handing the bytes to DPAPI with no entropy at all.
    ///
    /// Deliberately the same string macOS uses as its Keychain service name, so the
    /// two platforms name the same secret the same way.
    ///
    /// Changing this value makes every already-stored session undecryptable, which
    /// signs every existing user out. Do not change it.
    /// </summary>
    private static readonly byte[] ApplicationEntropy = Encoding.UTF8.GetBytes("io.vayme.nayf.auth");

    /// <summary>Suppress any UI: this runs during startup, with no window to parent to.</summary>
    private const int CryptProtectUIForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptographyDataBlob
    {
        public int ByteCount;
        public IntPtr ByteBuffer;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref CryptographyDataBlob input,
        string? description,
        ref CryptographyDataBlob optionalEntropy,
        IntPtr reservedMustBeZero,
        IntPtr promptStruct,
        int flags,
        out CryptographyDataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref CryptographyDataBlob input,
        IntPtr descriptionOutIgnored,
        ref CryptographyDataBlob optionalEntropy,
        IntPtr reservedMustBeZero,
        IntPtr promptStruct,
        int flags,
        out CryptographyDataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>
    /// Encrypts <paramref name="plaintextBytes"/> for the current Windows user.
    /// Throws if DPAPI refuses, so a failure to encrypt can never be mistaken for
    /// a successful save that quietly wrote the secret in the clear.
    /// </summary>
    public static byte[] EncryptForCurrentUser(byte[] plaintextBytes)
    {
        return CallDataProtectionApi(plaintextBytes, isEncrypting: true);
    }

    /// <summary>
    /// Decrypts bytes previously produced by <see cref="EncryptForCurrentUser"/>.
    /// Throws when the bytes were not written by this user, were written with
    /// different entropy, or are not DPAPI output at all — which is exactly how the
    /// caller detects a pre-encryption plaintext file and migrates it.
    /// </summary>
    public static byte[] DecryptForCurrentUser(byte[] encryptedBytes)
    {
        return CallDataProtectionApi(encryptedBytes, isEncrypting: false);
    }

    /// <summary>
    /// Both directions have identical marshalling and cleanup, and getting that
    /// cleanup wrong leaks native memory holding a decrypted token. Writing it once
    /// is what keeps the two paths honest with each other.
    /// </summary>
    private static byte[] CallDataProtectionApi(byte[] inputBytes, bool isEncrypting)
    {
        ArgumentNullException.ThrowIfNull(inputBytes);

        var inputBlob = default(CryptographyDataBlob);
        var entropyBlob = default(CryptographyDataBlob);
        var outputBlob = default(CryptographyDataBlob);

        try
        {
            inputBlob = AllocateBlob(inputBytes);
            entropyBlob = AllocateBlob(ApplicationEntropy);

            bool succeeded = isEncrypting
                ? CryptProtectData(
                    ref inputBlob,
                    // Shows up in Windows' own credential UI. Never displayed here,
                    // but a labelled blob is easier to identify when debugging.
                    "Vayme sign-in session",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUIForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUIForbidden,
                    out outputBlob);

            if (!succeeded)
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    isEncrypting
                        ? "CryptProtectData failed; refusing to store the session unencrypted."
                        : "CryptUnprotectData failed; the stored session cannot be read by this user.");
            }

            var resultBytes = new byte[outputBlob.ByteCount];
            Marshal.Copy(outputBlob.ByteBuffer, resultBytes, 0, outputBlob.ByteCount);
            return resultBytes;
        }
        finally
        {
            FreeBlob(ref inputBlob, Marshal.FreeHGlobal);
            FreeBlob(ref entropyBlob, Marshal.FreeHGlobal);

            // The output buffer is allocated by crypt32, so it must go back through
            // LocalFree rather than the allocator used for our own two blobs.
            FreeBlob(ref outputBlob, handle => LocalFree(handle));
        }
    }

    private static CryptographyDataBlob AllocateBlob(byte[] bytes)
    {
        var blob = new CryptographyDataBlob
        {
            ByteCount = bytes.Length,
            ByteBuffer = Marshal.AllocHGlobal(bytes.Length)
        };
        Marshal.Copy(bytes, 0, blob.ByteBuffer, bytes.Length);
        return blob;
    }

    /// <summary>
    /// Overwrites the buffer before releasing it. The input blob on the decrypt path
    /// and the output blob on the decrypt path both hold token material, and freed
    /// native memory is not zeroed by anyone else.
    /// </summary>
    private static void FreeBlob(ref CryptographyDataBlob blob, Action<IntPtr> release)
    {
        if (blob.ByteBuffer == IntPtr.Zero) return;

        for (int offset = 0; offset < blob.ByteCount; offset++)
        {
            Marshal.WriteByte(blob.ByteBuffer, offset, 0);
        }

        release(blob.ByteBuffer);
        blob.ByteBuffer = IntPtr.Zero;
        blob.ByteCount = 0;
    }
}
