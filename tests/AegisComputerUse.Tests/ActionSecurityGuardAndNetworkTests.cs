using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AegisComputerUse.Core.Gemini;
using AegisComputerUse.Core.Native;
using AegisComputerUse.Core.Security;

namespace AegisComputerUse.Tests;

public sealed class ActionSecurityGuardAndNetworkTests
{
    [Fact]
    public void ActionSecurityGuard_BlocksCriticalHotkeysAndOutOfBoundsCoords()
    {
        var guard = new ActionSecurityGuard();

        using var delDoc = JsonDocument.Parse("""{"keys":"Ctrl+Alt+Delete"}""");
        var resDel = guard.EvaluateAction("key_combination", delDoc.RootElement, 1, 1920, 1080);
        Assert.Equal(ActionVerdict.Block, resDel.Verdict);

        using var lockDoc = JsonDocument.Parse("""{"keys":"Win+L"}""");
        var resLock = guard.EvaluateAction("key_combination", lockDoc.RootElement, 1, 1920, 1080);
        Assert.Equal(ActionVerdict.Block, resLock.Verdict);

        using var badCoordDoc = JsonDocument.Parse("""{"x":1500,"y":-10}""");
        var resCoord = guard.EvaluateAction("click_at", badCoordDoc.RootElement, 1, 1920, 1080);
        Assert.Equal(ActionVerdict.Block, resCoord.Verdict);

        using var unknownDoc = JsonDocument.Parse("""{}""");
        var resUnknown = guard.EvaluateAction("exec_shell_cmd", unknownDoc.RootElement, 1, 1920, 1080);
        Assert.Equal(ActionVerdict.Block, resUnknown.Verdict);
    }

    [Fact]
    public void ActionSecurityGuard_RequiresConfirmationOnPromptInjectionCommandsAndSafetyDecisions()
    {
        var guard = new ActionSecurityGuard();

        using var winR = JsonDocument.Parse("""{"keys":"Win+R"}""");
        Assert.Equal(ActionVerdict.RequireConfirmation, guard.EvaluateAction("key_combination", winR.RootElement, 1, 1920, 1080).Verdict);

        using var psType = JsonDocument.Parse("""{"x":300,"y":300,"text":"powershell -enc SQBFAFgA"}""");
        Assert.Equal(ActionVerdict.RequireConfirmation, guard.EvaluateAction("type_text_at", psType.RootElement, 1, 1920, 1080).Verdict);

        using var safetyDec = JsonDocument.Parse("""{"x":400,"y":400,"safety_decision":{"decision":"require_confirmation","explanation":"Purchasing item"}}""");
        Assert.Equal(ActionVerdict.RequireConfirmation, guard.EvaluateAction("click_at", safetyDec.RootElement, 1, 1920, 1080).Verdict);
    }

    [Fact]
    public void ActionSecurityGuard_EnforcesTargetWindowBoundsAndSensitiveWindowPause()
    {
        var policy = new ActionSecurityPolicyOptions
        {
            EnforceTargetWindowBounds = true,
            TargetWindowRect = new Win32Interop.RECT { Left = 100, Top = 100, Right = 500, Bottom = 500 },
            BlockSensitiveForegroundWindows = true
        };
        var guard = new ActionSecurityGuard(policy);

        // (900, 900) on 1920x1080 maps outside [100..500, 100..500]
        using var outsideDoc = JsonDocument.Parse("""{"x":900,"y":900}""");
        Assert.Equal(ActionVerdict.Block, guard.EvaluateAction("click_at", outsideDoc.RootElement, 1, 1920, 1080).Verdict);

        // Sensitive foreground window triggers pause/confirmation
        var captureDecision = guard.EvaluateScreenCapture("KeePassXC - Passwords.kdbx");
        Assert.Equal(ActionVerdict.RequireConfirmation, captureDecision.Verdict);

        using var insideDoc = JsonDocument.Parse("""{"x":150,"y":250}""");
        Assert.Equal(ActionVerdict.RequireConfirmation, guard.EvaluateAction("click_at", insideDoc.RootElement, 1, 1920, 1080, 0, 0, "1Password Vault").Verdict);
    }

    [Fact]
    public void ActionSecurityGuard_BlocksUnsafeNavigationSchemes()
    {
        var guard = new ActionSecurityGuard();

        using var fileUrl = JsonDocument.Parse("""{"url":"file:///C:/Windows/System32/drivers/etc/hosts"}""");
        Assert.Equal(ActionVerdict.Block, guard.EvaluateAction("navigate", fileUrl.RootElement, 1, 1920, 1080).Verdict);

        using var jsUrl = JsonDocument.Parse("""{"url":"javascript:alert(1)"}""");
        Assert.Equal(ActionVerdict.Block, guard.EvaluateAction("navigate", jsUrl.RootElement, 1, 1920, 1080).Verdict);

        using var httpsUrl = JsonDocument.Parse("""{"url":"https://example.com"}""");
        Assert.Equal(ActionVerdict.Allow, guard.EvaluateAction("navigate", httpsUrl.RootElement, 1, 1920, 1080).Verdict);
    }

    [Theory]
    [InlineData("http://generativelanguage.googleapis.com/v1beta/models/gemini-4-argon:generateContent")]
    [InlineData("https://evil.com/v1beta/models/gemini-4-argon:generateContent")]
    [InlineData("https://generativelanguage.googleapis.com.evil.com/v1beta/models/gemini-4-argon:generateContent")]
    [InlineData("https://generativelanguage.googleapis.com:8443/v1beta/models/gemini-4-argon:generateContent")]
    [InlineData("https://user@generativelanguage.googleapis.com/v1beta/models/gemini-4-argon:generateContent")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/models/gemini-4-argon:generateContent?key=AIza123")]
    public void GeminiApiClient_RejectsUntrustedOrLeakyEndpoints(string badUrl)
    {
        Assert.Throws<InvalidOperationException>(() => GeminiApiClient.ValidateEndpointUri(new Uri(badUrl)));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("gemini-4-argon?key=123")]
    [InlineData("model/with/slash")]
    [InlineData("")]
    public void GeminiApiClient_RejectsInvalidModelIdentifiers(string badModel)
    {
        Assert.Throws<ArgumentException>(() => GeminiApiClient.ValidateModelId(badModel));
    }

    [Fact]
    public void GeminiApiClient_RejectsRogueLookalikeOrUnverifiedCertificates()
    {
        using var rsa = RSA.Create(2048);
        var reqRogue = new CertificateRequest("CN=generativelanguage.googleapis.com.attacker.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanRogue = new SubjectAlternativeNameBuilder();
        sanRogue.AddDnsName("generativelanguage.googleapis.com.attacker.com");
        reqRogue.CertificateExtensions.Add(sanRogue.Build());
        using var rogueCert = reqRogue.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // Even if SslPolicyErrors were None, hostname check rejects lookalike SAN
        Assert.False(GeminiApiClient.ValidateGoogleCertificate(null!, rogueCert, null, SslPolicyErrors.None));

        // Real SAN must still be rejected if chain has SslPolicyErrors
        var reqValidSan = new CertificateRequest("CN=generativelanguage.googleapis.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanValid = new SubjectAlternativeNameBuilder();
        sanValid.AddDnsName("generativelanguage.googleapis.com");
        reqValidSan.CertificateExtensions.Add(sanValid.Build());
        using var validSanCert = reqValidSan.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        Assert.False(GeminiApiClient.ValidateGoogleCertificate(null!, validSanCert, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(GeminiApiClient.ValidateGoogleCertificate(null!, validSanCert, null, SslPolicyErrors.None));
    }
}
