using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AegisComputerUse.Core.Security;

namespace AegisComputerUse.Core.Gemini;

public sealed partial class GeminiApiClient : IDisposable
{
    public const string AllowedHost = "generativelanguage.googleapis.com";
    private const long InlineAttachmentLimitBytes = 15 * 1024 * 1024; // 15 MB
    private const long MaxUploadFileBytes = 250 * 1024 * 1024; // 250 MB safety cap
    private const int MaxResponseBytes = 16 * 1024 * 1024;

    private static readonly JsonDocumentOptions SafeJsonDocOptions = new()
    {
        MaxDepth = 32,
        AllowTrailingCommas = false
    };

    [GeneratedRegex(@"^[a-zA-Z0-9._\-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeModelIdRegex();

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public static string? GlobalProxyUrl { get; set; }
    internal TimeSpan TransientRetryDelay { get; set; } = TimeSpan.FromMilliseconds(900);

    public static Uri? ParseProxyUri(string? rawProxy)
    {
        if (string.IsNullOrWhiteSpace(rawProxy)) return null;
        string trimmed = rawProxy.Trim().Trim('"', '\'', '<', '>', '`');
        if (string.Equals(trimmed, "system", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "direct", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed))
        {
            string scheme = parsed.Scheme.ToLowerInvariant();
            if (scheme is "http" or "https" or "socks4" or "socks4a" or "socks5")
            {
                return parsed;
            }
        }

        return null;
    }

    public static async Task<string?> DetectActiveLocalProxyAsync(CancellationToken ct = default)
    {
        // Prioritize true forward-proxy tunnel ports (Happ, Xray, Clash, Antigravity Bridge)
        // Port 8585 is an HTTP relay endpoint, while 8586 and 10809/10808 provide transparent HTTPS tunneling.
        int[] candidatePorts = [10809, 10808, 7890, 8586, 2081, 2080, 1080];
        foreach (int port in candidatePorts)
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(200));
                await tcp.ConnectAsync(IPAddress.Loopback, port, timeoutCts.Token).ConfigureAwait(false);
                if (tcp.Connected)
                {
                    string scheme = port is 10808 or 2080 or 1080 ? "socks5" : "http";
                    return $"{scheme}://127.0.0.1:{port}";
                }
            }
            catch
            {
                // Port inactive or connection refused
            }
        }

        return null;
    }

    public static SocketsHttpHandler CreateSocketsHttpHandler(RemoteCertificateValidationCallback certValidator)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = certValidator
            }
        };

        Uri? proxyUri = ParseProxyUri(GlobalProxyUrl);
        if (proxyUri is not null)
        {
            handler.Proxy = new WebProxy(proxyUri);
            handler.UseProxy = true;
        }
        else
        {
            handler.Proxy = HttpClient.DefaultProxy;
            handler.UseProxy = true;
        }

        return handler;
    }

    public GeminiApiClient(HttpMessageHandler? customHandler = null)
    {
        if (customHandler is not null)
        {
            _http = new HttpClient(customHandler, disposeHandler: false);
            _ownsHttp = false;
        }
        else
        {
            var handler = CreateSocketsHttpHandler(ValidateGoogleCertificate);
            _http = new HttpClient(handler, disposeHandler: true);
            _ownsHttp = true;
        }

        _http.Timeout = TimeSpan.FromMinutes(3);
        _http.MaxResponseContentBufferSize = MaxResponseBytes;
    }

    public static void ValidateEndpointUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
            throw new InvalidOperationException("Endpoint URI must be absolute.");
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only HTTPS endpoints are permitted.");
        if (uri.Port != 443)
            throw new InvalidOperationException("Only standard TLS port 443 is permitted.");
        if (!string.Equals(uri.Host, AllowedHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Untrusted endpoint host '{uri.Host}'. Only {AllowedHost} is allowed.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("UserInfo in endpoint URI is forbidden.");
        if (!string.IsNullOrEmpty(uri.Query))
            throw new InvalidOperationException("Query strings in endpoint URI are forbidden to prevent secret leakage.");
    }

    public static string ValidateModelId(string model)
    {
        string trimmed = model?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Contains("..", StringComparison.Ordinal) || !SafeModelIdRegex().IsMatch(trimmed))
            throw new ArgumentException("Invalid model identifier.", nameof(model));
        return trimmed;
    }

    public async Task<IReadOnlyList<ProviderModelCatalogItem>> ListModelsAsync(
        SealedSecret apiKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        var endpoint = new Uri($"https://{AllowedHost}/v1beta/models", UriKind.Absolute);
        ValidateEndpointUri(endpoint);

        async Task<HttpResponseMessage> SendOnceAsync()
        {
            return await apiKey.UseSecretAsync(async secretBytes =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                AddApiKeyHeader(req, secretBytes.Span);
                return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        HttpResponseMessage? response = null;
        const int maxListRetries = 2;
        for (int attempt = 0; attempt <= maxListRetries; attempt++)
        {
            response?.Dispose();
            response = await SendOnceAsync().ConfigureAwait(false);
            if ((int)response.StatusCode is 500 or 502 or 503 or 504 or 529 && attempt < maxListRetries)
            {
                await Task.Delay(TransientRetryDelay * (attempt + 1), ct).ConfigureAwait(false);
                continue;
            }
            break;
        }

        using HttpResponseMessage finalResponse = response ?? throw new InvalidOperationException("No response received from models endpoint.");

        if ((int)finalResponse.StatusCode is >= 300 and < 400)
            throw new HttpRequestException($"Blocked HTTP redirect ({(int)finalResponse.StatusCode}) from API endpoint.");

        string responseBody = await ReadBoundedStringAsync(finalResponse.Content, MaxResponseBytes, ct).ConfigureAwait(false);
        if (!finalResponse.IsSuccessStatusCode)
        {
            throw new GeminiApiException((int)finalResponse.StatusCode, LogRedactor.Redact(responseBody));
        }

        using var doc = JsonDocument.Parse(responseBody, SafeJsonDocOptions);
        var result = new List<ProviderModelCatalogItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (doc.RootElement.TryGetProperty("models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var mEl in modelsEl.EnumerateArray())
            {
                if (!SupportsGenerateContent(mEl))
                    continue;

                string rawName = mEl.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? string.Empty : string.Empty;
                if (rawName.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                    rawName = rawName["models/".Length..];

                string safeId;
                try { safeId = ValidateModelId(rawName); }
                catch (ArgumentException) { continue; }

                if (!seen.Add(safeId))
                    continue;

                string displayName = mEl.TryGetProperty("displayName", out var dEl) && dEl.GetString() is string dn && !string.IsNullOrWhiteSpace(dn)
                    ? dn.Trim()
                    : safeId;

                int ctxLimit = mEl.TryGetProperty("inputTokenLimit", out var cEl) && cEl.TryGetInt32(out int tok) && tok > 0
                    ? tok
                    : 1_048_576;

                string desc = ctxLimit >= 1_000_000
                    ? $"{ctxLimit / 1024}K контекст"
                    : $"{ctxLimit:N0} tok";

                result.Add(new ProviderModelCatalogItem(
                    AiProviderKind.GoogleGemini,
                    safeId,
                    displayName,
                    ctxLimit,
                    desc));
            }
        }

        var sorted = result
            .OrderByDescending(m => m.ModelId.Contains("computer-use", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => m.ModelId.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AiProviderCatalog.RegisterDiscoveredContextLimits(sorted);
        return sorted;
    }

    private static bool SupportsGenerateContent(JsonElement modelEl)
    {
        if (!modelEl.TryGetProperty("supportedGenerationMethods", out var methodsEl) ||
            methodsEl.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in methodsEl.EnumerateArray())
        {
            if (string.Equals(item.GetString(), "generateContent", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public async Task<long> ProbeModelAsync(
        SealedSecret apiKey,
        string model,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        string safeModel = ValidateModelId(model);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var payload = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = "ping" } }
                }
            },
            ["generationConfig"] = new JsonObject
            {
                ["maxOutputTokens"] = 8,
                ["temperature"] = 0.0
            }
        };

        const int maxRetries = 2;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await GenerateContentAsync(apiKey, safeModel, payload, ct).ConfigureAwait(false);
                return Math.Max(1, sw.ElapsedMilliseconds);
            }
            catch (GeminiApiException ex) when (ex.StatusCode is 500 or 502 or 503 or 504 or 529 && attempt < maxRetries)
            {
                await Task.Delay(TransientRetryDelay * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<string> TranscribeAudioAsync(
        SealedSecret apiKey,
        string model,
        byte[] wavAudioBytes,
        CancellationToken ct)
    {
        if (wavAudioBytes.Length < 45)
            return string.Empty;

        string safeModel = ValidateModelId(model);
        JsonObject BuildPayload(bool includeThinking)
        {
            var genCfg = new JsonObject
            {
                ["temperature"] = 0.0,
                ["maxOutputTokens"] = 256
            };
            if (includeThinking)
            {
                genCfg["thinkingConfig"] = safeModel.Contains("flash", StringComparison.OrdinalIgnoreCase) &&
                                           ComputerUseAgent.IsGemini25Model(safeModel)
                    ? new JsonObject { ["thinkingBudget"] = 0 }
                    : ComputerUseAgent.BuildThinkingConfig(safeModel, "LOW");
            }

            return new JsonObject
            {
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["text"] = "Transcribe the spoken voice command verbatim in the original language (Russian, English, etc.). Return ONLY the transcribed text without quotes or explanations. If silent/unintelligible, return empty text."
                            },
                            new JsonObject
                            {
                                ["inlineData"] = new JsonObject
                                {
                                    ["mimeType"] = "audio/wav",
                                    ["data"] = Convert.ToBase64String(wavAudioBytes)
                                }
                            }
                        }
                    }
                },
                ["generationConfig"] = genCfg
            };
        }

        async Task<string> RunOnceAsync()
        {
            try
            {
                var resp = await GenerateContentAsync(apiKey, safeModel, BuildPayload(includeThinking: true), ct).ConfigureAwait(false);
                return resp.Text.Trim();
            }
            catch (GeminiApiException ex) when (ex.StatusCode == 400)
            {
                var resp = await GenerateContentAsync(apiKey, safeModel, BuildPayload(includeThinking: false), ct).ConfigureAwait(false);
                return resp.Text.Trim();
            }
        }

        try
        {
            return await RunOnceAsync().ConfigureAwait(false);
        }
        catch (GeminiApiException ex) when (ex.StatusCode is 500 or 502 or 503 or 504 or 529)
        {
            await Task.Delay(TransientRetryDelay, ct).ConfigureAwait(false);
            return await RunOnceAsync().ConfigureAwait(false);
        }
    }

    public async Task<JsonObject> BuildUserPartForAttachmentAsync(
        SealedSecret apiKey,
        AttachmentInput attachment,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (attachment.DataBytes.LongLength > MaxUploadFileBytes)
            throw new InvalidOperationException($"Attachment '{attachment.FileName}' exceeds 250 MB limit.");

        if (attachment.IsTextDocument && attachment.DataBytes.Length <= 512 * 1024)
        {
            string text = Encoding.UTF8.GetString(attachment.DataBytes);
            return new JsonObject
            {
                ["text"] = $"[Attached File: {attachment.FileName}]\n```text\n{text}\n```"
            };
        }

        if (attachment.DataBytes.LongLength <= InlineAttachmentLimitBytes)
        {
            return new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = attachment.MimeType,
                    ["data"] = Convert.ToBase64String(attachment.DataBytes)
                }
            };
        }

        string fileUri = await UploadLargeFileResumableAsync(apiKey, attachment, ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["fileData"] = new JsonObject
            {
                ["mimeType"] = attachment.MimeType,
                ["fileUri"] = fileUri
            }
        };
    }

    public async Task<GeminiTurnResponse> GenerateContentAsync(
        SealedSecret apiKey,
        string model,
        JsonObject requestPayload,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(requestPayload);

        string safeModel = ValidateModelId(model);
        var endpoint = new Uri($"https://{AllowedHost}/v1beta/models/{safeModel}:generateContent", UriKind.Absolute);
        ValidateEndpointUri(endpoint);

        string jsonBody = requestPayload.ToJsonString();

        using HttpResponseMessage response = await apiKey.UseSecretAsync(async secretBytes =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            AddApiKeyHeader(req, secretBytes.Span);
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException($"Blocked HTTP redirect ({(int)response.StatusCode}) from API endpoint.");

        string responseBody = await ReadBoundedStringAsync(response.Content, MaxResponseBytes, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string safeErr = LogRedactor.Redact(responseBody);
            throw new GeminiApiException((int)response.StatusCode, safeErr);
        }

        return ParseTurnResponse(responseBody);
    }

    internal static async Task<string> ReadBoundedStringAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > 0 and long declared && declared > maxBytes)
            throw new InvalidOperationException($"API response exceeds {maxBytes / (1024 * 1024)} MB safety cap.");

        await using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        long total = 0;

        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException($"API response exceeded {maxBytes / (1024 * 1024)} MB safety cap.");
            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private async Task<string> UploadLargeFileResumableAsync(
        SealedSecret apiKey,
        AttachmentInput attachment,
        CancellationToken ct)
    {
        var startUri = new Uri($"https://{AllowedHost}/upload/v1beta/files", UriKind.Absolute);
        ValidateEndpointUri(startUri);

        string metaJson = new JsonObject
        {
            ["file"] = new JsonObject { ["displayName"] = attachment.FileName }
        }.ToJsonString();

        using HttpResponseMessage startResp = await apiKey.UseSecretAsync(async secretBytes =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, startUri);
            req.Headers.TryAddWithoutValidation("X-Goog-Upload-Protocol", "resumable");
            req.Headers.TryAddWithoutValidation("X-Goog-Upload-Command", "start");
            req.Headers.TryAddWithoutValidation("X-Goog-Upload-Header-Content-Length", attachment.DataBytes.LongLength.ToString());
            req.Headers.TryAddWithoutValidation("X-Goog-Upload-Header-Content-Type", attachment.MimeType);
            req.Content = new StringContent(metaJson, Encoding.UTF8, "application/json");
            AddApiKeyHeader(req, secretBytes.Span);
            return await _http.SendAsync(req, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (!startResp.IsSuccessStatusCode)
            throw new GeminiApiException((int)startResp.StatusCode, LogRedactor.Redact(await startResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)));

        if (!startResp.Headers.TryGetValues("X-Goog-Upload-URL", out var urls))
            throw new InvalidOperationException("Resumable upload URL missing in Gemini Files API response.");

        string rawUploadUrl = urls.FirstOrDefault() ?? string.Empty;
        // Upload URL contains upload_id query param on same trusted host; validate host & scheme strictly!
        if (!Uri.TryCreate(rawUploadUrl, UriKind.Absolute, out Uri? uploadUri) ||
            uploadUri.Scheme != Uri.UriSchemeHttps ||
            uploadUri.Port != 443 ||
            !string.Equals(uploadUri.Host, AllowedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Rejected untrusted upload URL returned by Files API.");
        }

        using HttpResponseMessage uploadResp = await apiKey.UseSecretAsync(async secretBytes =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, uploadUri);
            req.Headers.TryAddWithoutValidation("X-Goog-Upload-Offset", "0");
            req.Headers.TryAddWithoutValidation("X-Goog-Upload-Command", "upload, finalize");
            req.Content = new ByteArrayContent(attachment.DataBytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue(attachment.MimeType);
            AddApiKeyHeader(req, secretBytes.Span);
            return await _http.SendAsync(req, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        string uploadJson = await uploadResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!uploadResp.IsSuccessStatusCode)
            throw new GeminiApiException((int)uploadResp.StatusCode, LogRedactor.Redact(uploadJson));

        using var doc = JsonDocument.Parse(uploadJson, SafeJsonDocOptions);
        if (doc.RootElement.TryGetProperty("file", out var fEl) &&
            fEl.TryGetProperty("uri", out var uriEl) &&
            uriEl.GetString() is string fileUri &&
            Uri.TryCreate(fileUri, UriKind.Absolute, out Uri? parsedFileUri) &&
            parsedFileUri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(parsedFileUri.Host, AllowedHost, StringComparison.OrdinalIgnoreCase))
        {
            return fileUri;
        }

        throw new InvalidOperationException("Invalid file URI returned by Gemini Files API.");
    }

    private static void AddApiKeyHeader(HttpRequestMessage request, ReadOnlySpan<byte> utf8Secret)
    {
        string headerKey = Encoding.UTF8.GetString(utf8Secret);
        request.Headers.TryAddWithoutValidation("x-goog-api-key", headerKey);
    }

    private static GeminiTurnResponse ParseTurnResponse(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson, SafeJsonDocOptions);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Gemini API returned no candidates.");
        }

        JsonElement candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var contentEl))
        {
            string finish = candidate.TryGetProperty("finishReason", out var fr) ? fr.GetString() ?? "UNKNOWN" : "UNKNOWN";
            throw new InvalidOperationException($"Gemini candidate has no content (finishReason={finish}).");
        }

        JsonNode rawModelContent = JsonNode.Parse(contentEl.GetRawText(), new JsonNodeOptions { PropertyNameCaseInsensitive = false })
            ?? new JsonObject { ["role"] = "model", ["parts"] = new JsonArray() };

        var textParts = new List<string>();
        var thoughtParts = new List<string>();
        var calls = new List<GeminiFunctionCall>();

        if (contentEl.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                {
                    bool isThought = part.TryGetProperty("thought", out var thEl) && thEl.ValueKind == JsonValueKind.True;
                    string txt = textEl.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        if (isThought) thoughtParts.Add(txt);
                        else textParts.Add(txt);
                    }
                }

                if (part.TryGetProperty("functionCall", out var fcEl) && fcEl.ValueKind == JsonValueKind.Object)
                {
                    string name = fcEl.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? string.Empty : string.Empty;
                    JsonNode argsNode = fcEl.TryGetProperty("args", out var aEl)
                        ? JsonNode.Parse(aEl.GetRawText()) ?? new JsonObject()
                        : new JsonObject();
                    calls.Add(new GeminiFunctionCall(name, argsNode));
                }
            }
        }

        GeminiUsageMetadata? usage = null;
        if (root.TryGetProperty("usageMetadata", out var uEl) && uEl.ValueKind == JsonValueKind.Object)
        {
            int promptTok = uEl.TryGetProperty("promptTokenCount", out var p1) && p1.TryGetInt32(out int pv) ? pv : 0;
            int candTok = uEl.TryGetProperty("candidatesTokenCount", out var p2) && p2.TryGetInt32(out int cv) ? cv : 0;
            int totalTok = uEl.TryGetProperty("totalTokenCount", out var p3) && p3.TryGetInt32(out int tv) ? tv : (promptTok + candTok);
            int cachedTok = uEl.TryGetProperty("cachedContentTokenCount", out var p4) && p4.TryGetInt32(out int chv) ? chv : 0;
            int thoughtsTok = uEl.TryGetProperty("thoughtsTokenCount", out var p5) && p5.TryGetInt32(out int thv) ? thv : 0;
            usage = new GeminiUsageMetadata(promptTok, candTok, totalTok, cachedTok, thoughtsTok);
        }

        return new GeminiTurnResponse(
            string.Join("\n", textParts),
            calls,
            rawModelContent,
            string.Join("\n", thoughtParts),
            usage);
    }

    public static bool ValidateGoogleCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors != SslPolicyErrors.None || certificate is null)
            return false;

        using var cert2 = new X509Certificate2(certificate);
        return cert2.MatchesHostname(AllowedHost, allowWildcards: true, allowCommonName: false);
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}

public sealed record GeminiRateLimitInfo(
    bool IsZeroLimit,
    bool IsFreeTier,
    TimeSpan? RetryDelay,
    string ShortMessage,
    bool IsDailyLimit = false)
{
    public string? FormatRetryDelayRussian()
    {
        if (RetryDelay is null || RetryDelay.Value <= TimeSpan.Zero)
            return null;

        TimeSpan d = RetryDelay.Value;
        if (d.TotalHours >= 1)
            return $"{(int)d.TotalHours} ч {d.Minutes} мин";
        if (d.TotalMinutes >= 1)
            return $"{(int)d.TotalMinutes} мин {d.Seconds} сек";
        return $"{Math.Ceiling(d.TotalSeconds)} сек";
    }
}

public sealed partial class GeminiApiException : Exception
{
    private const string HttpPrefix = "Gemini API HTTP ";

    [GeneratedRegex("\"retryDelay\"\\s*:\\s*\"([0-9]+(?:\\.[0-9]+)?)s\"", RegexOptions.CultureInvariant, 250)]
    private static partial Regex RetryDelayJsonRegex();

    public int StatusCode { get; }

    public GeminiApiException(int statusCode, string safeMessage)
        : base($"{HttpPrefix}{statusCode}: {safeMessage}")
    {
        StatusCode = statusCode;
    }

    public string ExtractShortMessage()
    {
        string raw = Message;
        int colonIdx = raw.IndexOf(':', HttpPrefix.Length);
        string body = colonIdx >= 0 ? raw[(colonIdx + 1)..].Trim() : raw;

        try
        {
            int jsonStart = body.IndexOf('{');
            if (jsonStart >= 0)
            {
                using var doc = JsonDocument.Parse(body[jsonStart..], new JsonDocumentOptions { MaxDepth = 32 });
                if (doc.RootElement.TryGetProperty("error", out var errEl) &&
                    errEl.TryGetProperty("message", out var msgEl) &&
                    msgEl.GetString() is string fullMsg &&
                    !string.IsNullOrWhiteSpace(fullMsg))
                {
                    string firstLine = fullMsg.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? fullMsg;
                    return firstLine.Trim();
                }
            }
        }
        catch
        {
            // Keep graceful fallback below
        }

        string fallback = body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? raw;
        return fallback.Length > 220 ? fallback[..217] + "..." : fallback;
    }

    public GeminiRateLimitInfo ParseRateLimitInfo()
    {
        string raw = Message;
        int colonIdx = raw.IndexOf(':', HttpPrefix.Length);
        string body = colonIdx >= 0 ? raw[(colonIdx + 1)..].Trim() : raw;

        bool isZero = body.Contains("limit: 0", StringComparison.OrdinalIgnoreCase);
        bool isFree = body.Contains("FreeTier", StringComparison.OrdinalIgnoreCase) ||
                      body.Contains("Free Tier", StringComparison.OrdinalIgnoreCase) ||
                      body.Contains("free_tier", StringComparison.OrdinalIgnoreCase);
        bool isDaily = body.Contains("per day", StringComparison.OrdinalIgnoreCase) ||
                       body.Contains("daily", StringComparison.OrdinalIgnoreCase) ||
                       body.Contains("requests per day limit exceeded", StringComparison.OrdinalIgnoreCase);
        TimeSpan? retryDelay = null;

        var m = RetryDelayJsonRegex().Match(body);
        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double secs))
        {
            retryDelay = TimeSpan.FromSeconds(Math.Clamp(secs, 0, 86400 * 7));
        }

        string shortMsg = ExtractShortMessage();
        return new GeminiRateLimitInfo(isZero, isFree, retryDelay, shortMsg, isDaily);
    }
}
