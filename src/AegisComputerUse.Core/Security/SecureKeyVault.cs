using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace AegisComputerUse.Core.Security;

[SupportedOSPlatform("windows")]
public sealed class SecureKeyVault
{
    private static readonly byte[] MagicHeader = "AEGISV1\0"u8.ToArray();
    private static readonly byte[] AppEntropy = "AegisComputerUse::DPAPI::Vault::2026::v1"u8.ToArray();
    private const int Pbkdf2Iterations = 600_000;
    private const byte ModeDpapiOnly = 0x01;
    private const byte ModeDpapiWithPassphrase = 0x02;

    public string VaultDirectory { get; }
    public string VaultFilePath { get; }

    public SecureKeyVault(string? customDirectory = null)
    {
        VaultDirectory = string.IsNullOrWhiteSpace(customDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AegisComputerUse", "Vault")
            : Path.GetFullPath(customDirectory);
        VaultFilePath = Path.Combine(VaultDirectory, "api_key.vault");
    }

    public bool VaultExists => File.Exists(VaultFilePath);

    public bool RequiresPassphrase()
    {
        if (!VaultExists) return false;
        byte[] outer = UnprotectOuterEnvelope();
        try
        {
            ValidateHeader(outer);
            return outer[8] == ModeDpapiWithPassphrase;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(outer);
        }
    }

    public void SaveKey(SealedSecret secret, ReadOnlySpan<char> masterPassphrase = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        EnsureRestrictedDirectory();

        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte mode = masterPassphrase.IsEmpty ? ModeDpapiOnly : ModeDpapiWithPassphrase;
        byte[] aesKey = DeriveInnerKey(masterPassphrase, salt, mode);

        byte[] innerEnvelope;
        try
        {
            innerEnvelope = secret.UseSecret(secretChars =>
            {
                byte[] secretBytes = new byte[Encoding.UTF8.GetMaxByteCount(secretChars.Length)];
                byte[] tag = new byte[16];
                try
                {
                    int secretLen = Encoding.UTF8.GetBytes(secretChars, secretBytes);
                    ReadOnlySpan<byte> payload = secretBytes.AsSpan(0, secretLen);
                    byte[] cipher = new byte[payload.Length];
                    try
                    {
                        using var aes = new AesGcm(aesKey, 16);
                        aes.Encrypt(nonce, payload, cipher, tag, MagicHeader);

                        byte[] envelope = new byte[8 + 1 + 32 + 12 + 16 + cipher.Length];
                        Buffer.BlockCopy(MagicHeader, 0, envelope, 0, 8);
                        envelope[8] = mode;
                        Buffer.BlockCopy(salt, 0, envelope, 9, 32);
                        Buffer.BlockCopy(nonce, 0, envelope, 41, 12);
                        Buffer.BlockCopy(tag, 0, envelope, 53, 16);
                        Buffer.BlockCopy(cipher, 0, envelope, 69, cipher.Length);
                        return envelope;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(cipher);
                        CryptographicOperations.ZeroMemory(tag);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secretBytes);
                }
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
        }

        byte[] dpapiProtected;
        try
        {
            dpapiProtected = ProtectedData.Protect(innerEnvelope, AppEntropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(innerEnvelope);
        }

        string tempFile = Path.Combine(VaultDirectory, $".tmp_{Guid.NewGuid():N}.vault");
        try
        {
            using (var fs = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(dpapiProtected);
                fs.Flush(flushToDisk: true);
            }

            ApplyStrictFileAcl(tempFile);
            File.Move(tempFile, VaultFilePath, overwrite: true);
            ApplyStrictFileAcl(VaultFilePath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dpapiProtected);
            if (File.Exists(tempFile))
            {
                SecureDeleteFile(tempFile);
            }
        }
    }

    public SealedSecret LoadKey(ReadOnlySpan<char> masterPassphrase = default)
    {
        if (!VaultExists)
            throw new FileNotFoundException("Encrypted vault file does not exist.", VaultFilePath);

        byte[] outer = UnprotectOuterEnvelope();
        try
        {
            ValidateHeader(outer);
            byte mode = outer[8];
            if (mode == ModeDpapiWithPassphrase && masterPassphrase.IsEmpty)
                throw new CryptographicException("Master passphrase is required to unlock the API key vault.");

            ReadOnlySpan<byte> salt = outer.AsSpan(9, 32);
            ReadOnlySpan<byte> nonce = outer.AsSpan(41, 12);
            ReadOnlySpan<byte> tag = outer.AsSpan(53, 16);
            ReadOnlySpan<byte> cipher = outer.AsSpan(69);

            byte[] aesKey = DeriveInnerKey(masterPassphrase, salt, mode);
            byte[] plainBytes = new byte[cipher.Length];
            char[] plainChars = new char[Encoding.UTF8.GetMaxCharCount(cipher.Length)];

            try
            {
                using var aes = new AesGcm(aesKey, 16);
                aes.Decrypt(nonce, cipher, tag, plainBytes, MagicHeader);
                int charLen = Encoding.UTF8.GetChars(plainBytes, plainChars);
                return SealedSecret.Create(plainChars.AsSpan(0, charLen));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(aesKey);
                CryptographicOperations.ZeroMemory(plainBytes);
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(plainChars.AsSpan()));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(outer);
        }
    }

    public void DeleteVault()
    {
        if (File.Exists(VaultFilePath))
        {
            SecureDeleteFile(VaultFilePath);
        }
    }

    private byte[] UnprotectOuterEnvelope()
    {
        byte[] raw = File.ReadAllBytes(VaultFilePath);
        try
        {
            return ProtectedData.Unprotect(raw, AppEntropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    private static void ValidateHeader(byte[] envelope)
    {
        if (envelope.Length < 70 || !envelope.AsSpan(0, 8).SequenceEqual(MagicHeader))
            throw new CryptographicException("Vault envelope failed integrity verification.");

        byte mode = envelope[8];
        if (mode != ModeDpapiOnly && mode != ModeDpapiWithPassphrase)
            throw new CryptographicException("Unsupported vault encryption mode.");
    }

    private static byte[] DeriveInnerKey(ReadOnlySpan<char> masterPassphrase, ReadOnlySpan<byte> salt, byte mode)
    {
        if (mode == ModeDpapiOnly)
        {
            Span<byte> material = stackalloc byte[AppEntropy.Length + salt.Length];
            AppEntropy.CopyTo(material);
            salt.CopyTo(material.Slice(AppEntropy.Length));
            return SHA256.HashData(material);
        }

        byte[] passBytes = new byte[Encoding.UTF8.GetMaxByteCount(masterPassphrase.Length)];
        try
        {
            int len = Encoding.UTF8.GetBytes(masterPassphrase, passBytes);
            return Rfc2898DeriveBytes.Pbkdf2(passBytes.AsSpan(0, len), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passBytes);
        }
    }

    private void EnsureRestrictedDirectory()
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot resolve current Windows user SID.");

        Directory.CreateDirectory(VaultDirectory);

        var dirInfo = new DirectoryInfo(VaultDirectory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        dirInfo.SetAccessControl(security);
    }

    private static void ApplyStrictFileAcl(string path)
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot resolve current Windows user SID.");

        var fileInfo = new FileInfo(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        fileInfo.SetAccessControl(security);
    }

    private static void SecureDeleteFile(string path)
    {
        try
        {
            var len = new FileInfo(path).Length;
            if (len > 0 && len <= 1024 * 1024)
            {
                byte[] wipe = new byte[len];
                RandomNumberGenerator.Fill(wipe);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(wipe);
                    fs.Flush(true);
                }
                CryptographicOperations.ZeroMemory(wipe);
            }
        }
        catch
        {
            // Proceed to delete even if overwrite failed.
        }
        File.Delete(path);
    }
}
