using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AegisComputerUse.Core.Gemini;

namespace AegisComputerUse.Core.Security;

public sealed class ConfiguredModelEntry : IDisposable
{
    public string EntryId { get; }
    public AiProviderKind Provider { get; }
    public string ModelId { get; }
    public string DisplayName { get; }
    public string ThinkingLevel { get; set; }
    public bool SavedInVault { get; set; }
    public SealedSecret Secret { get; }

    public ConfiguredModelEntry(
        string entryId,
        AiProviderKind provider,
        string modelId,
        string displayName,
        string thinkingLevel,
        bool savedInVault,
        SealedSecret secret)
    {
        EntryId = entryId;
        Provider = provider;
        ModelId = modelId;
        DisplayName = displayName;
        ThinkingLevel = thinkingLevel;
        SavedInVault = savedInVault;
        Secret = secret;
    }

    public void Dispose() => Secret.Dispose();
}

[SupportedOSPlatform("windows")]
public sealed class MultiModelVault
{
    private static readonly byte[] MagicHeader = "AEGISM2\0"u8.ToArray();
    private static readonly byte[] AppEntropy = "AegisComputerUse::DPAPI::MultiModel::2026::v2"u8.ToArray();
    private const int Pbkdf2Iterations = 600_000;
    private const byte ModeDpapiOnly = 0x01;
    private const byte ModeDpapiWithPassphrase = 0x02;

    public string VaultDirectory { get; }
    public string VaultFilePath { get; }

    public MultiModelVault(string? customDirectory = null)
    {
        VaultDirectory = string.IsNullOrWhiteSpace(customDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AegisComputerUse", "Vault")
            : Path.GetFullPath(customDirectory);
        VaultFilePath = Path.Combine(VaultDirectory, "models_v2.vault");
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

    public void SaveEntries(IReadOnlyList<ConfiguredModelEntry> entries, ReadOnlySpan<char> masterPassphrase = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        EnsureRestrictedDirectory();

        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte mode = masterPassphrase.IsEmpty ? ModeDpapiOnly : ModeDpapiWithPassphrase;
        byte[] aesKey = DeriveInnerKey(masterPassphrase, salt, mode);

        using var ms = new MemoryStream(1024);
        byte[]? dpapiProtected = null;
        byte[]? innerEnvelope = null;

        try
        {
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(entries.Count);
                foreach (var entry in entries)
                {
                    bw.Write((byte)entry.Provider);
                    WriteUtf8String(bw, entry.EntryId);
                    WriteUtf8String(bw, entry.ModelId);
                    WriteUtf8String(bw, entry.DisplayName);
                    WriteUtf8String(bw, entry.ThinkingLevel);

                    _ = entry.Secret.UseSecret(chars =>
                    {
                        byte[] keyBuf = new byte[Encoding.UTF8.GetMaxByteCount(chars.Length)];
                        try
                        {
                            int len = Encoding.UTF8.GetBytes(chars, keyBuf);
                            bw.Write(len);
                            bw.Write(keyBuf, 0, len);
                            return true;
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(keyBuf);
                        }
                    });
                }
                bw.Flush();
            }

            ReadOnlySpan<byte> plainPayload = ms.GetBuffer().AsSpan(0, (int)ms.Length);
            byte[] cipher = new byte[plainPayload.Length];
            byte[] tag = new byte[16];
            try
            {
                using var aes = new AesGcm(aesKey, 16);
                aes.Encrypt(nonce, plainPayload, cipher, tag, MagicHeader);

                innerEnvelope = new byte[8 + 1 + 32 + 12 + 16 + cipher.Length];
                Buffer.BlockCopy(MagicHeader, 0, innerEnvelope, 0, 8);
                innerEnvelope[8] = mode;
                Buffer.BlockCopy(salt, 0, innerEnvelope, 9, 32);
                Buffer.BlockCopy(nonce, 0, innerEnvelope, 41, 12);
                Buffer.BlockCopy(tag, 0, innerEnvelope, 53, 16);
                Buffer.BlockCopy(cipher, 0, innerEnvelope, 69, cipher.Length);

                dpapiProtected = ProtectedData.Protect(innerEnvelope, AppEntropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(cipher);
                CryptographicOperations.ZeroMemory(tag);
            }

            string tempFile = Path.Combine(VaultDirectory, $".tmp_{Guid.NewGuid():N}.vault");
            try
            {
                using (var fs = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(dpapiProtected);
                    fs.Flush(true);
                }

                ApplyStrictFileAcl(tempFile);
                File.Move(tempFile, VaultFilePath, overwrite: true);
                ApplyStrictFileAcl(VaultFilePath);
            }
            finally
            {
                if (File.Exists(tempFile)) SecureDeleteFile(tempFile);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
            CryptographicOperations.ZeroMemory(ms.GetBuffer());
            if (innerEnvelope is not null) CryptographicOperations.ZeroMemory(innerEnvelope);
            if (dpapiProtected is not null) CryptographicOperations.ZeroMemory(dpapiProtected);
        }
    }

    public List<ConfiguredModelEntry> LoadEntries(ReadOnlySpan<char> masterPassphrase = default)
    {
        if (!VaultExists)
            return [];

        byte[] outer = UnprotectOuterEnvelope();
        try
        {
            ValidateHeader(outer);
            byte mode = outer[8];
            if (mode == ModeDpapiWithPassphrase && masterPassphrase.IsEmpty)
                throw new CryptographicException("Master passphrase is required to unlock the saved models vault.");

            ReadOnlySpan<byte> salt = outer.AsSpan(9, 32);
            ReadOnlySpan<byte> nonce = outer.AsSpan(41, 12);
            ReadOnlySpan<byte> tag = outer.AsSpan(53, 16);
            ReadOnlySpan<byte> cipher = outer.AsSpan(69);

            byte[] aesKey = DeriveInnerKey(masterPassphrase, salt, mode);
            byte[] plainBytes = new byte[cipher.Length];
            var loaded = new List<ConfiguredModelEntry>();

            try
            {
                using var aes = new AesGcm(aesKey, 16);
                aes.Decrypt(nonce, cipher, tag, plainBytes, MagicHeader);

                using var ms = new MemoryStream(plainBytes, writable: false);
                using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);

                int count = br.ReadInt32();
                if (count < 0 || count > 256)
                    throw new CryptographicException("Invalid model entry count in vault.");

                for (int i = 0; i < count; i++)
                {
                    var provider = (AiProviderKind)br.ReadByte();
                    if (!Enum.IsDefined(provider))
                        throw new CryptographicException("Invalid AI provider identifier in vault.");

                    string entryId = ReadUtf8String(br, 128);
                    string modelId = GeminiApiClient.ValidateModelId(ReadUtf8String(br, 128));
                    string displayName = ReadUtf8String(br, 128);
                    string thinking = ReadUtf8String(br, 32);

                    int secretByteLen = br.ReadInt32();
                    if (secretByteLen <= 0 || secretByteLen > 16_384)
                        throw new CryptographicException("Invalid secret length in vault.");

                    byte[] secretBytes = br.ReadBytes(secretByteLen);
                    if (secretBytes.Length != secretByteLen)
                        throw new CryptographicException("Truncated secret record in vault.");

                    char[] secretChars = new char[Encoding.UTF8.GetMaxCharCount(secretByteLen)];
                    try
                    {
                        int charLen = Encoding.UTF8.GetChars(secretBytes, secretChars);
                        var sealedSecret = SealedSecret.Create(secretChars.AsSpan(0, charLen));
                        loaded.Add(new ConfiguredModelEntry(entryId, provider, modelId, displayName, thinking, true, sealedSecret));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(secretBytes);
                        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(secretChars.AsSpan()));
                    }
                }

                return loaded;
            }
            catch
            {
                foreach (var e in loaded) e.Dispose();
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(aesKey);
                CryptographicOperations.ZeroMemory(plainBytes);
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

    private static void WriteUtf8String(BinaryWriter bw, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    private static string ReadUtf8String(BinaryReader br, int maxBytes)
    {
        int len = br.ReadInt32();
        if (len < 0 || len > maxBytes)
            throw new CryptographicException("Corrupted string field in vault.");
        byte[] bytes = br.ReadBytes(len);
        if (bytes.Length != len)
            throw new CryptographicException("Truncated string field in vault.");
        return Encoding.UTF8.GetString(bytes);
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
        if (envelope.Length < 73 || !envelope.AsSpan(0, 8).SequenceEqual(MagicHeader))
            throw new CryptographicException("Multi-model vault envelope failed integrity verification.");

        byte mode = envelope[8];
        if (mode != ModeDpapiOnly && mode != ModeDpapiWithPassphrase)
            throw new CryptographicException("Unsupported multi-model vault encryption mode.");
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
            if (len > 0 && len <= 2 * 1024 * 1024)
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
        catch { }
        File.Delete(path);
    }
}
