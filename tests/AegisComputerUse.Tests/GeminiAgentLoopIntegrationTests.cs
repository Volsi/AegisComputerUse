using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Threading;
using AegisComputerUse.App;
using AegisComputerUse.App.Controls;
using AegisComputerUse.Core.Files;
using AegisComputerUse.Core.Gemini;
using AegisComputerUse.Core.Native;
using AegisComputerUse.Core.Security;

namespace AegisComputerUse.Tests;

public sealed class GeminiAgentLoopIntegrationTests : IDisposable
{
    private readonly string _sandboxDir = Path.Combine(Path.GetTempPath(), "AegisAgentTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_sandboxDir))
            Directory.Delete(_sandboxDir, recursive: true);
    }

    [Fact]
    public async Task ComputerUseAgent_FullLoop_PreservesThoughtSignature_SendsHeaderKey_CreatesFile()
    {
        var handler = new RecordingHandler(
        [
            // Turn 1: model requests click_at with thoughtSignature
            (HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": {
                  "role": "model",
                  "parts": [{
                    "thoughtSignature": "SIG_TURN_1_OPAQUE",
                    "functionCall": { "name": "click_at", "args": { "x": 500, "y": 500 } }
                  }]
                }
              }]
            }
            """),
            // Turn 2: model creates a docx file safely inside sandbox
            (HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": {
                  "role": "model",
                  "parts": [{
                    "thoughtSignature": "SIG_TURN_2_OPAQUE",
                    "functionCall": {
                      "name": "create_file",
                      "args": { "relative_path": "result.docx", "content": "# Done\nClick succeeded." }
                    }
                  }]
                }
              }]
            }
            """),
            // Turn 3: final answer
            (HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": {
                  "role": "model",
                  "parts": [{ "text": "Задача выполнена и файл result.docx создан." }]
                }
              }]
            }
            """)
        ]);

        using var apiClient = new GeminiApiClient(handler);
        using var secret = SealedSecret.Create("AIzaSyIntegrationTestKey000111222333".AsSpan());
        var fakeCapture = new FakeScreenCaptureService();
        var fakeInput = new FakeInputAutomationService();
        var policy = new ActionSecurityPolicyOptions { MinActionIntervalMs = 0, BlockSensitiveForegroundWindows = false };
        var guard = new ActionSecurityGuard(policy);
        var creator = new UniversalFileCreator(new SafeFileSandbox(_sandboxDir));
        var agent = new ComputerUseAgent(apiClient, fakeCapture, fakeInput, guard, creator, policy);

        var history = new List<JsonNode>();
        var events = new List<AgentEvent>();
        string answer = await agent.RunTurnAsync(
            secret,
            "Click center and save result.docx",
            [new AttachmentInput("sample.png", "image/png", [0x89, 0x50, 0x4E, 0x47])],
            history,
            new GeminiRequestOptions(Model: "gemini-4-argon", ThinkingLevel: "MEDIUM"),
            _ => Task.FromResult(true),
            ev => events.Add(ev),
            CancellationToken.None);

        Assert.Contains("Задача выполнена", answer, StringComparison.Ordinal);
        Assert.Single(fakeInput.ExecutedCalls);
        Assert.Equal("click_at", fakeInput.ExecutedCalls[0]);
        Assert.True(File.Exists(Path.Combine(_sandboxDir, "result.docx")));

        // Verify security invariants on recorded HTTP requests
        Assert.Equal(3, handler.RecordedRequests.Count);
        foreach (var req in handler.RecordedRequests)
        {
            Assert.Equal("/v1beta/models/gemini-4-argon:generateContent", req.Uri.AbsolutePath);
            Assert.Equal(string.Empty, req.Uri.Query); // API key NEVER in URL query string
            Assert.Equal("AIzaSyIntegrationTestKey000111222333", req.ApiKeyHeader);
        }

        // Verify thoughtSignature preserved verbatim in turn 2 request body
        Assert.Contains("SIG_TURN_1_OPAQUE", handler.RecordedRequests[1].BodyJson, StringComparison.Ordinal);
        Assert.Contains("ENVIRONMENT_DESKTOP", handler.RecordedRequests[0].BodyJson, StringComparison.Ordinal);
        Assert.Contains("inlineData", handler.RecordedRequests[1].BodyJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComputerUseAgent_PausesAndAborts_WhenUserDeniesSafetyDecision()
    {
        var handler = new RecordingHandler(
        [
            (HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": {
                  "role": "model",
                  "parts": [{
                    "functionCall": {
                      "name": "click_at",
                      "args": {
                        "x": 200, "y": 200,
                        "safety_decision": { "decision": "require_confirmation", "explanation": "Confirm checkout" }
                      }
                    }
                  }]
                }
              }]
            }
            """)
        ]);

        using var apiClient = new GeminiApiClient(handler);
        using var secret = SealedSecret.Create("AIzaSyIntegrationTestKey000111222333".AsSpan());
        var fakeCapture = new FakeScreenCaptureService();
        var fakeInput = new FakeInputAutomationService();
        var policy = new ActionSecurityPolicyOptions { MinActionIntervalMs = 0, BlockSensitiveForegroundWindows = false };
        var agent = new ComputerUseAgent(apiClient, fakeCapture, fakeInput, new ActionSecurityGuard(policy),
            new UniversalFileCreator(new SafeFileSandbox(_sandboxDir)), policy);

        bool confirmationPrompted = false;
        var events = new List<AgentEvent>();
        string answer = await agent.RunTurnAsync(
            secret,
            "Buy item",
            [],
            [],
            new GeminiRequestOptions(),
            _ => { confirmationPrompted = true; return Task.FromResult(false); },
            ev => events.Add(ev),
            CancellationToken.None);

        Assert.True(confirmationPrompted);
        Assert.Empty(fakeInput.ExecutedCalls); // Must NOT execute when denied!
        Assert.Contains("aborted", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComputerUseAgent_AutoAdaptsToolSchema_OnHttp400()
    {
        var handler = new RecordingHandler(
        [
            (HttpStatusCode.BadRequest, """{"error":{"message":"combined computerUse and functionDeclarations unsupported"}}"""),
            (HttpStatusCode.BadRequest, """{"error":{"message":"computerUse not enabled for this model"}}"""),
            (HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": {
                  "role": "model",
                  "parts": [{ "text": "Adapted to functionDeclarations cleanly." }]
                }
              }]
            }
            """)
        ]);

        using var apiClient = new GeminiApiClient(handler);
        using var secret = SealedSecret.Create("AIzaSyIntegrationTestKey000111222333".AsSpan());
        var policy = new ActionSecurityPolicyOptions { MinActionIntervalMs = 0, BlockSensitiveForegroundWindows = false };
        var agent = new ComputerUseAgent(apiClient, new FakeScreenCaptureService(), new FakeInputAutomationService(),
            new ActionSecurityGuard(policy), new UniversalFileCreator(new SafeFileSandbox(_sandboxDir)), policy);

        string result = await agent.RunTurnAsync(
            secret, "Check screen", [], [], new GeminiRequestOptions(ToolMode: ComputerUseToolMode.Auto),
            _ => Task.FromResult(true), _ => { }, CancellationToken.None);

        Assert.Equal("Adapted to functionDeclarations cleanly.", result);
        Assert.Equal(3, handler.RecordedRequests.Count);
        Assert.Contains("functionDeclarations", handler.RecordedRequests[2].BodyJson, StringComparison.Ordinal);
        Assert.Contains("click_at", handler.RecordedRequests[2].BodyJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeminiApiClient_TranscribeAudioAsync_SendsWavInlineData()
    {
        var handler = new RecordingHandler(
        [
            (HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": {
                  "role": "model",
                  "parts": [{ "text": "Открой блокнот и напиши отчёт" }]
                }
              }]
            }
            """)
        ]);

        using var apiClient = new GeminiApiClient(handler);
        using var secret = SealedSecret.Create("AIzaSyVoiceTestKey1234567890ABCDEF".AsSpan());
        byte[] fakeWav = new byte[128];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(fakeWav, 0);

        string text = await apiClient.TranscribeAudioAsync(secret, "gemini-4-argon", fakeWav, CancellationToken.None);

        Assert.Equal("Открой блокнот и напиши отчёт", text);
        Assert.Single(handler.RecordedRequests);
        Assert.Contains("audio/wav", handler.RecordedRequests[0].BodyJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenCaptureService_CapturesValidPngOrReportsDesktopPolicyCleanly()
    {
        var svc = new ScreenCaptureService();
        try
        {
            var frame = svc.CaptureDesktopOrWindow(new Win32Interop.RECT { Left = 0, Top = 0, Right = 320, Bottom = 200 });
            Assert.Equal("image/png", frame.MimeType);
            Assert.True(frame.ImageBytes.Length > 100);
            Assert.Equal([0x89, 0x50, 0x4E, 0x47], frame.ImageBytes[..4]);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Windows desktop policy", StringComparison.Ordinal))
        {
            // Non-interactive/isolated CI desktop denies BitBlt with ERROR_ACCESS_DENIED (5) or ERROR_INVALID_HANDLE (6)
            Assert.Contains("Screen capture unavailable", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ComputerUseAgent_ExtractsUsageMetadataAndThoughtReasoningParts()
    {
        var handler = new RecordingHandler(
        [
            (HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": {
                  "role": "model",
                  "parts": [
                    { "thought": true, "text": "Analyzing request carefully before answering." },
                    { "text": "### Готово\nВот пример кода:\n```python\nprint('ok')\n```" }
                  ]
                }
              }],
              "usageMetadata": {
                "promptTokenCount": 80000,
                "candidatesTokenCount": 5000,
                "totalTokenCount": 87000,
                "cachedContentTokenCount": 12000,
                "thoughtsTokenCount": 2000
              }
            }
            """)
        ]);

        using var apiClient = new GeminiApiClient(handler);
        using var secret = SealedSecret.Create("AIzaSyUsageTestKey1234567890ABCDEF".AsSpan());
        var policy = new ActionSecurityPolicyOptions { MinActionIntervalMs = 0, BlockSensitiveForegroundWindows = false };
        var agent = new ComputerUseAgent(apiClient, new FakeScreenCaptureService(), new FakeInputAutomationService(),
            new ActionSecurityGuard(policy), new UniversalFileCreator(new SafeFileSandbox(_sandboxDir)), policy);

        var events = new List<AgentEvent>();
        string answer = await agent.RunTurnAsync(
            secret,
            "Show python hello",
            [],
            [],
            new GeminiRequestOptions(EnableComputerUse: false),
            _ => Task.FromResult(true),
            ev => events.Add(ev),
            CancellationToken.None);

        Assert.Contains("```python", answer, StringComparison.Ordinal);

        var usageEv = Assert.Single(events, e => e.Kind == AgentEventKind.UsageUpdated);
        Assert.NotNull(usageEv.Usage);
        Assert.Equal(80_000, usageEv.Usage.PromptTokens);
        Assert.Equal(5_000, usageEv.Usage.CandidatesTokens);
        Assert.Equal(87_000, usageEv.Usage.TotalTokens);
        Assert.Equal(12_000, usageEv.Usage.CachedTokens);
        Assert.Equal(2_000, usageEv.Usage.ThoughtsTokens);

        var reasonEv = Assert.Single(events, e => e.Kind == AgentEventKind.ModelReasoning);
        Assert.Contains("Analyzing request carefully", reasonEv.Message, StringComparison.Ordinal);

        var textEv = Assert.Single(events, e => e.Kind == AgentEventKind.ModelThoughtOrText);
        Assert.DoesNotContain("Analyzing request carefully", textEv.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UiControls_MarkdownRendererBuildsSelectableBlocks_AndContextRingFormatsCompactTokens()
    {
        Assert.Equal("0", ContextRingControl.FormatCompactTokens(0));
        Assert.Equal("850", ContextRingControl.FormatCompactTokens(850));
        Assert.Equal("87 тыс.", ContextRingControl.FormatCompactTokens(87_120));
        Assert.Equal("1.2 млн", ContextRingControl.FormatCompactTokens(1_200_000));

        Exception? staEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                string md = "# Заголовок\nТекст с **жирным** и `inline_code()`\n- пункт 1\n> цитата\n```csharp\n// comment\npublic static void Run() { }\n```";
                UIElement element = MarkdownChatRenderer.BuildRichMessage(md);
                var stack = Assert.IsType<StackPanel>(element);
                Assert.True(stack.Children.Count >= 2);

                // First child is selectable read-only RichTextBox for prose
                var proseRtb = Assert.IsType<RichTextBox>(stack.Children[0]);
                Assert.True(proseRtb.IsReadOnly);

                // Second child is Border containing header + syntax-highlighted selectable RichTextBox
                var codeBorder = Assert.IsType<Border>(stack.Children[1]);
                var codeInner = Assert.IsType<StackPanel>(codeBorder.Child);
                var codeRtb = Assert.IsType<RichTextBox>(codeInner.Children[1]);
                Assert.True(codeRtb.IsReadOnly);
                string extracted = new TextRange(codeRtb.Document.ContentStart, codeRtb.Document.ContentEnd).Text;
                Assert.Contains("public static void Run()", extracted, StringComparison.Ordinal);

                var ring = new ContextRingControl();
                ring.UpdateUsage(87_000, 1_048_576);
            }
            catch (Exception ex)
            {
                staEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(staEx);
    }



    [Fact]
    public async Task ComputerUseAgent_OpenAiResponsesLoop_SendsBearerHeader_ExecutesTool_AndParsesUsageAndReasoning()
    {
        var handler = new RecordingHandler(
        [
            // Turn 1: HTTP 400 on reasoning effort -> should retry cleanly without reasoning
            (HttpStatusCode.BadRequest, """{"error":{"message":"reasoning parameter unsupported"}}"""),
            // Retry Turn 1: function_call click_at + reasoning summary
            (HttpStatusCode.OK, """
            {
              "output": [
                { "type": "reasoning", "summary": [{ "type": "summary_text", "text": "Inspecting desktop button coordinates." }] },
                { "type": "function_call", "call_id": "call_oai_1", "name": "click_at", "arguments": "{\"x\":320,\"y\":480}" }
              ],
              "usage": {
                "input_tokens": 1200,
                "output_tokens": 150,
                "total_tokens": 1350,
                "input_tokens_details": { "cached_tokens": 300 },
                "output_tokens_details": { "reasoning_tokens": 45 }
              }
            }
            """),
            // Turn 2: final message
            (HttpStatusCode.OK, """
            {
              "output": [
                {
                  "type": "message",
                  "content": [{ "type": "output_text", "text": "OpenAI click complete." }]
                }
              ],
              "usage": {
                "input_tokens": 1800,
                "output_tokens": 90,
                "total_tokens": 1890
              }
            }
            """)
        ]);

        using var gemClient = new GeminiApiClient(handler);
        using var multi = new MultiProviderTransport(handler);
        using var secret = SealedSecret.Create("sk-proj-OpenAiTestSecret1234567890ABCD".AsSpan());
        var fakeInput = new FakeInputAutomationService();
        var policy = new ActionSecurityPolicyOptions { MinActionIntervalMs = 0, BlockSensitiveForegroundWindows = false };
        var agent = new ComputerUseAgent(gemClient, new FakeScreenCaptureService(), fakeInput,
            new ActionSecurityGuard(policy), new UniversalFileCreator(new SafeFileSandbox(_sandboxDir)), policy, multi);

        var events = new List<AgentEvent>();
        string answer = await agent.RunTurnAsync(
            secret,
            "Click button",
            [],
            [],
            new GeminiRequestOptions(Model: "gpt-5.4", ThinkingLevel: "HIGH", Provider: AiProviderKind.OpenAI),
            _ => Task.FromResult(true),
            ev => events.Add(ev),
            CancellationToken.None);

        Assert.Equal("OpenAI click complete.", answer);
        Assert.Single(fakeInput.ExecutedCalls);
        Assert.Equal("click_at", fakeInput.ExecutedCalls[0]);
        Assert.Equal(3, handler.RecordedRequests.Count);

        foreach (var req in handler.RecordedRequests)
        {
            Assert.Equal("https://api.openai.com/v1/responses", req.Uri.ToString());
            Assert.Equal("Bearer sk-proj-OpenAiTestSecret1234567890ABCD", req.AuthorizationHeader);
            Assert.Null(req.ApiKeyHeader);
        }

        Assert.Contains("\"reasoning\"", handler.RecordedRequests[0].BodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reasoning\"", handler.RecordedRequests[1].BodyJson, StringComparison.Ordinal);
        Assert.Contains("\"function_call_output\"", handler.RecordedRequests[2].BodyJson, StringComparison.Ordinal);
        Assert.Contains("call_oai_1", handler.RecordedRequests[2].BodyJson, StringComparison.Ordinal);
        Assert.Contains("input_image", handler.RecordedRequests[2].BodyJson, StringComparison.Ordinal);

        Assert.Contains(events, e => e.Kind == AgentEventKind.ModelReasoning && e.Message.Contains("Inspecting desktop", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == AgentEventKind.UsageUpdated && e.Usage is { TotalTokens: 1350, CachedTokens: 300, ThoughtsTokens: 45 });
    }

    [Fact]
    public async Task ComputerUseAgent_AnthropicMessagesLoop_SendsXApiKey_ExecutesTool_AndParsesThinkingAndUsage()
    {
        var handler = new RecordingHandler(
        [
            (HttpStatusCode.OK, """
            {
              "content": [
                { "type": "thinking", "thinking": "Evaluating sandbox file target first." },
                { "type": "tool_use", "id": "toolu_01ABC", "name": "create_file", "input": { "relative_path": "claude_note.txt", "content": "Hello from Anthropic" } }
              ],
              "usage": {
                "input_tokens": 900,
                "output_tokens": 110,
                "cache_read_input_tokens": 250
              }
            }
            """),
            (HttpStatusCode.OK, """
            {
              "content": [
                { "type": "text", "text": "Anthropic created claude_note.txt." }
              ],
              "usage": {
                "input_tokens": 1100,
                "output_tokens": 40
              }
            }
            """)
        ]);

        using var gemClient = new GeminiApiClient(handler);
        using var multi = new MultiProviderTransport(handler);
        using var secret = SealedSecret.Create("sk-ant-AnthropicTestSecret1234567890".AsSpan());
        var policy = new ActionSecurityPolicyOptions { MinActionIntervalMs = 0, BlockSensitiveForegroundWindows = false };
        var agent = new ComputerUseAgent(gemClient, new FakeScreenCaptureService(), new FakeInputAutomationService(),
            new ActionSecurityGuard(policy), new UniversalFileCreator(new SafeFileSandbox(_sandboxDir)), policy, multi);

        var events = new List<AgentEvent>();
        string answer = await agent.RunTurnAsync(
            secret,
            "Save a note",
            [],
            [],
            new GeminiRequestOptions(Model: "claude-opus-4-6", ThinkingLevel: "HIGH", Provider: AiProviderKind.Anthropic),
            _ => Task.FromResult(true),
            ev => events.Add(ev),
            CancellationToken.None);

        Assert.Equal("Anthropic created claude_note.txt.", answer);
        Assert.True(File.Exists(Path.Combine(_sandboxDir, "claude_note.txt")));
        Assert.Equal(2, handler.RecordedRequests.Count);

        foreach (var req in handler.RecordedRequests)
        {
            Assert.Equal("https://api.anthropic.com/v1/messages", req.Uri.ToString());
            Assert.Equal("sk-ant-AnthropicTestSecret1234567890", req.AnthropicKeyHeader);
            Assert.Equal("2023-06-01", req.AnthropicVersionHeader);
        }

        Assert.Contains("tool_result", handler.RecordedRequests[1].BodyJson, StringComparison.Ordinal);
        Assert.Contains("toolu_01ABC", handler.RecordedRequests[1].BodyJson, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Kind == AgentEventKind.ModelReasoning && e.Message.Contains("Evaluating sandbox", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == AgentEventKind.UsageUpdated && e.Usage is { PromptTokens: 900, CandidatesTokens: 110, CachedTokens: 250 });
    }

    [Fact]
    public async Task ComputerUseAgent_NonGeminiOversizedAttachment_FailsLocallyWithoutCallingGoogleFilesApi()
    {
        var handler = new RecordingHandler([]);
        using var gemClient = new GeminiApiClient(handler);
        using var multi = new MultiProviderTransport(handler);
        using var secret = SealedSecret.Create("sk-proj-NoLeakToGoogleFilesKey123456".AsSpan());
        var policy = new ActionSecurityPolicyOptions { MinActionIntervalMs = 0, BlockSensitiveForegroundWindows = false };
        var agent = new ComputerUseAgent(gemClient, new FakeScreenCaptureService(), new FakeInputAutomationService(),
            new ActionSecurityGuard(policy), new UniversalFileCreator(new SafeFileSandbox(_sandboxDir)), policy, multi);

        byte[] oversized = new byte[16 * 1024 * 1024]; // > 15 MB
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunTurnAsync(
            secret,
            "Analyze big file",
            [new AttachmentInput("big.bin", "application/octet-stream", oversized)],
            [],
            new GeminiRequestOptions(Model: "gpt-5.4", EnableComputerUse: false, Provider: AiProviderKind.OpenAI),
            _ => Task.FromResult(true),
            _ => { },
            CancellationToken.None));

        Assert.Empty(handler.RecordedRequests);
    }

    [Fact]
    public void MainWindow_HidesSidebar_ShowsOnlyAddedModels_AndTracksHoverPowerSubmenu()
    {
        string testVaultDir = Path.Combine(_sandboxDir, "UiTestVault");
        Exception? staEx = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new AegisComputerUse.App.App();
                    app.InitializeComponent();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                }

                var vault = new MultiModelVault(testVaultDir);
                var win = new MainWindow(vault);
                try
                {
                    // 1. Initial state: 0 preset/unadded models in ModelRowsControl
                    Assert.Empty(win.ModelRowsControl.Items);
                    Assert.Equal(Visibility.Visible, win.PopupNoModelsPanel.Visibility);
                    Assert.Equal("Модель не добавлена", win.UnifiedModelLabel.Text);

                    // Verify custom obsidian window chrome maximize/restore toggle works cleanly
                    Assert.Equal("□", win.MaximizeGlyph.Text);
                    win.WinMaxBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(WindowState.Maximized, win.WindowState);
                    Assert.Equal("❐", win.MaximizeGlyph.Text);
                    win.WinMaxBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(WindowState.Normal, win.WindowState);
                    Assert.Equal("□", win.MaximizeGlyph.Text);

                    // 2. Open Settings Modal via Settings Gear button
                    win.SettingsGearBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(Visibility.Visible, win.SettingsOverlay.Visibility);

                    // 3. Verify Company -> Dynamic Version -> Custom ID flow
                    win.ProviderOpenAiBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Contains(win.SettingsVersionCombo.Items.OfType<ComboBoxItem>(),
                        item => item.Content?.ToString()?.Contains("gpt-5.4", StringComparison.Ordinal) == true);

                    // Select custom version row (last item, Tag == null) -> CustomVersionPanel becomes visible
                    win.SettingsVersionCombo.SelectedIndex = win.SettingsVersionCombo.Items.Count - 1;
                    Assert.Equal(Visibility.Visible, win.CustomVersionPanel.Visibility);
                    win.SettingsVersionCombo.SelectedIndex = 0; // Select GPT-5.4
                    Assert.Equal(Visibility.Collapsed, win.CustomVersionPanel.Visibility);

                    // 4. Add OpenAI GPT-5.4 model with API key
                    win.SettingsApiKeyBox.Password = "sk-proj-UiWorkflowOpenAiKey000111222";
                    win.AddModelConfirmBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                    Assert.Single(win.ModelRowsControl.Items);
                    Assert.Equal(Visibility.Collapsed, win.PopupNoModelsPanel.Visibility);
                    Assert.Equal("GPT-5.4", win.UnifiedModelLabel.Text);
                    Assert.Equal("Medium", win.UnifiedThinkingLabel.Text);
                    Assert.Equal(Visibility.Visible, win.SettingsAddSuccessText.Visibility);
                    Assert.Equal(0, win.SettingsApiKeyBox.SecurePassword.Length); // PasswordBox cleared immediately
                    Assert.True(vault.VaultExists);

                    // 5. Add a second model (Anthropic Claude Opus 4.6)
                    win.ProviderAnthropicBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    win.SettingsVersionCombo.SelectedIndex = 0;
                    win.SettingsApiKeyBox.Password = "sk-ant-UiWorkflowAnthropicKey000111";
                    win.AddModelConfirmBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                    Assert.Equal(2, win.ModelRowsControl.Items.Count);
                    Assert.Equal("Claude Opus 4.6", win.UnifiedModelLabel.Text);

                    // 6. Test hover power submenu placement target + per-row checkmark & High power switch
                    var firstMenuItem = (MainWindow.ModelMenuItem)win.ModelRowsControl.Items[0]!;
                    var secondMenuItem = (MainWindow.ModelMenuItem)win.ModelRowsControl.Items[1]!;
                    var fakeRowBtn1 = new Button { Tag = firstMenuItem };
                    var fakeRowBtn2 = new Button { Tag = secondMenuItem };

                    win.SimulateHoverModelRow(fakeRowBtn1, firstMenuItem);
                    Assert.Same(fakeRowBtn1, win.ThinkingSubmenuPopup.PlacementTarget);
                    Assert.Equal(Visibility.Visible, win.CheckMedium.Visibility);
                    Assert.Equal(Visibility.Collapsed, win.CheckHigh.Visibility);

                    // Hover second row -> PlacementTarget switches cleanly to second row
                    win.SimulateHoverModelRow(fakeRowBtn2, secondMenuItem);
                    Assert.Same(fakeRowBtn2, win.ThinkingSubmenuPopup.PlacementTarget);

                    // Click "High" in power submenu -> applies to hovered model & closes popups cleanly
                    win.ThinkingHighBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal("Claude Opus 4.6", win.UnifiedModelLabel.Text);
                    Assert.Equal("High", win.UnifiedThinkingLabel.Text);

                    // 7. Wipe all clears RAM and deletes Vault on disk
                    win.SettingsWipeAllBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Empty(win.ModelRowsControl.Items);
                    Assert.Equal(Visibility.Visible, win.PopupNoModelsPanel.Visibility);
                    Assert.False(vault.VaultExists);
                }
                finally
                {
                    win.Close();
                }
            }
            catch (Exception ex)
            {
                staEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(staEx);
    }



    [Fact]
    public void AiProviderCatalog_UsesValidGemini25ComputerUseId_AndResolvesContextLimit()
    {
        var geminiModels = AiProviderCatalog.GetModelsForProvider(AiProviderKind.GoogleGemini);
        Assert.Contains(geminiModels, m => m.ModelId == "gemini-2.5-computer-use-preview-10-2025");
        Assert.DoesNotContain(geminiModels, m => m.ModelId == "gemini-2.5-computer-use-preview");
        Assert.Equal(131_072, AiProviderCatalog.ResolveContextLimit(AiProviderKind.GoogleGemini, "gemini-2.5-computer-use-preview-10-2025"));
    }

    [Fact]
    public async Task GeminiApiClient_ListModelsAsync_FiltersGenerateContent_StripsPrefix_UsesHeaderAuth()
    {
        var handler = new RecordingHandler(
        [
            (HttpStatusCode.OK, """
            {
              "models": [
                {
                  "name": "models/embedding-001",
                  "displayName": "Embedding",
                  "inputTokenLimit": 2048,
                  "supportedGenerationMethods": ["embedContent"]
                },
                {
                  "name": "models/gemini-2.5-pro",
                  "displayName": "Gemini 2.5 Pro",
                  "description": "Coding and reasoning",
                  "inputTokenLimit": 1048576,
                  "supportedGenerationMethods": ["generateContent", "countTokens"]
                },
                {
                  "name": "models/gemini-2.5-computer-use-preview-10-2025",
                  "displayName": "Gemini 2.5 Computer Use",
                  "description": "Browser and UI control",
                  "inputTokenLimit": 131072,
                  "supportedGenerationMethods": ["generateContent"]
                },
                {
                  "name": "models/../evil-model",
                  "displayName": "Evil",
                  "supportedGenerationMethods": ["generateContent"]
                }
              ]
            }
            """)
        ]);

        using var apiClient = new GeminiApiClient(handler);
        using var secret = SealedSecret.Create("AIzaSyListModelsSecret000111222".AsSpan());

        var models = await apiClient.ListModelsAsync(secret, CancellationToken.None);

        Assert.Equal(2, models.Count);
        Assert.Equal("gemini-2.5-computer-use-preview-10-2025", models[0].ModelId);
        Assert.Equal("gemini-2.5-pro", models[1].ModelId);
        Assert.Single(handler.RecordedRequests);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models", handler.RecordedRequests[0].Uri.ToString());
        Assert.Equal(string.Empty, handler.RecordedRequests[0].Uri.Query);
        Assert.Equal("AIzaSyListModelsSecret000111222", handler.RecordedRequests[0].ApiKeyHeader);
    }

    [Fact]
    public async Task MultiProviderTransport_ListModelsAsync_FiltersOpenAiUtilities_AndParsesAnthropicModels()
    {
        var handler = new RecordingHandler(
        [
            (HttpStatusCode.OK, """
            {
              "object": "list",
              "data": [
                { "id": "whisper-1", "created": 1700000000 },
                { "id": "text-embedding-3-large", "created": 1700000100 },
                { "id": "gpt-5.4", "created": 1740000000 },
                { "id": "computer-use-preview", "created": 1730000000 }
              ]
            }
            """),
            (HttpStatusCode.OK, """
            {
              "data": [
                { "id": "claude-opus-4-6", "display_name": "Claude Opus 4.6" },
                { "id": "claude-sonnet-4-6", "display_name": "Claude Sonnet 4.6" }
              ]
            }
            """)
        ]);

        using var transport = new MultiProviderTransport(handler);
        using var openAiKey = SealedSecret.Create("sk-proj-ListModelsOpenAiSecret12345".AsSpan());
        using var antKey = SealedSecret.Create("sk-ant-ListModelsAnthropicSecret123".AsSpan());

        var openAiModels = await transport.ListModelsAsync(AiProviderKind.OpenAI, openAiKey, CancellationToken.None);
        var antModels = await transport.ListModelsAsync(AiProviderKind.Anthropic, antKey, CancellationToken.None);

        Assert.Equal(2, openAiModels.Count);
        Assert.Equal("computer-use-preview", openAiModels[0].ModelId);
        Assert.Equal("gpt-5.4", openAiModels[1].ModelId);

        Assert.Equal(2, antModels.Count);
        Assert.Equal("Claude Opus 4.6", antModels[0].DisplayName);

        Assert.Equal("https://api.openai.com/v1/models", handler.RecordedRequests[0].Uri.ToString());
        Assert.Equal("Bearer sk-proj-ListModelsOpenAiSecret12345", handler.RecordedRequests[0].AuthorizationHeader);
        Assert.Equal("https://api.anthropic.com/v1/models", handler.RecordedRequests[1].Uri.ToString());
        Assert.Equal("sk-ant-ListModelsAnthropicSecret123", handler.RecordedRequests[1].AnthropicKeyHeader);
    }

    [Fact]
    public async Task ComputerUseAgent_Gemini25ComputerUse_UsesThinkingBudgetAndEnvironmentBrowser_WithThinkingFallback()
    {
        var handler = new RecordingHandler(
        [
            // Simulate model rejecting thinkingConfig on first request -> should strip thinkingConfig cleanly and retry
            (HttpStatusCode.BadRequest, """{"error":{"message":"Unknown name \"thinkingBudget\" at 'generation_config.thinking_config'"}}"""),
            (HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": {
                  "role": "model",
                  "parts": [{ "text": "Gemini 2.5 Computer Use active and working cleanly." }]
                }
              }]
            }
            """)
        ]);

        using var apiClient = new GeminiApiClient(handler);
        using var secret = SealedSecret.Create("AIzaSyGemini25PreviewSecret123456".AsSpan());
        var policy = new ActionSecurityPolicyOptions { MinActionIntervalMs = 0, BlockSensitiveForegroundWindows = false };
        var agent = new ComputerUseAgent(apiClient, new FakeScreenCaptureService(), new FakeInputAutomationService(),
            new ActionSecurityGuard(policy), new UniversalFileCreator(new SafeFileSandbox(_sandboxDir)), policy);

        string answer = await agent.RunTurnAsync(
            secret,
            "Inspect page",
            [],
            [],
            new GeminiRequestOptions(Model: "gemini-2.5-computer-use-preview-10-2025", ThinkingLevel: "HIGH"),
            _ => Task.FromResult(true),
            _ => { },
            CancellationToken.None);

        Assert.Equal("Gemini 2.5 Computer Use active and working cleanly.", answer);
        Assert.Equal(2, handler.RecordedRequests.Count);

        // First request: must use thinkingBudget (16384 for HIGH), ENVIRONMENT_BROWSER, maxOutputTokens <= 64000, NO thinkingLevel
        Assert.Contains("\"thinkingBudget\":16384", handler.RecordedRequests[0].BodyJson, StringComparison.Ordinal);
        Assert.Contains("ENVIRONMENT_BROWSER", handler.RecordedRequests[0].BodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("thinkingLevel", handler.RecordedRequests[0].BodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("enablePromptInjectionDetection", handler.RecordedRequests[0].BodyJson, StringComparison.Ordinal);

        // Fallback retry: strips thinkingConfig cleanly while keeping ENVIRONMENT_BROWSER
        Assert.DoesNotContain("thinkingConfig", handler.RecordedRequests[1].BodyJson, StringComparison.Ordinal);
        Assert.Contains("ENVIRONMENT_BROWSER", handler.RecordedRequests[1].BodyJson, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_LoadModelsFromKey_PopulatesDynamicList_AndPreservesApiKeyUntilAdd()
    {
        string testVaultDir = Path.Combine(_sandboxDir, "UiDynamicModelsVault");
        Exception? staEx = null;

        var handler = new RecordingHandler(
        [
            (HttpStatusCode.OK, """
            {
              "models": [
                {
                  "name": "models/gemini-2.5-computer-use-preview-10-2025",
                  "displayName": "Gemini 2.5 Computer Use Live",
                  "inputTokenLimit": 131072,
                  "supportedGenerationMethods": ["generateContent"]
                },
                {
                  "name": "models/gemini-2.5-flash",
                  "displayName": "Gemini 2.5 Flash Live",
                  "inputTokenLimit": 1048576,
                  "supportedGenerationMethods": ["generateContent"]
                }
              ]
            }
            """)
        ]);

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            try
            {
                if (Application.Current is null)
                {
                    var app = new AegisComputerUse.App.App();
                    app.InitializeComponent();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                }

                var vault = new MultiModelVault(testVaultDir);
                using var mockClient = new GeminiApiClient(handler);
                var win = new MainWindow(vault, mockClient);
                try
                {
                    win.SettingsGearBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    win.SettingsApiKeyBox.Password = "AIzaSyLiveDiscoveryUiTestKey000111";

                    Task loadTask = win.LoadModelsFromKeyAsync();
                    var frame = new DispatcherFrame();
                    _ = loadTask.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                    loadTask.GetAwaiter().GetResult();

                    // Live models populated & ApiKeyBox NOT cleared prematurely
                    Assert.Equal("AIzaSyLiveDiscoveryUiTestKey000111".Length, win.SettingsApiKeyBox.SecurePassword.Length);
                    Assert.Contains(win.SettingsVersionCombo.Items.OfType<ComboBoxItem>(),
                        item => item.Content?.ToString()?.Contains("gemini-2.5-computer-use-preview-10-2025", StringComparison.Ordinal) == true);
                    Assert.Equal(Visibility.Visible, win.SettingsAddSuccessText.Visibility);

                    // Adding selected fetched model clears ApiKeyBox cleanly and updates Context Limit
                    win.AddModelConfirmBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(0, win.SettingsApiKeyBox.SecurePassword.Length);
                    Assert.Equal("Gemini 2.5 Computer Use Live", win.UnifiedModelLabel.Text);
                    Assert.Single(win.ModelRowsControl.Items);
                }
                finally
                {
                    win.Close();
                }
            }
            catch (Exception ex)
            {
                staEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(staEx);
    }



    [Fact]
    public void GeminiApiException_ParseRateLimitInfo_DetectsZeroFreeTierLimit_AndFormatsFriendlyMessage()
    {
        string googleJson = """
        {
          "error": {
            "code": 429,
            "message": "You exceeded your current quota, please check your plan and billing details.\n* Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_requests, limit: 0, model: gemini-4-argon.\nPlease retry in 9h50m44.03s.",
            "status": "RESOURCE_EXHAUSTED",
            "details": [
              { "@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "35444s" }
            ]
          }
        }
        """;

        var ex = new GeminiApiException(429, googleJson);
        var info = ex.ParseRateLimitInfo();

        Assert.True(info.IsZeroLimit);
        Assert.True(info.IsFreeTier);
        Assert.Equal(TimeSpan.FromSeconds(35444), info.RetryDelay);
        Assert.Equal("9 ч 50 мин", info.FormatRetryDelayRussian());

        string uiMsg = MainWindow.FormatRateLimitMessage("gemini-4-argon", ex);
        Assert.Contains("Free Tier limit: 0", uiMsg, StringComparison.Ordinal);
        Assert.Contains("gemini-2.5-flash", uiMsg, StringComparison.Ordinal);
        Assert.Contains("Billing", uiMsg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComputerUseAgent_RetriesShortRpm429_AndFailsFastOnZeroFreeTierQuota()
    {
        // 1. Transient RPM 429 (retryDelay: 0.05s, no limit: 0) -> auto-retries and succeeds
        var transientHandler = new RecordingHandler(
        [
            ((HttpStatusCode)429, """
            {
              "error": {
                "code": 429,
                "message": "Rate limit reached",
                "details": [{ "@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "0.05s" }]
              }
            }
            """),
            (HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": { "role": "model", "parts": [{ "text": "Recovered cleanly after short RPM delay." }] }
              }]
            }
            """)
        ]);

        using var api1 = new GeminiApiClient(transientHandler);
        using var secret = SealedSecret.Create("AIzaSyRateLimitTestKey000111222333".AsSpan());
        var policy = new ActionSecurityPolicyOptions { MinActionIntervalMs = 0, BlockSensitiveForegroundWindows = false };
        var agent1 = new ComputerUseAgent(api1, new FakeScreenCaptureService(), new FakeInputAutomationService(),
            new ActionSecurityGuard(policy), new UniversalFileCreator(new SafeFileSandbox(_sandboxDir)), policy);

        var events = new List<AgentEvent>();
        string ok = await agent1.RunTurnAsync(
            secret, "Hello", [], [],
            new GeminiRequestOptions(Model: "gemini-2.5-flash", EnableComputerUse: false),
            _ => Task.FromResult(true),
            ev => events.Add(ev),
            CancellationToken.None);

        Assert.Equal("Recovered cleanly after short RPM delay.", ok);
        Assert.Equal(2, transientHandler.RecordedRequests.Count);
        Assert.Contains(events, e => e.Kind == AgentEventKind.SecurityWarning && e.Message.Contains("HTTP 429", StringComparison.Ordinal));

        // 2. Hard FreeTier limit: 0 with 35444s -> fails fast in 1 request without waiting
        var zeroHandler = new RecordingHandler(
        [
            ((HttpStatusCode)429, """
            {
              "error": {
                "code": 429,
                "message": "Quota exceeded, limit: 0, FreeTier",
                "details": [{ "@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "35444s" }]
              }
            }
            """)
        ]);

        using var api2 = new GeminiApiClient(zeroHandler);
        var agent2 = new ComputerUseAgent(api2, new FakeScreenCaptureService(), new FakeInputAutomationService(),
            new ActionSecurityGuard(policy), new UniversalFileCreator(new SafeFileSandbox(_sandboxDir)), policy);

        var thrown = await Assert.ThrowsAsync<GeminiApiException>(() => agent2.RunTurnAsync(
            secret, "Hello", [], [],
            new GeminiRequestOptions(Model: "gemini-4-argon", EnableComputerUse: false),
            _ => Task.FromResult(true),
            _ => { },
            CancellationToken.None));

        Assert.Equal(429, thrown.StatusCode);
        Assert.Single(zeroHandler.RecordedRequests);
    }

    [Fact]
    public async Task ComputerUseAgent_Gemini25Flash_SkipsNativeProbe_UsesCompactBudget_AndTrimsStaleScreenshots()
    {
        static string ClickTurn(int step) => $$"""
            {
              "candidates": [{
                "content": {
                  "role": "model",
                  "parts": [{ "functionCall": { "name": "click_at", "args": { "x": {{100 + step * 50}}, "y": 300 } } }]
                }
              }]
            }
            """;

        var handler = new RecordingHandler(
        [
            (HttpStatusCode.OK, ClickTurn(1)),
            (HttpStatusCode.OK, ClickTurn(2)),
            (HttpStatusCode.OK, ClickTurn(3)),
            (HttpStatusCode.OK, ClickTurn(4)),
            (HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": {
                  "role": "model",
                  "parts": [{ "text": "Выполнено за 4 клика с экономией трафика." }]
                }
              }]
            }
            """)
        ]);

        using var apiClient = new GeminiApiClient(handler);
        using var secret = SealedSecret.Create("AIzaSyFlashSpeedTestKey000111222333".AsSpan());
        var policy = new ActionSecurityPolicyOptions { MinActionIntervalMs = 0, BlockSensitiveForegroundWindows = false };
        var fakeInput = new FakeInputAutomationService();
        var agent = new ComputerUseAgent(apiClient, new FakeScreenCaptureService(), fakeInput,
            new ActionSecurityGuard(policy), new UniversalFileCreator(new SafeFileSandbox(_sandboxDir)), policy);

        var history = new List<JsonNode>();
        string answer = await agent.RunTurnAsync(
            secret,
            "Perform 4 clicks quickly",
            [],
            history,
            new GeminiRequestOptions(Model: "gemini-2.5-flash", ThinkingLevel: "MEDIUM", ToolMode: ComputerUseToolMode.Auto),
            _ => Task.FromResult(true),
            _ => { },
            CancellationToken.None);

        Assert.Contains("Выполнено", answer, StringComparison.Ordinal);
        Assert.Equal(4, fakeInput.ExecutedCalls.Count);
        Assert.Equal(5, handler.RecordedRequests.Count); // Zero wasted 400 roundtrips!
        Assert.Equal(ComputerUseToolMode.FunctionCallingFallback, agent.LastEffectiveToolMode);

        // Request 0: uses FunctionDeclarations directly + compact 2048 MEDIUM thinkingBudget
        Assert.Contains("functionDeclarations", handler.RecordedRequests[0].BodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"computerUse\"", handler.RecordedRequests[0].BodyJson, StringComparison.Ordinal);
        Assert.Contains("\"thinkingBudget\":2048", handler.RecordedRequests[0].BodyJson, StringComparison.Ordinal);

        // Request 4 (Turn 5): 5 total screenshots captured (1 initial + 4 post-click), trimmed to at most 3 newest inlineData blobs
        int sentImagesCount = System.Text.RegularExpressions.Regex.Matches(
            handler.RecordedRequests[4].BodyJson,
            "\"inlineData\"").Count;
        Assert.Equal(3, sentImagesCount);
        Assert.Contains("screenshot omitted", handler.RecordedRequests[4].BodyJson, StringComparison.OrdinalIgnoreCase);

        // Original in-memory caller history stays complete & unmutated
        int historyImagesCount = System.Text.RegularExpressions.Regex.Matches(
            new JsonArray(history.Select(h => h.DeepClone()).ToArray()).ToJsonString(),
            "\"inlineData\"").Count;
        Assert.Equal(5, historyImagesCount);
    }

    [Fact]
    public async Task ProbeModelAsync_SupportsGeminiOpenAiAndAnthropic_WithoutLeakingKeyInUrl()
    {
        var handler = new RecordingHandler(
        [
            (HttpStatusCode.OK, """{"candidates":[{"content":{"role":"model","parts":[{"text":"pong"}]}}]}"""),
            (HttpStatusCode.OK, """{"output":[{"type":"message","content":[{"type":"output_text","text":"pong"}]}]}"""),
            (HttpStatusCode.OK, """{"content":[{"type":"text","text":"pong"}]}""")
        ]);

        using var gemClient = new GeminiApiClient(handler);
        using var multi = new MultiProviderTransport(handler);
        using var gKey = SealedSecret.Create("AIzaSyProbeGeminiSecret000111222".AsSpan());
        using var oKey = SealedSecret.Create("sk-proj-ProbeOpenAiSecret000111".AsSpan());
        using var aKey = SealedSecret.Create("sk-ant-ProbeAnthropicSecret000".AsSpan());

        long gMs = await gemClient.ProbeModelAsync(gKey, "gemini-2.5-flash");
        long oMs = await multi.ProbeModelAsync(AiProviderKind.OpenAI, oKey, "gpt-5.4");
        long aMs = await multi.ProbeModelAsync(AiProviderKind.Anthropic, aKey, "claude-opus-4-6");

        Assert.True(gMs >= 1);
        Assert.True(oMs >= 1);
        Assert.True(aMs >= 1);
        Assert.Equal(3, handler.RecordedRequests.Count);
        Assert.Equal("/v1beta/models/gemini-2.5-flash:generateContent", handler.RecordedRequests[0].Uri.AbsolutePath);
        Assert.Equal(string.Empty, handler.RecordedRequests[0].Uri.Query);
        Assert.Equal("Bearer sk-proj-ProbeOpenAiSecret000111", handler.RecordedRequests[1].AuthorizationHeader);
        Assert.Equal("sk-ant-ProbeAnthropicSecret000", handler.RecordedRequests[2].AnthropicKeyHeader);
    }

    [Fact]
    public void MainWindow_AgentParamsPanel_AllowsEditingStepsSteppersModes_AndProbePreservesApiKey()
    {
        string testVaultDir = Path.Combine(_sandboxDir, "UiParamsAndProbeVault");
        Exception? staEx = null;

        var handler = new RecordingHandler(
        [
            (HttpStatusCode.OK, """{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]}}]}"""),
            ((HttpStatusCode)429, """
            {
              "error": {
                "code": 429,
                "message": "Quota exceeded, limit: 0, FreeTier, model: gemini-4-argon",
                "details": [{ "@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "3600s" }]
              }
            }
            """)
        ]);

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            try
            {
                if (Application.Current is null)
                {
                    var app = new AegisComputerUse.App.App();
                    app.InitializeComponent();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                }

                var vault = new MultiModelVault(testVaultDir);
                using var mockClient = new GeminiApiClient(handler);
                var win = new MainWindow(vault, mockClient);
                try
                {
                    // 1. Open in-window interactive AgentParamsPanel (not a focus-stealing Win32 Popup!)
                    Assert.Equal(Visibility.Collapsed, win.AgentParamsPanel.Visibility);
                    win.AgentParamsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(Visibility.Visible, win.AgentParamsPanel.Visibility);
                    Assert.Equal("25", win.MaxStepsBox.Text);

                    // 2. Steppers (+ / - / direct typing) work cleanly & keep panel open
                    win.StepIncrementBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal("26", win.MaxStepsBox.Text);
                    win.StepDecrementBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal("25", win.MaxStepsBox.Text);
                    win.MaxStepsBox.Text = "12";
                    Assert.Equal("Экран • 12 ш.", win.UnifiedScopeBadge.Text);
                    Assert.Equal(Visibility.Visible, win.AgentParamsPanel.Visibility);

                    // 3. Quick Chat / Screen toggles synchronize EnableComputerUseCheck & badge
                    win.QuickChatModeBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.False(win.EnableComputerUseCheck.IsChecked);
                    Assert.Equal("Чат", win.UnifiedScopeBadge.Text);

                    win.QuickScreenModeBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.True(win.EnableComputerUseCheck.IsChecked);
                    Assert.Equal("Экран • 12 ш.", win.UnifiedScopeBadge.Text);

                    // 4. Probe key in Settings tests connectivity & quota WITHOUT erasing PasswordBox
                    win.SettingsGearBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    win.SettingsApiKeyBox.Password = "AIzaSyProbeUiSecretKey000111222";

                    void RunSync(Task task)
                    {
                        var frame = new DispatcherFrame();
                        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
                        Dispatcher.PushFrame(frame);
                        task.GetAwaiter().GetResult();
                    }

                    // Probe 1: Gemini 2.5 Flash succeeds -> SettingsAddSuccessText visible, key preserved
                    win.SettingsVersionCombo.SelectedIndex = 0;
                    RunSync(win.ProbeModelFromSettingsAsync());
                    Assert.Equal(Visibility.Visible, win.SettingsAddSuccessText.Visibility);
                    Assert.Contains("доступна", win.SettingsAddSuccessText.Text, StringComparison.Ordinal);
                    Assert.Equal("AIzaSyProbeUiSecretKey000111222".Length, win.SettingsApiKeyBox.SecurePassword.Length);

                    // Probe 2: Gemini 4 Argon Free Tier limit: 0 -> clear localized 429 explanation, key STILL preserved
                    int argonIdx = -1;
                    for (int i = 0; i < win.SettingsVersionCombo.Items.Count; i++)
                    {
                        if ((win.SettingsVersionCombo.Items[i] as ComboBoxItem)?.Content?.ToString()?.Contains("gemini-4-argon", StringComparison.Ordinal) == true)
                        {
                            argonIdx = i;
                            break;
                        }
                    }
                    Assert.True(argonIdx >= 0);
                    win.SettingsVersionCombo.SelectedIndex = argonIdx;
                    RunSync(win.ProbeModelFromSettingsAsync());
                    Assert.Equal(Visibility.Visible, win.SettingsAddErrorText.Visibility);
                    Assert.Contains("Free Tier limit: 0", win.SettingsAddErrorText.Text, StringComparison.Ordinal);
                    Assert.Equal("AIzaSyProbeUiSecretKey000111222".Length, win.SettingsApiKeyBox.SecurePassword.Length);
                }
                finally
                {
                    win.Close();
                }
            }
            catch (Exception ex)
            {
                staEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(staEx);
    }



    [Fact]
    public async Task ComputerUseAgent_RetriesTransient503_AndFormatsLocalizedOverloadAfterExhaustion()
    {
        const string google503Json = """
            {
              "error": {
                "code": 503,
                "message": "This model is currently experiencing high demand. Spikes in demand are usually temporary. Please try again later.",
                "status": "UNAVAILABLE"
              }
            }
            """;

        var recoveringHandler = new RecordingHandler(
        [
            (HttpStatusCode.ServiceUnavailable, google503Json),
            (HttpStatusCode.ServiceUnavailable, google503Json),
            (HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": { "role": "model", "parts": [{ "text": "Ответ получен после пика нагрузки." }] }
              }]
            }
            """)
        ]);

        using var api1 = new GeminiApiClient(recoveringHandler);
        using var secret = SealedSecret.Create("AIzaSyOverload503TestKey000111222333".AsSpan());
        var policy = new ActionSecurityPolicyOptions { MinActionIntervalMs = 0, BlockSensitiveForegroundWindows = false };
        var agent1 = new ComputerUseAgent(api1, new FakeScreenCaptureService(), new FakeInputAutomationService(),
            new ActionSecurityGuard(policy), new UniversalFileCreator(new SafeFileSandbox(_sandboxDir)), policy)
        {
            TransientServerRetryBaseDelay = TimeSpan.FromMilliseconds(15)
        };

        var events = new List<AgentEvent>();
        string recovered = await agent1.RunTurnAsync(
            secret,
            "Hello during spike",
            [],
            [],
            new GeminiRequestOptions(Model: "gemini-2.5-flash", EnableComputerUse: false),
            _ => Task.FromResult(true),
            ev => events.Add(ev),
            CancellationToken.None);

        Assert.Equal("Ответ получен после пика нагрузки.", recovered);
        Assert.Equal(3, recoveringHandler.RecordedRequests.Count);
        Assert.Contains(events, e => e.Kind == AgentEventKind.Progress && e.Message.Contains("HTTP 503", StringComparison.Ordinal) && e.Message.Contains("1/2", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == AgentEventKind.Progress && e.Message.Contains("HTTP 503", StringComparison.Ordinal) && e.Message.Contains("2/2", StringComparison.Ordinal));

        // Exhausted 503 (3 failed attempts: initial + 2 retries) throws clean GeminiApiException -> localized UI text without raw JSON
        var exhaustedHandler = new RecordingHandler(
        [
            (HttpStatusCode.ServiceUnavailable, google503Json),
            (HttpStatusCode.ServiceUnavailable, google503Json),
            (HttpStatusCode.ServiceUnavailable, google503Json)
        ]);

        using var api2 = new GeminiApiClient(exhaustedHandler);
        var agent2 = new ComputerUseAgent(api2, new FakeScreenCaptureService(), new FakeInputAutomationService(),
            new ActionSecurityGuard(policy), new UniversalFileCreator(new SafeFileSandbox(_sandboxDir)), policy)
        {
            TransientServerRetryBaseDelay = TimeSpan.FromMilliseconds(10)
        };

        var thrown = await Assert.ThrowsAsync<GeminiApiException>(() => agent2.RunTurnAsync(
            secret, "Hello", [], [],
            new GeminiRequestOptions(Model: "gemini-2.5-flash", EnableComputerUse: false),
            _ => Task.FromResult(true), _ => { }, CancellationToken.None));

        Assert.Equal(503, thrown.StatusCode);
        Assert.Equal(3, exhaustedHandler.RecordedRequests.Count);

        string uiMsg = MainWindow.FormatTransientOverloadMessage("gemini-2.5-flash", thrown);
        Assert.Contains("HTTP 503", uiMsg, StringComparison.Ordinal);
        Assert.Contains("временный сбой", uiMsg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("This model is currently experiencing high demand", uiMsg, StringComparison.Ordinal);
        Assert.DoesNotContain("{", uiMsg, StringComparison.Ordinal);
        Assert.DoesNotContain("}", uiMsg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeminiApiClient_TranscribeAudioAsync_RetriesOn503_AndExtractShortMessageStripsRawJson()
    {
        const string google503Json = """
            {
              "error": {
                "code": 503,
                "message": "This model is currently experiencing high demand. Spikes in demand are usually temporary. Please try again later.",
                "status": "UNAVAILABLE"
              }
            }
            """;

        var handler = new RecordingHandler(
        [
            (HttpStatusCode.ServiceUnavailable, google503Json),
            (HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": { "role": "model", "parts": [{ "text": "Открой калькулятор" }] }
              }]
            }
            """)
        ]);

        using var apiClient = new GeminiApiClient(handler)
        {
            TransientRetryDelay = TimeSpan.FromMilliseconds(15)
        };
        using var secret = SealedSecret.Create("AIzaSyVoice503TestKey000111222333".AsSpan());
        byte[] fakeWav = new byte[128];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(fakeWav, 0);

        string text = await apiClient.TranscribeAudioAsync(secret, "gemini-2.5-flash", fakeWav, CancellationToken.None);

        Assert.Equal("Открой калькулятор", text);
        Assert.Equal(2, handler.RecordedRequests.Count);

        var ex = new GeminiApiException(503, google503Json);
        Assert.Equal(
            "This model is currently experiencing high demand. Spikes in demand are usually temporary. Please try again later.",
            ex.ExtractShortMessage());
    }

    [Fact]
    public void MainWindow_AutoFallbackOnOverload_SwitchesToGemini25Flash_WithoutDuplicatingHistory()
    {
        string testVaultDir = Path.Combine(_sandboxDir, "UiAutoFallbackVault");
        Exception? staEx = null;

        const string google503Json = """
            {
              "error": {
                "code": 503,
                "message": "This model is currently experiencing high demand. Spikes in demand are usually temporary. Please try again later.",
                "status": "UNAVAILABLE"
              }
            }
            """;

        var handler = new RecordingHandler(
        [
            (HttpStatusCode.ServiceUnavailable, google503Json),
            (HttpStatusCode.ServiceUnavailable, google503Json),
            (HttpStatusCode.ServiceUnavailable, google503Json),
            (HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": { "role": "model", "parts": [{ "text": "Выполнено через резервную Gemini 2.5 Flash." }] }
              }]
            }
            """)
        ]);

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            try
            {
                if (Application.Current is null)
                {
                    var app = new AegisComputerUse.App.App();
                    app.InitializeComponent();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                }

                var vault = new MultiModelVault(testVaultDir);
                using var mockClient = new GeminiApiClient(handler);
                var win = new MainWindow(vault, mockClient)
                {
                    AgentRetryBaseDelayForTests = TimeSpan.FromMilliseconds(5)
                };
                try
                {
                    // Add gemini-3.8-flash and disable live screen capture for deterministic unit test
                    win.SettingsGearBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    int idx38 = -1;
                    for (int i = 0; i < win.SettingsVersionCombo.Items.Count; i++)
                    {
                        if ((win.SettingsVersionCombo.Items[i] as ComboBoxItem)?.Content?.ToString()?.Contains("gemini-3.8-flash", StringComparison.Ordinal) == true)
                        {
                            idx38 = i;
                            break;
                        }
                    }
                    Assert.True(idx38 >= 0);
                    win.SettingsVersionCombo.SelectedIndex = idx38;
                    win.SettingsApiKeyBox.Password = "AIzaSyFallbackTestKey000111222333";
                    win.AddModelConfirmBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                    win.EnableComputerUseCheck.IsChecked = false;
                    Assert.True(win.AutoFallbackOnOverloadCheck.IsChecked);

                    win.PromptBox.Text = "Привет при пике 503";
                    Task runTask = win.ExecutePromptAsync();
                    var frame = new DispatcherFrame();
                    _ = runTask.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                    runTask.GetAwaiter().GetResult();

                    // 3 attempts on gemini-3.8-flash + 1 automatic failover request on gemini-2.5-flash with same key
                    Assert.Equal(4, handler.RecordedRequests.Count);
                    Assert.Equal("/v1beta/models/gemini-3.8-flash:generateContent", handler.RecordedRequests[0].Uri.AbsolutePath);
                    Assert.Equal("/v1beta/models/gemini-2.5-flash:generateContent", handler.RecordedRequests[3].Uri.AbsolutePath);
                    Assert.Equal("AIzaSyFallbackTestKey000111222333", handler.RecordedRequests[3].ApiKeyHeader);
                    Assert.Equal(string.Empty, handler.RecordedRequests[3].Uri.Query);

                    // History has strictly [user, model] (no orphan duplicated user entries from failed attempts!)
                    Assert.Equal(2, win.HistoryForTests.Count);
                    Assert.Contains(win.ChatEntriesForTests, c => c.Header.Contains("АВТО-РЕЗЕРВ", StringComparison.Ordinal));
                }
                finally
                {
                    win.Close();
                }
            }
            catch (Exception ex)
            {
                staEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(staEx);
    }



    [Fact]
    public void MainWindow_OverloadCardActionButton_ClonesGoogleKeyToGemini25Flash_AndRetriesPrompt()
    {
        string testVaultDir = Path.Combine(_sandboxDir, "UiActionButtonVault");
        Exception? staEx = null;

        const string google503Json = """
            {
              "error": {
                "code": 503,
                "message": "This model is currently experiencing high demand. Spikes in demand are usually temporary. Please try again later.",
                "status": "UNAVAILABLE"
              }
            }
            """;

        var handler = new RecordingHandler(
        [
            (HttpStatusCode.ServiceUnavailable, google503Json),
            (HttpStatusCode.ServiceUnavailable, google503Json),
            (HttpStatusCode.ServiceUnavailable, google503Json),
            (HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": { "role": "model", "parts": [{ "text": "Ответила после нажатия кнопки переключения." }] }
              }]
            }
            """)
        ]);

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            try
            {
                if (Application.Current is null)
                {
                    var app = new AegisComputerUse.App.App();
                    app.InitializeComponent();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                }

                var vault = new MultiModelVault(testVaultDir);
                using var mockClient = new GeminiApiClient(handler);
                var win = new MainWindow(vault, mockClient)
                {
                    AgentRetryBaseDelayForTests = TimeSpan.FromMilliseconds(5)
                };
                try
                {
                    win.SettingsGearBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    int idx38 = -1;
                    for (int i = 0; i < win.SettingsVersionCombo.Items.Count; i++)
                    {
                        if ((win.SettingsVersionCombo.Items[i] as ComboBoxItem)?.Content?.ToString()?.Contains("gemini-3.8-flash", StringComparison.Ordinal) == true)
                        {
                            idx38 = i;
                            break;
                        }
                    }
                    win.SettingsVersionCombo.SelectedIndex = idx38;
                    win.SettingsApiKeyBox.Password = "AIzaSyActionBtnSecret000111222333";
                    win.AddModelConfirmBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                    win.EnableComputerUseCheck.IsChecked = false;
                    win.AutoFallbackOnOverloadCheck.IsChecked = false;

                    // 1. Primary run fails with 503 -> history rolled back cleanly & action button shown on error card
                    win.PromptBox.Text = "Задача через кнопку переключения";
                    Task runTask = win.ExecutePromptAsync();
                    var frame1 = new DispatcherFrame();
                    _ = runTask.ContinueWith(_ => dispatcher.BeginInvoke(() => frame1.Continue = false));
                    Dispatcher.PushFrame(frame1);
                    runTask.GetAwaiter().GetResult();

                    Assert.Empty(win.HistoryForTests);
                    var errorCard = win.ChatEntriesForTests.Last(c => c.Header.Contains("ПЕРЕГРУЖЕН", StringComparison.Ordinal));
                    var stack = Assert.IsType<StackPanel>(errorCard.RichVisual);
                    var actionWrap = Assert.IsType<WrapPanel>(stack.Children[1]);
                    var switchBtn = actionWrap.Children.OfType<Button>().First(b => b.Content?.ToString()?.Contains("gemini-2.5-flash", StringComparison.Ordinal) == true);

                    // 2. Click quick-switch action button -> clones secret cleanly into Gemini 2.5 Flash & runs retry!
                    switchBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var frame2 = new DispatcherFrame();
                    _ = Task.Run(async () =>
                    {
                        for (int k = 0; k < 100 && handler.RecordedRequests.Count < 4; k++)
                            await Task.Delay(20);
                        await Task.Delay(40);
                        _ = dispatcher.BeginInvoke(() => frame2.Continue = false);
                    });
                    Dispatcher.PushFrame(frame2);

                    Assert.Equal("Gemini 2.5 Flash", win.UnifiedModelLabel.Text);
                    Assert.Equal(2, win.ModelRowsControl.Items.Count);
                    Assert.Equal(4, handler.RecordedRequests.Count);
                    Assert.Equal("/v1beta/models/gemini-2.5-flash:generateContent", handler.RecordedRequests[3].Uri.AbsolutePath);
                    Assert.Equal("AIzaSyActionBtnSecret000111222333", handler.RecordedRequests[3].ApiKeyHeader);
                    Assert.Equal(2, win.HistoryForTests.Count);
                }
                finally
                {
                    win.Close();
                }
            }
            catch (Exception ex)
            {
                staEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(staEx);
    }


    [Fact]
    public void VoiceCaptureService_CalculateRms_DistinguishesSilenceFromSpeech_AndBuildsWavHeader()
    {
        byte[] silent = new byte[1600];
        double silentRms = VoiceCaptureService.CalculateRms(silent, 0, silent.Length);
        Assert.Equal(0, silentRms);

        byte[] speechLike = new byte[1600];
        for (int i = 0; i < speechLike.Length / 2; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 16000.0) * 12000);
            speechLike[i * 2] = (byte)(sample & 0xFF);
            speechLike[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        double loudRms = VoiceCaptureService.CalculateRms(speechLike, 0, speechLike.Length);
        Assert.True(loudRms > 5000, $"Expected loud RMS > 5000, got {loudRms}");

        byte[] wav = VoiceCaptureService.BuildWavContainer(speechLike);
        Assert.Equal(44 + speechLike.Length, wav.Length);
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);
        Assert.Equal((byte)'W', wav[8]);
        Assert.Equal((byte)'A', wav[9]);
        Assert.Equal((byte)'V', wav[10]);
        Assert.Equal((byte)'E', wav[11]);
    }

    [Fact]
    public void GeminiApiClient_GlobalProxyUrl_ConfiguresCustomProxyAndNoRevocationCheck()
    {
        string? prev = GeminiApiClient.GlobalProxyUrl;
        try
        {
            GeminiApiClient.GlobalProxyUrl = "http://127.0.0.1:7890";
            using var handler = GeminiApiClient.CreateSocketsHttpHandler((_, _, _, _) => true);
            Assert.True(handler.UseProxy);
            Assert.NotNull(handler.Proxy);
            Assert.Equal(X509RevocationMode.NoCheck, handler.SslOptions.CertificateRevocationCheckMode);

            GeminiApiClient.GlobalProxyUrl = "system";
            using var sysHandler = GeminiApiClient.CreateSocketsHttpHandler((_, _, _, _) => true);
            Assert.True(sysHandler.UseProxy);
            Assert.Equal(HttpClient.DefaultProxy, sysHandler.Proxy);

            GeminiApiClient.GlobalProxyUrl = null;
            using var directHandler = GeminiApiClient.CreateSocketsHttpHandler((_, _, _, _) => true);
            Assert.Equal(X509RevocationMode.NoCheck, directHandler.SslOptions.CertificateRevocationCheckMode);
        }
        finally
        {
            GeminiApiClient.GlobalProxyUrl = prev;
        }
    }

    [Fact]
    public void GeminiApiClient_ParseProxyUri_HandlesHappPortsAndSchemes()
    {
        Assert.Null(GeminiApiClient.ParseProxyUri(null));
        Assert.Null(GeminiApiClient.ParseProxyUri(""));
        Assert.Null(GeminiApiClient.ParseProxyUri("system"));
        Assert.Null(GeminiApiClient.ParseProxyUri("direct"));

        // Happ / v2ray HTTP port
        Uri? happHttp = GeminiApiClient.ParseProxyUri("127.0.0.1:10809");
        Assert.NotNull(happHttp);
        Assert.Equal("http", happHttp.Scheme);
        Assert.Equal("127.0.0.1", happHttp.Host);
        Assert.Equal(10809, happHttp.Port);

        // Happ SOCKS5 port
        Uri? happSocks = GeminiApiClient.ParseProxyUri("socks5://127.0.0.1:10808");
        Assert.NotNull(happSocks);
        Assert.Equal("socks5", happSocks.Scheme);
        Assert.Equal(10808, happSocks.Port);

        // Clash mixed port
        Uri? clash = GeminiApiClient.ParseProxyUri("http://127.0.0.1:7890");
        Assert.NotNull(clash);
        Assert.Equal(7890, clash.Port);
    }

    [Fact]
    public void MarkdownChatRenderer_BuildUserMessage_CreatesWrappingTextBlock()
    {
        Exception? staEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                UIElement elem = MarkdownChatRenderer.BuildUserMessage("Короткий промпт");
                var tb = Assert.IsType<TextBlock>(elem);
                Assert.Equal("Короткий промпт", tb.Text);
                Assert.Equal(TextWrapping.Wrap, tb.TextWrapping);
            }
            catch (Exception ex)
            {
                staEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(staEx);
    }

    [Fact]
    public void MainWindow_FormatForbiddenMessage_DiagnosesLocationBlockAndApiDisabled()
    {
        var geoEx = new GeminiApiException(403, "User location is not supported for the API use.");
        string geoMsg = MainWindow.FormatForbiddenMessage("gemini-2.5-flash", geoEx);
        Assert.Contains("User location is not supported", geoMsg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Сеть и Прокси", geoMsg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Clash", geoMsg, StringComparison.OrdinalIgnoreCase);

        var disabledEx = new GeminiApiException(403, "Generative Language API has not been used in project 12345 before or it is disabled.");
        string disabledMsg = MainWindow.FormatForbiddenMessage("gemini-2.5-flash", disabledEx);
        Assert.Contains("Google Cloud Console", disabledMsg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Generative Language API", disabledMsg, StringComparison.OrdinalIgnoreCase);

        var genericEx = new GeminiApiException(403, "The caller does not have permission");
        string genericMsg = MainWindow.FormatForbiddenMessage("gemini-2.5-flash", genericEx);
        Assert.Contains("отклонён провайдером", genericMsg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainWindow_ProbeModel_AutoDiscoversWorkingGemini25Flash_On403Or503()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"AegisProbeTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        Exception? staEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var handler = new RecordingHandler([
                    (HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"message\":\"User location is not supported for the API use.\"}}"),
                    (HttpStatusCode.OK, "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"pong\"}]}}]}")
                ]);

                using var apiClient = new GeminiApiClient(handler);
                var vault = new MultiModelVault(tempDir);

                var dispatcher = Dispatcher.CurrentDispatcher;
                var win = new MainWindow(vault, apiClient);
                try
                {
                    win.OpenSettingsModal("Models");
                    win.SelectSettingsModelId("gemini-4-argon");
                    win.SettingsApiKeyBox.Password = "AIzaSyWorkingKeyOnFlash12345";

                    var probeTask = win.ProbeModelFromSettingsAsync();
                    var frame = new DispatcherFrame();
                    _ = probeTask.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);

                    Assert.Equal(Visibility.Visible, win.SettingsAddSuccessText.Visibility);
                    Assert.Contains("Ваш API-ключ действителен и полностью работает", win.SettingsAddSuccessText.Text, StringComparison.Ordinal);
                    Assert.Contains("gemini-2.5-flash", win.SettingsAddSuccessText.Text, StringComparison.Ordinal);

                    var selectedInfo = win.GetSelectedSettingsModelInfo();
                    Assert.Equal("gemini-2.5-flash", selectedInfo.ModelId);

                    Assert.Equal(2, handler.RecordedRequests.Count);
                    Assert.Equal("/v1beta/models/gemini-4-argon:generateContent", handler.RecordedRequests[0].Uri.AbsolutePath);
                    Assert.Equal("/v1beta/models/gemini-2.5-flash:generateContent", handler.RecordedRequests[1].Uri.AbsolutePath);
                }
                finally
                {
                    win.Close();
                }
            }
            catch (Exception ex)
            {
                staEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (Directory.Exists(tempDir))
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        Assert.Null(staEx);
    }

    [Fact]
    public void ScreenCaptureService_GetMonitors_ReturnsAtLeastOneValidMonitor()
    {
        var monitors = ScreenCaptureService.GetMonitors();
        Assert.NotEmpty(monitors);

        var primary = monitors.FirstOrDefault(m => m.IsPrimary);
        Assert.NotNull(primary);
        Assert.True(primary.Bounds.Right > primary.Bounds.Left, "Monitor width must be positive");
        Assert.True(primary.Bounds.Bottom > primary.Bounds.Top, "Monitor height must be positive");
        Assert.False(string.IsNullOrWhiteSpace(primary.Name), "Monitor name must not be blank");
    }

    [Fact]
    public void MainWindow_ExportSessionReport_GeneratesMarkdownFileInSandboxReports()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"AegisReportTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        Exception? staEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var handler = new RecordingHandler([]);
                using var apiClient = new GeminiApiClient(handler);
                var vault = new MultiModelVault(tempDir);

                var win = new MainWindow(vault, apiClient);
                try
                {
                    string reportPath = win.ExportSessionReport();
                    Assert.True(File.Exists(reportPath), "Report file must exist on disk");

                    string content = File.ReadAllText(reportPath);
                    Assert.Contains("Аудиторский отчёт сессии Aegis Computer Use", content);
                    Assert.Contains("Журнал диалога и действий", content);
                }
                finally
                {
                    win.Close();
                }
            }
            catch (Exception ex)
            {
                staEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (Directory.Exists(tempDir))
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        Assert.Null(staEx);
    }

    private sealed class FakeScreenCaptureService : IScreenCaptureService
    {
        public ScreenCaptureFrame CaptureDesktopOrWindow(Win32Interop.RECT? targetRect = null, int maxOutputWidth = 1280) =>
            new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png", 0, 0, 1920, 1080);
    }

    private sealed class FakeInputAutomationService : IInputAutomationService
    {
        public List<string> ExecutedCalls { get; } = [];
        public Task<string> ExecuteAsync(string functionName, JsonElement args, int screenWidth, int screenHeight, int originX, int originY, CancellationToken ct)
        {
            ExecutedCalls.Add(functionName);
            return Task.FromResult($"Executed {functionName}");
        }
    }

    private sealed record RecordedHttp(
        Uri Uri,
        string? ApiKeyHeader,
        string? AuthorizationHeader,
        string? AnthropicKeyHeader,
        string? AnthropicVersionHeader,
        string BodyJson);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Json)> _responses;
        public List<RecordedHttp> RecordedRequests { get; } = [];

        public RecordingHandler(IEnumerable<(HttpStatusCode, string)> responses)
        {
            _responses = new Queue<(HttpStatusCode, string)>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            string? key = request.Headers.TryGetValues("x-goog-api-key", out var vals) ? vals.FirstOrDefault() : null;
            string? auth = request.Headers.Authorization?.ToString();
            string? antKey = request.Headers.TryGetValues("x-api-key", out var avals) ? avals.FirstOrDefault() : null;
            string? antVer = request.Headers.TryGetValues("anthropic-version", out var vvals) ? vvals.FirstOrDefault() : null;
            RecordedRequests.Add(new RecordedHttp(request.RequestUri!, key, auth, antKey, antVer, body));

            var (status, json) = _responses.Dequeue();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
