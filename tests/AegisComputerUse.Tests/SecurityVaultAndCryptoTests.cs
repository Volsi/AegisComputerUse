using System.IO;
using System.Net.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using AegisComputerUse.Core.Gemini;
using AegisComputerUse.Core.Security;

namespace AegisComputerUse.Tests;

public sealed class SecurityVaultAndCryptoTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "AegisVaultTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void SealedSecret_ProtectsInRam_AndWipesOnDispose()
    {
        using var secret = SealedSecret.Create("AIzaSyTestSecretKey1234567890ABCDEF".AsSpan());
        Assert.False(secret.IsDisposed);
        Assert.Equal(35, secret.Length);
        Assert.DoesNotContain("AIza", secret.GetSafeStatusLabel(), StringComparison.Ordinal);

        string read = secret.UseSecret(chars => new string(chars));
        Assert.Equal("AIzaSyTestSecretKey1234567890ABCDEF", read);

        secret.Dispose();
        Assert.True(secret.IsDisposed);
        Assert.Equal(0, secret.Length);
        Assert.Throws<ObjectDisposedException>(() => secret.UseSecret(chars => chars.Length));
    }

    [Theory]
    [InlineData("AIzaTest\r\nX-Injected: evil")]
    [InlineData("AIzaTest\nBad")]
    [InlineData("AIzaTest\0Null")]
    [InlineData("   ")]
    public void SealedSecret_RejectsControlCharsAndWhitespaceKeys(string invalidSecret)
    {
        Assert.Throws<ArgumentException>(() => SealedSecret.Create(invalidSecret.AsSpan()));
    }

    [Fact]
    public void SecureKeyVault_DpapiOnly_RoundTripAndStrictAcl()
    {
        var vault = new SecureKeyVault(_tempDir);
        using var secret = SealedSecret.Create("AIzaSyDpapiOnlyTestKey999999999999".AsSpan());
        vault.SaveKey(secret);

        Assert.True(vault.VaultExists);
        Assert.False(vault.RequiresPassphrase());

        // Verify strict NTFS ACL: current user only, inheritance disabled
        var currentSid = WindowsIdentity.GetCurrent().User!;
        var fileSecurity = new FileInfo(vault.VaultFilePath).GetAccessControl();
        var rules = fileSecurity.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        Assert.Single(rules);
        Assert.Equal(currentSid, rules[0].IdentityReference);
        Assert.Equal(AccessControlType.Allow, rules[0].AccessControlType);
        Assert.True(fileSecurity.AreAccessRulesProtected);

        using var loaded = vault.LoadKey();
        string actual = loaded.UseSecret(c => new string(c));
        Assert.Equal("AIzaSyDpapiOnlyTestKey999999999999", actual);
    }

    [Fact]
    public void SecureKeyVault_WithMasterPassphrase_BlocksWrongPassphraseAndTampering()
    {
        var vault = new SecureKeyVault(_tempDir);
        using var secret = SealedSecret.Create("AIzaSySuperHardenedVaultSecretKey0123".AsSpan());
        vault.SaveKey(secret, "Correct-Horse-Battery-Staple!".AsSpan());

        Assert.True(vault.RequiresPassphrase());

        Assert.ThrowsAny<CryptographicException>(() => vault.LoadKey());
        Assert.ThrowsAny<CryptographicException>(() => vault.LoadKey("WrongPassphrase".AsSpan()));

        using (var unlocked = vault.LoadKey("Correct-Horse-Battery-Staple!".AsSpan()))
        {
            Assert.Equal("AIzaSySuperHardenedVaultSecretKey0123", unlocked.UseSecret(c => new string(c)));
        }

        // Tamper with ciphertext byte
        byte[] raw = File.ReadAllBytes(vault.VaultFilePath);
        raw[^1] ^= 0xFF;
        File.WriteAllBytes(vault.VaultFilePath, raw);

        Assert.ThrowsAny<CryptographicException>(() => vault.LoadKey("Correct-Horse-Battery-Staple!".AsSpan()));

        vault.DeleteVault();
        Assert.False(vault.VaultExists);
    }

    [Fact]
    public void LogRedactor_ScrubsKeysHeadersTokensAndQuerySecrets()
    {
        const string secret = "AIzaSyFakeKeyValue9876543210ABCDE";
        string noisy = $"GET https://generativelanguage.googleapis.com/v1beta/models?key={secret} Header x-goog-api-key: {secret} Authorization: Bearer my.jwt.token";
        string redacted = LogRedactor.Redact(noisy, secret);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("my.jwt.token", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void LogRedactor_ScrubsOpenAiAndAnthropicKeysAndXApiKeyHeader()
    {
        const string openAiKey = "sk-proj-TestOpenAiSecretKey1234567890ABCD";
        const string anthropicKey = "sk-ant-api03-FakeAnthropicSecretKey1234567890";
        string msg = $"OpenAI Bearer {openAiKey} Anthropic x-api-key: {anthropicKey} raw={anthropicKey}";
        string redacted = LogRedactor.Redact(msg);

        Assert.DoesNotContain(openAiKey, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(anthropicKey, redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiModelVault_DpapiOnly_RoundTripsMultipleProvidersAndEnforcesStrictAcl()
    {
        var vault = new MultiModelVault(_tempDir);
        var entries = new List<ConfiguredModelEntry>
        {
            new("id-gem", AiProviderKind.GoogleGemini, "gemini-4-argon", "Gemini 4 Argon", "HIGH", true,
                SealedSecret.Create("AIzaSyMultiVaultGeminiKey1234567890".AsSpan())),
            new("id-oai", AiProviderKind.OpenAI, "gpt-5.4", "GPT-5.4", "MEDIUM", true,
                SealedSecret.Create("sk-proj-MultiVaultOpenAiKey1234567890".AsSpan())),
            new("id-ant", AiProviderKind.Anthropic, "claude-opus-4-6", "Claude Opus 4.6", "LOW", true,
                SealedSecret.Create("sk-ant-MultiVaultAnthropicKey1234567890".AsSpan()))
        };

        try
        {
            vault.SaveEntries(entries);
            Assert.True(vault.VaultExists);
            Assert.False(vault.RequiresPassphrase());

            // Strict current-user-only NTFS ACL, inheritance disabled
            var currentSid = WindowsIdentity.GetCurrent().User!;
            var fileSecurity = new FileInfo(vault.VaultFilePath).GetAccessControl();
            var rules = fileSecurity.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();
            Assert.Single(rules);
            Assert.Equal(currentSid, rules[0].IdentityReference);
            Assert.True(fileSecurity.AreAccessRulesProtected);

            var loaded = vault.LoadEntries();
            try
            {
                Assert.Equal(3, loaded.Count);
                Assert.Equal(AiProviderKind.GoogleGemini, loaded[0].Provider);
                Assert.Equal("gemini-4-argon", loaded[0].ModelId);
                Assert.Equal("HIGH", loaded[0].ThinkingLevel);
                Assert.Equal("AIzaSyMultiVaultGeminiKey1234567890", loaded[0].Secret.UseSecret(c => new string(c)));

                Assert.Equal(AiProviderKind.OpenAI, loaded[1].Provider);
                Assert.Equal("gpt-5.4", loaded[1].ModelId);
                Assert.Equal("sk-proj-MultiVaultOpenAiKey1234567890", loaded[1].Secret.UseSecret(c => new string(c)));

                Assert.Equal(AiProviderKind.Anthropic, loaded[2].Provider);
                Assert.Equal("claude-opus-4-6", loaded[2].ModelId);
                Assert.Equal("LOW", loaded[2].ThinkingLevel);
                Assert.Equal("sk-ant-MultiVaultAnthropicKey1234567890", loaded[2].Secret.UseSecret(c => new string(c)));
            }
            finally
            {
                foreach (var e in loaded) e.Dispose();
            }
        }
        finally
        {
            foreach (var e in entries) e.Dispose();
        }
    }

    [Fact]
    public void MultiModelVault_WithPassphrase_BlocksWrongPassphraseAndTampering()
    {
        var vault = new MultiModelVault(_tempDir);
        using var entry = new ConfiguredModelEntry(
            "id-sec", AiProviderKind.OpenAI, "o3", "OpenAI o3", "HIGH", true,
            SealedSecret.Create("sk-proj-PassphraseProtectedKey000111".AsSpan()));

        vault.SaveEntries([entry], "Master-Vault-Pass!2026".AsSpan());
        Assert.True(vault.RequiresPassphrase());

        Assert.ThrowsAny<CryptographicException>(() => vault.LoadEntries());
        Assert.ThrowsAny<CryptographicException>(() => vault.LoadEntries("WrongPass!".AsSpan()));

        var unlocked = vault.LoadEntries("Master-Vault-Pass!2026".AsSpan());
        try
        {
            Assert.Single(unlocked);
            Assert.Equal("sk-proj-PassphraseProtectedKey000111", unlocked[0].Secret.UseSecret(c => new string(c)));
        }
        finally
        {
            foreach (var e in unlocked) e.Dispose();
        }

        // Tamper ciphertext byte
        byte[] raw = File.ReadAllBytes(vault.VaultFilePath);
        raw[^2] ^= 0x5A;
        File.WriteAllBytes(vault.VaultFilePath, raw);
        Assert.ThrowsAny<CryptographicException>(() => vault.LoadEntries("Master-Vault-Pass!2026".AsSpan()));

        vault.DeleteVault();
        Assert.False(vault.VaultExists);
    }

    [Theory]
    [InlineData("http://api.openai.com/v1/responses", AiProviderKind.OpenAI)]
    [InlineData("https://api.openai.com:8443/v1/responses", AiProviderKind.OpenAI)]
    [InlineData("https://evil.openai.com/v1/responses", AiProviderKind.OpenAI)]
    [InlineData("https://api.anthropic.com/v1/messages", AiProviderKind.OpenAI)]
    [InlineData("https://api.openai.com/v1/responses?key=secret", AiProviderKind.OpenAI)]
    [InlineData("https://user:pass@api.anthropic.com/v1/messages", AiProviderKind.Anthropic)]
    public void MultiProviderTransport_RejectsUntrustedHostsPortsSchemesAndQueries(string badUrl, AiProviderKind provider)
    {
        var uri = new Uri(badUrl, UriKind.Absolute);
        Assert.Throws<InvalidOperationException>(() => MultiProviderTransport.ValidateProviderEndpoint(uri, provider));
    }

    [Fact]
    public void MultiProviderTransport_ValidateProviderCertificate_RejectsErrorsAndNull()
    {
        Assert.False(MultiProviderTransport.ValidateProviderCertificate(new object(), null, null, SslPolicyErrors.None));
        Assert.False(MultiProviderTransport.ValidateProviderCertificate(new object(), null, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../models/evil")]
    [InlineData("gemini..argon")]
    [InlineData("gpt-5?key=123")]
    [InlineData("claude/sonnet")]
    public void GeminiApiClient_ValidateModelId_RejectsTraversalAndInjection(string badModelId)
    {
        Assert.Throws<ArgumentException>(() => GeminiApiClient.ValidateModelId(badModelId));
    }
}
