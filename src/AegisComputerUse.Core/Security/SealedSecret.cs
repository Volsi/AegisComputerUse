using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AegisComputerUse.Core.Native;

namespace AegisComputerUse.Core.Security;

[SupportedOSPlatform("windows")]
public sealed class SealedSecret : IDisposable
{
    private readonly Lock _gate = new();
    private readonly byte[] _ephemeralKey;
    private GCHandle _keyHandle;
    private readonly byte[] _nonce;
    private readonly byte[] _tag;
    private readonly byte[] _ciphertext;
    private readonly int _charLength;
    private int _disposed;

    private SealedSecret(ReadOnlySpan<char> secret)
    {
        secret = secret.Trim();
        if (secret.IsEmpty)
            throw new ArgumentException("Secret cannot be empty.", nameof(secret));
        if (secret.IndexOfAny('\r', '\n', '\0') >= 0)
            throw new ArgumentException("Secret contains forbidden control characters.", nameof(secret));

        _charLength = secret.Length;
        int maxBytes = Encoding.UTF8.GetMaxByteCount(secret.Length);
        byte[] plainBytes = new byte[maxBytes];
        _ephemeralKey = new byte[32]; // Multiple of CRYPTPROTECTMEMORY_BLOCK_SIZE (16)
        _nonce = new byte[12];
        _tag = new byte[16];

        try
        {
            int actualBytes = Encoding.UTF8.GetBytes(secret, plainBytes);
            _ciphertext = new byte[actualBytes];

            RandomNumberGenerator.Fill(_ephemeralKey);
            RandomNumberGenerator.Fill(_nonce);

            using var aes = new AesGcm(_ephemeralKey, 16);
            aes.Encrypt(_nonce, plainBytes.AsSpan(0, actualBytes), _ciphertext, _tag);

            _keyHandle = GCHandle.Alloc(_ephemeralKey, GCHandleType.Pinned);
            if (!Win32Interop.CryptProtectMemory(_keyHandle.AddrOfPinnedObject(), (uint)_ephemeralKey.Length, Win32Interop.CRYPTPROTECTMEMORY_SAME_PROCESS))
                throw new CryptographicException("CryptProtectMemory failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public int Length => Volatile.Read(ref _disposed) == 0 ? _charLength : 0;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public static SealedSecret Create(ReadOnlySpan<char> secret) => new(secret);

    public T UseSecret<T>(Func<ReadOnlySpan<char>, T> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            byte[] plainBytes = new byte[_ciphertext.Length];
            char[] plainChars = new char[Encoding.UTF8.GetMaxCharCount(_ciphertext.Length)];
            bool keyUnprotected = false;

            try
            {
                if (!Win32Interop.CryptUnprotectMemory(_keyHandle.AddrOfPinnedObject(), (uint)_ephemeralKey.Length, Win32Interop.CRYPTPROTECTMEMORY_SAME_PROCESS))
                    throw new CryptographicException("CryptUnprotectMemory failed.");
                keyUnprotected = true;

                using var aes = new AesGcm(_ephemeralKey, 16);
                aes.Decrypt(_nonce, _ciphertext, _tag, plainBytes);

                int charCount = Encoding.UTF8.GetChars(plainBytes, plainChars);
                return consumer(plainChars.AsSpan(0, charCount));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(plainChars.AsSpan()));
                if (keyUnprotected && _disposed == 0)
                {
                    Win32Interop.CryptProtectMemory(_keyHandle.AddrOfPinnedObject(), (uint)_ephemeralKey.Length, Win32Interop.CRYPTPROTECTMEMORY_SAME_PROCESS);
                }
            }
        }
    }

    public async Task<T> UseSecretAsync<T>(Func<ReadOnlyMemory<byte>, Task<T>> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        byte[] plainBytes;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            plainBytes = new byte[_ciphertext.Length];
            bool keyUnprotected = false;
            try
            {
                if (!Win32Interop.CryptUnprotectMemory(_keyHandle.AddrOfPinnedObject(), (uint)_ephemeralKey.Length, Win32Interop.CRYPTPROTECTMEMORY_SAME_PROCESS))
                    throw new CryptographicException("CryptUnprotectMemory failed.");
                keyUnprotected = true;

                using var aes = new AesGcm(_ephemeralKey, 16);
                aes.Decrypt(_nonce, _ciphertext, _tag, plainBytes);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plainBytes);
                throw;
            }
            finally
            {
                if (keyUnprotected && _disposed == 0)
                {
                    Win32Interop.CryptProtectMemory(_keyHandle.AddrOfPinnedObject(), (uint)_ephemeralKey.Length, Win32Interop.CRYPTPROTECTMEMORY_SAME_PROCESS);
                }
            }
        }

        try
        {
            return await consumer(plainBytes).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public string GetSafeStatusLabel() =>
        IsDisposed
            ? "Key wiped from RAM"
            : $"Protected in RAM (AES-GCM + CryptProtectMemory, {_charLength} chars)";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            _disposed = 1;

            CryptographicOperations.ZeroMemory(_ephemeralKey);
            CryptographicOperations.ZeroMemory(_ciphertext);
            CryptographicOperations.ZeroMemory(_nonce);
            CryptographicOperations.ZeroMemory(_tag);

            if (_keyHandle.IsAllocated)
            {
                _keyHandle.Free();
            }
        }
    }
}
