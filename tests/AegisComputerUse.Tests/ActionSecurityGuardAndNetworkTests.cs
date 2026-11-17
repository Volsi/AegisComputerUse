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

    [Fact]
    public void ActionSecurityGuard_AcceptsGeminiComputerUseNativeActionsAndPointFormats()
    {
        var guard = new ActionSecurityGuard();

        // 1. Native "click" with "point": [500, 500]
        using var clickPointDoc = JsonDocument.Parse("""{"point":[500, 500]}""");
        var res1 = guard.EvaluateAction("click", clickPointDoc.RootElement, 1, 1920, 1080);
        Assert.Equal(ActionVerdict.Allow, res1.Verdict);

        // 2. Native "click" with "coordinate": [300, 400] and "button": "left"
        using var clickCoordDoc = JsonDocument.Parse("""{"coordinate":[300, 400], "button": "left"}""");
        var res2 = guard.EvaluateAction("click", clickCoordDoc.RootElement, 2, 1920, 1080);
        Assert.Equal(ActionVerdict.Allow, res2.Verdict);

        // 3. "move" with "point": [100, 200]
        using var moveDoc = JsonDocument.Parse("""{"point":[100, 200]}""");
        var res3 = guard.EvaluateAction("move", moveDoc.RootElement, 3, 1920, 1080);
        Assert.Equal(ActionVerdict.Allow, res3.Verdict);

        // 4. "type" with text
        using var typeDoc = JsonDocument.Parse("""{"text":"hello world"}""");
        var res4 = guard.EvaluateAction("type", typeDoc.RootElement, 4, 1920, 1080);
        Assert.Equal(ActionVerdict.Allow, res4.Verdict);

        // 5. "press_key" with key
        using var pressKeyDoc = JsonDocument.Parse("""{"key":"Enter"}""");
        var res5 = guard.EvaluateAction("press_key", pressKeyDoc.RootElement, 5, 1920, 1080);
        Assert.Equal(ActionVerdict.Allow, res5.Verdict);

        // 6. "click" with out of bounds coordinates [1200, 500]
        using var badDoc = JsonDocument.Parse("""{"point":[1200, 500]}""");
        var res6 = guard.EvaluateAction("click", badDoc.RootElement, 6, 1920, 1080);
        Assert.Equal(ActionVerdict.Block, res6.Verdict);
    }

    [Fact]
    public void ActionSecurityGuard_TryExtractPoint_SupportsVariousFormats()
    {
        // 1. Direct x, y
        using var d1 = JsonDocument.Parse("""{"x": 120, "y": 340}""");
        Assert.True(ActionSecurityGuard.TryExtractPoint(d1.RootElement, "x", "y", out int x1, out int y1));
        Assert.Equal(120, x1);
        Assert.Equal(340, y1);

        // 2. Point array
        using var d2 = JsonDocument.Parse("""{"point": [550, 750]}""");
        Assert.True(ActionSecurityGuard.TryExtractPoint(d2.RootElement, "x", "y", out int x2, out int y2));
        Assert.Equal(550, x2);
        Assert.Equal(750, y2);

        // 3. Coordinate array
        using var d3 = JsonDocument.Parse("""{"coordinate": [11, 22]}""");
        Assert.True(ActionSecurityGuard.TryExtractPoint(d3.RootElement, "x", "y", out int x3, out int y3));
        Assert.Equal(11, x3);
        Assert.Equal(22, y3);

        // 4. Nested object in point
        using var d4 = JsonDocument.Parse("""{"point": {"x": 88, "y": 99}}""");
        Assert.True(ActionSecurityGuard.TryExtractPoint(d4.RootElement, "x", "y", out int x4, out int y4));
        Assert.Equal(88, x4);
        Assert.Equal(99, y4);

        // 5. Destination point for drag
        using var d5 = JsonDocument.Parse("""{"destination_point": [400, 600]}""");
        Assert.True(ActionSecurityGuard.TryExtractPoint(d5.RootElement, "destination_x", "destination_y", out int x5, out int y5));
        Assert.Equal(400, x5);
        Assert.Equal(600, y5);
    }
}
