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
using AegisComputerUse.Core.Security;

namespace AegisComputerUse.Core.Gemini;

public sealed class MultiProviderTransport : IDisposable
{
    public const string OpenAiHost = "api.openai.com";
    public const string AnthropicHost = "api.anthropic.com";
    private const int MaxResponseBytes = 16 * 1024 * 1024;

    private static readonly JsonDocumentOptions SafeJsonDocOptions = new()
    {
        MaxDepth = 32,
        AllowTrailingCommas = false
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public MultiProviderTransport(HttpMessageHandler? customHandler = null)
    {
        if (customHandler is not null)
        {
            _http = new HttpClient(customHandler, disposeHandler: false);
            _ownsHttp = false;
        }
        else
        {
            var handler = GeminiApiClient.CreateSocketsHttpHandler(ValidateProviderCertificate);
            _http = new HttpClient(handler, disposeHandler: true);
            _ownsHttp = true;
        }

        _http.Timeout = TimeSpan.FromMinutes(3);
        _http.MaxResponseContentBufferSize = MaxResponseBytes;
    }

    public static void ValidateProviderEndpoint(Uri uri, AiProviderKind provider)
    {
        ArgumentNullException.ThrowIfNull(uri);
        string expectedHost = GetExpectedHost(provider);
        if (!uri.IsAbsoluteUri)
            throw new InvalidOperationException("Endpoint URI must be absolute.");
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only HTTPS endpoints are permitted.");
        if (uri.Port != 443)
            throw new InvalidOperationException("Only standard TLS port 443 is permitted.");
        if (!string.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Untrusted endpoint host '{uri.Host}'. Expected '{expectedHost}'.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
            throw new InvalidOperationException("UserInfo and query parameters are forbidden on provider endpoints.");
    }

    public static string GetExpectedHost(AiProviderKind provider) => provider switch
    {
        AiProviderKind.GoogleGemini => GeminiApiClient.AllowedHost,
        AiProviderKind.OpenAI => OpenAiHost,
        AiProviderKind.Anthropic => AnthropicHost,
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    public static bool ValidateProviderCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors != SslPolicyErrors.None || certificate is null)
            return false;

        using var cert2 = new X509Certificate2(certificate);
        string? targetHost = (sender as SslStream)?.TargetHostName;
        if (!string.IsNullOrWhiteSpace(targetHost))
        {
            return (string.Equals(targetHost, OpenAiHost, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(targetHost, AnthropicHost, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(targetHost, GeminiApiClient.AllowedHost, StringComparison.OrdinalIgnoreCase))
                   && cert2.MatchesHostname(targetHost, allowWildcards: true, allowCommonName: false);
        }

        return cert2.MatchesHostname(OpenAiHost, allowWildcards: true, allowCommonName: false) ||
               cert2.MatchesHostname(AnthropicHost, allowWildcards: true, allowCommonName: false) ||
               cert2.MatchesHostname(GeminiApiClient.AllowedHost, allowWildcards: true, allowCommonName: false);
    }

    public Task<GeminiTurnResponse> SendTurnAsync(
        SealedSecret apiKey,
        IReadOnlyList<JsonNode> unifiedHistory,
        GeminiRequestOptions options,
        string systemInstruction,
        CancellationToken ct)
    {
        return options.Provider switch
        {
            AiProviderKind.OpenAI => SendOpenAiTurnAsync(apiKey, unifiedHistory, options, systemInstruction, ct),
            AiProviderKind.Anthropic => SendAnthropicTurnAsync(apiKey, unifiedHistory, options, systemInstruction, ct),
            _ => throw new InvalidOperationException($"Unsupported provider for MultiProviderTransport: {options.Provider}")
        };
    }

    public async Task<long> ProbeModelAsync(
        AiProviderKind provider,
        SealedSecret apiKey,
        string model,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        string safeModel = GeminiApiClient.ValidateModelId(model);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (provider == AiProviderKind.OpenAI)
        {
            var endpoint = new Uri($"https://{OpenAiHost}/v1/responses", UriKind.Absolute);
            ValidateProviderEndpoint(endpoint, AiProviderKind.OpenAI);
            var payload = new JsonObject
            {
                ["model"] = safeModel,
                ["input"] = "ping",
                ["max_output_tokens"] = 16
            };
            await PostOpenAiJsonAsync(apiKey, endpoint, payload, ct).ConfigureAwait(false);
            return Math.Max(1, sw.ElapsedMilliseconds);
        }

        if (provider == AiProviderKind.Anthropic)
        {
            var endpoint = new Uri($"https://{AnthropicHost}/v1/messages", UriKind.Absolute);
            ValidateProviderEndpoint(endpoint, AiProviderKind.Anthropic);
            var payload = new JsonObject
            {
                ["model"] = safeModel,
                ["max_tokens"] = 16,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "user", ["content"] = "ping" }
                }
            };
            await PostAnthropicJsonAsync(apiKey, endpoint, payload, ct).ConfigureAwait(false);
            return Math.Max(1, sw.ElapsedMilliseconds);
        }

        throw new InvalidOperationException($"Unsupported provider for ProbeModelAsync: {provider}");
    }

    public async Task<IReadOnlyList<ProviderModelCatalogItem>> ListModelsAsync(
        AiProviderKind provider,
        SealedSecret apiKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        return provider switch
        {
            AiProviderKind.OpenAI => await ListOpenAiModelsAsync(apiKey, ct).ConfigureAwait(false),
            AiProviderKind.Anthropic => await ListAnthropicModelsAsync(apiKey, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported provider for MultiProviderTransport.ListModelsAsync: {provider}")
        };
    }

    private async Task<IReadOnlyList<ProviderModelCatalogItem>> ListOpenAiModelsAsync(SealedSecret apiKey, CancellationToken ct)
    {
        var endpoint = new Uri($"https://{OpenAiHost}/v1/models", UriKind.Absolute);
        ValidateProviderEndpoint(endpoint, AiProviderKind.OpenAI);

        using HttpResponseMessage response = await apiKey.UseSecretAsync(async secretBytes =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            AddBearerHeader(req, secretBytes.Span);
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException($"Blocked HTTP redirect ({(int)response.StatusCode}) from OpenAI endpoint.");

        string body = await GeminiApiClient.ReadBoundedStringAsync(response.Content, MaxResponseBytes, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new GeminiApiException((int)response.StatusCode, LogRedactor.Redact(body));

        using var doc = JsonDocument.Parse(body, SafeJsonDocOptions);
        var items = new List<(long Created, ProviderModelCatalogItem Item)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var mEl in dataEl.EnumerateArray())
            {
                string rawId = mEl.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                if (IsExcludedOpenAiUtilityModel(rawId))
                    continue;

                string safeId;
                try { safeId = GeminiApiClient.ValidateModelId(rawId); }
                catch (ArgumentException) { continue; }

                if (!seen.Add(safeId))
                    continue;

                long created = mEl.TryGetProperty("created", out var crEl) && crEl.TryGetInt64(out long ts) ? ts : 0;
                int ctx = safeId.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ||
                          safeId.StartsWith("gpt-4.1", StringComparison.OrdinalIgnoreCase)
                    ? 1_000_000
                    : 200_000;

                items.Add((created, new ProviderModelCatalogItem(
                    AiProviderKind.OpenAI,
                    safeId,
                    safeId,
                    ctx,
                    "Доступна по вашему ключу OpenAI")));
            }
        }

        var sorted = items
            .OrderByDescending(x => x.Item.ModelId.Contains("computer-use", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Created)
            .ThenBy(x => x.Item.ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Item)
            .ToList();

        AiProviderCatalog.RegisterDiscoveredContextLimits(sorted);
        return sorted;
    }

    private async Task<IReadOnlyList<ProviderModelCatalogItem>> ListAnthropicModelsAsync(SealedSecret apiKey, CancellationToken ct)
    {
        var endpoint = new Uri($"https://{AnthropicHost}/v1/models", UriKind.Absolute);
        ValidateProviderEndpoint(endpoint, AiProviderKind.Anthropic);

        using HttpResponseMessage response = await apiKey.UseSecretAsync(async secretBytes =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            AddAnthropicHeaders(req, secretBytes.Span);
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException($"Blocked HTTP redirect ({(int)response.StatusCode}) from Anthropic endpoint.");

        string body = await GeminiApiClient.ReadBoundedStringAsync(response.Content, MaxResponseBytes, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new GeminiApiException((int)response.StatusCode, LogRedactor.Redact(body));

        using var doc = JsonDocument.Parse(body, SafeJsonDocOptions);
        var items = new List<ProviderModelCatalogItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var mEl in dataEl.EnumerateArray())
            {
                string rawId = mEl.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                string safeId;
                try { safeId = GeminiApiClient.ValidateModelId(rawId); }
                catch (ArgumentException) { continue; }

                if (!seen.Add(safeId))
                    continue;

                string displayName = mEl.TryGetProperty("display_name", out var dnEl) && dnEl.GetString() is string dn && !string.IsNullOrWhiteSpace(dn)
                    ? dn.Trim()
                    : safeId;

                items.Add(new ProviderModelCatalogItem(
                    AiProviderKind.Anthropic,
                    safeId,
                    displayName,
                    200_000,
                    "Доступна по вашему ключу Anthropic"));
            }
        }

        AiProviderCatalog.RegisterDiscoveredContextLimits(items);
        return items;
    }

    private static bool IsExcludedOpenAiUtilityModel(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return true;
        return id.Contains("embedding", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("whisper", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("tts", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("dall-e", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("moderation", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("babbage", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("davinci", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("gpt-image", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("sora", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("-transcribe", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("-realtime", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> TranscribeAudioOpenAiAsync(SealedSecret apiKey, byte[] wavAudioBytes, CancellationToken ct)
    {
        if (wavAudioBytes.Length < 45) return string.Empty;

        var endpoint = new Uri($"https://{OpenAiHost}/v1/audio/transcriptions", UriKind.Absolute);
        ValidateProviderEndpoint(endpoint, AiProviderKind.OpenAI);

        using HttpResponseMessage response = await apiKey.UseSecretAsync(async secretBytes =>
        {
            using var form = new MultipartFormDataContent();
            var audioContent = new ByteArrayContent(wavAudioBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audioContent, "file", "voice.wav");
            form.Add(new StringContent("gpt-4o-mini-transcribe"), "model");

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
            AddBearerHeader(req, secretBytes.Span);
            return await _http.SendAsync(req, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException($"Blocked HTTP redirect ({(int)response.StatusCode}) from OpenAI endpoint.");

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new GeminiApiException((int)response.StatusCode, LogRedactor.Redact(body));

        using var doc = JsonDocument.Parse(body, SafeJsonDocOptions);
        return doc.RootElement.TryGetProperty("text", out var tEl) ? (tEl.GetString() ?? string.Empty).Trim() : string.Empty;
    }

    // ==================== OpenAI Responses API (/v1/responses) ====================

    private async Task<GeminiTurnResponse> SendOpenAiTurnAsync(
        SealedSecret apiKey,
        IReadOnlyList<JsonNode> history,
        GeminiRequestOptions options,
        string systemInstruction,
        CancellationToken ct)
    {
        string safeModel = GeminiApiClient.ValidateModelId(options.Model);
        var endpoint = new Uri($"https://{OpenAiHost}/v1/responses", UriKind.Absolute);
        ValidateProviderEndpoint(endpoint, AiProviderKind.OpenAI);

        JsonObject payload = BuildOpenAiPayload(history, safeModel, options, systemInstruction, includeReasoning: true);
        try
        {
            return await PostOpenAiJsonAsync(apiKey, endpoint, payload, ct).ConfigureAwait(false);
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 400)
        {
            // Retry cleanly without reasoning parameter if selected model doesn't support reasoning effort
            JsonObject fallbackPayload = BuildOpenAiPayload(history, safeModel, options, systemInstruction, includeReasoning: false);
            return await PostOpenAiJsonAsync(apiKey, endpoint, fallbackPayload, ct).ConfigureAwait(false);
        }
    }

    private async Task<GeminiTurnResponse> PostOpenAiJsonAsync(SealedSecret apiKey, Uri endpoint, JsonObject payload, CancellationToken ct)
    {
        string json = payload.ToJsonString();
        using HttpResponseMessage response = await apiKey.UseSecretAsync(async secretBytes =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AddBearerHeader(req, secretBytes.Span);
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException($"Blocked HTTP redirect ({(int)response.StatusCode}) from OpenAI endpoint.");

        string body = await GeminiApiClient.ReadBoundedStringAsync(response.Content, MaxResponseBytes, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new GeminiApiException((int)response.StatusCode, LogRedactor.Redact(body));

        return ParseOpenAiResponse(body);
    }

    private static JsonObject BuildOpenAiPayload(
        IReadOnlyList<JsonNode> history,
        string safeModel,
        GeminiRequestOptions options,
        string systemInstruction,
        bool includeReasoning)
    {
        var inputArr = new JsonArray();

        foreach (JsonNode node in history)
        {
            if (node is not JsonObject obj) continue;
            string role = obj["role"]?.GetValue<string>() ?? "user";
            var parts = obj["parts"] as JsonArray;
            if (parts is null) continue;

            if (role == "user")
            {
                var contentArr = new JsonArray();
                foreach (JsonNode? p in parts)
                {
                    if (p is not JsonObject po) continue;
                    if (po["text"]?.GetValue<string>() is string txt)
                    {
                        contentArr.Add(new JsonObject { ["type"] = "input_text", ["text"] = txt });
                    }
                    else if (po["inlineData"] is JsonObject idObj)
                    {
                        string mime = idObj["mimeType"]?.GetValue<string>() ?? "application/octet-stream";
                        string b64 = idObj["data"]?.GetValue<string>() ?? string.Empty;
                        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        {
                            contentArr.Add(new JsonObject
                            {
                                ["type"] = "input_image",
                                ["image_url"] = $"data:{mime};base64,{b64}"
                            });
                        }
                        else if (string.Equals(mime, "application/pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            contentArr.Add(new JsonObject
                            {
                                ["type"] = "input_file",
                                ["filename"] = "attachment.pdf",
                                ["file_data"] = $"data:{mime};base64,{b64}"
                            });
                        }
                        else
                        {
                            contentArr.Add(new JsonObject
                            {
                                ["type"] = "input_text",
                                ["text"] = $"[Binary attachment: {mime} ({b64.Length * 3 / 4} bytes)]"
                            });
                        }
                    }
                }
                if (contentArr.Count > 0)
                {
                    inputArr.Add(new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = contentArr
                    });
                }
            }
            else if (role == "model")
            {
                var outContent = new JsonArray();
                foreach (JsonNode? p in parts)
                {
                    if (p is not JsonObject po) continue;
                    bool isThought = po["thought"]?.GetValue<bool>() ?? false;
                    if (!isThought && po["text"]?.GetValue<string>() is string txt && !string.IsNullOrWhiteSpace(txt))
                    {
                        outContent.Add(new JsonObject { ["type"] = "output_text", ["text"] = txt });
                    }
                    else if (po["functionCall"] is JsonObject fc)
                    {
                        if (outContent.Count > 0)
                        {
                            inputArr.Add(new JsonObject
                            {
                                ["type"] = "message",
                                ["role"] = "assistant",
                                ["content"] = outContent
                            });
                            outContent = new JsonArray();
                        }

                        string callId = fc["id"]?.GetValue<string>() ?? ("call_" + Guid.NewGuid().ToString("N")[..12]);
                        string name = fc["name"]?.GetValue<string>() ?? string.Empty;
                        string argsJson = fc["args"]?.ToJsonString() ?? "{}";
                        inputArr.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = callId,
                            ["name"] = name,
                            ["arguments"] = argsJson
                        });
                    }
                }
                if (outContent.Count > 0)
                {
                    inputArr.Add(new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["content"] = outContent
                    });
                }
            }
            else if (role == "function")
            {
                var pendingImages = new JsonArray();
                foreach (JsonNode? p in parts)
                {
                    if (p is not JsonObject po || po["functionResponse"] is not JsonObject fr) continue;
                    string callId = fr["id"]?.GetValue<string>() ?? "call_0";
                    string outputText = fr["response"]?.ToJsonString() ?? "{}";
                    inputArr.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = callId,
                        ["output"] = outputText
                    });

                    if (fr["parts"] is JsonArray fParts)
                    {
                        foreach (JsonNode? fp in fParts)
                        {
                            if (fp is JsonObject fpo && fpo["inlineData"] is JsonObject idObj)
                            {
                                string mime = idObj["mimeType"]?.GetValue<string>() ?? "image/png";
                                string b64 = idObj["data"]?.GetValue<string>() ?? string.Empty;
                                pendingImages.Add(new JsonObject
                                {
                                    ["type"] = "input_image",
                                    ["image_url"] = $"data:{mime};base64,{b64}"
                                });
                            }
                        }
                    }
                }

                if (pendingImages.Count > 0)
                {
                    var imgMsgContent = new JsonArray
                    {
                        new JsonObject { ["type"] = "input_text", ["text"] = "Updated screen capture after action:" }
                    };
                    foreach (JsonNode? img in pendingImages)
                    {
                        if (img is not null) imgMsgContent.Add(img.DeepClone());
                    }
                    inputArr.Add(new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = imgMsgContent
                    });
                }
            }
        }

        var payload = new JsonObject
        {
            ["model"] = safeModel,
            ["instructions"] = systemInstruction,
            ["input"] = inputArr,
            ["max_output_tokens"] = 16384
        };

        if (includeReasoning)
        {
            string effort = options.ThinkingLevel.ToUpperInvariant() switch
            {
                "LOW" => "low",
                "HIGH" => "high",
                _ => "medium"
            };
            payload["reasoning"] = new JsonObject { ["effort"] = effort };
        }

        var tools = BuildOpenAiTools(options);
        if (tools.Count > 0)
            payload["tools"] = tools;

        return payload;
    }

    private static GeminiTurnResponse ParseOpenAiResponse(string json)
    {
        using var doc = JsonDocument.Parse(json, SafeJsonDocOptions);
        JsonElement root = doc.RootElement;

        var textParts = new List<string>();
        var thoughtParts = new List<string>();
        var calls = new List<GeminiFunctionCall>();
        var modelParts = new JsonArray();

        if (root.TryGetProperty("output", out var outputEl) && outputEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in outputEl.EnumerateArray())
            {
                string type = item.TryGetProperty("type", out var tyEl) ? tyEl.GetString() ?? string.Empty : string.Empty;
                if (type == "reasoning" && item.TryGetProperty("summary", out var sumEl) && sumEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in sumEl.EnumerateArray())
                    {
                        if (s.TryGetProperty("text", out var st) && st.GetString() is string rtxt && !string.IsNullOrWhiteSpace(rtxt))
                        {
                            thoughtParts.Add(rtxt);
                            modelParts.Add(new JsonObject { ["thought"] = true, ["text"] = rtxt });
                        }
                    }
                }
                else if (type == "message" && item.TryGetProperty("content", out var contEl) && contEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in contEl.EnumerateArray())
                    {
                        if (c.TryGetProperty("text", out var tEl) && tEl.GetString() is string msg && !string.IsNullOrWhiteSpace(msg))
                        {
                            textParts.Add(msg);
                            modelParts.Add(new JsonObject { ["text"] = msg });
                        }
                    }
                }
                else if (type == "function_call")
                {
                    string name = item.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? string.Empty : string.Empty;
                    string callId = item.TryGetProperty("call_id", out var cidEl) ? cidEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                    string argsRaw = item.TryGetProperty("arguments", out var aEl) ? aEl.GetString() ?? "{}" : "{}";
                    JsonNode argsNode;
                    try { argsNode = JsonNode.Parse(argsRaw) ?? new JsonObject(); }
                    catch { argsNode = new JsonObject(); }

                    calls.Add(new GeminiFunctionCall(name, argsNode, callId));
                    modelParts.Add(new JsonObject
                    {
                        ["functionCall"] = new JsonObject
                        {
                            ["id"] = callId,
                            ["name"] = name,
                            ["args"] = argsNode.DeepClone()
                        }
                    });
                }
            }
        }

        GeminiUsageMetadata? usage = null;
        if (root.TryGetProperty("usage", out var uEl) && uEl.ValueKind == JsonValueKind.Object)
        {
            int inp = uEl.TryGetProperty("input_tokens", out var p1) && p1.TryGetInt32(out int v1) ? v1 : 0;
            int outp = uEl.TryGetProperty("output_tokens", out var p2) && p2.TryGetInt32(out int v2) ? v2 : 0;
            int tot = uEl.TryGetProperty("total_tokens", out var p3) && p3.TryGetInt32(out int v3) ? v3 : (inp + outp);
            int cached = uEl.TryGetProperty("input_tokens_details", out var idet) && idet.TryGetProperty("cached_tokens", out var pc) && pc.TryGetInt32(out int vc) ? vc : 0;
            int rTok = uEl.TryGetProperty("output_tokens_details", out var odet) && odet.TryGetProperty("reasoning_tokens", out var pr) && pr.TryGetInt32(out int vr) ? vr : 0;
            usage = new GeminiUsageMetadata(inp, outp, tot, cached, rTok);
        }

        var rawModelContent = new JsonObject
        {
            ["role"] = "model",
            ["parts"] = modelParts
        };

        return new GeminiTurnResponse(
            string.Join("\n", textParts),
            calls,
            rawModelContent,
            string.Join("\n", thoughtParts),
            usage);
    }

    // ==================== Anthropic Messages API (/v1/messages) ====================

    private async Task<GeminiTurnResponse> SendAnthropicTurnAsync(
        SealedSecret apiKey,
        IReadOnlyList<JsonNode> history,
        GeminiRequestOptions options,
        string systemInstruction,
        CancellationToken ct)
    {
        string safeModel = GeminiApiClient.ValidateModelId(options.Model);
        var endpoint = new Uri($"https://{AnthropicHost}/v1/messages", UriKind.Absolute);
        ValidateProviderEndpoint(endpoint, AiProviderKind.Anthropic);

        JsonObject payload = BuildAnthropicPayload(history, safeModel, options, systemInstruction, includeThinking: true);
        try
        {
            return await PostAnthropicJsonAsync(apiKey, endpoint, payload, ct).ConfigureAwait(false);
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 400)
        {
            // Fallback without thinking parameter if model rejects extended thinking config
            JsonObject fallback = BuildAnthropicPayload(history, safeModel, options, systemInstruction, includeThinking: false);
            return await PostAnthropicJsonAsync(apiKey, endpoint, fallback, ct).ConfigureAwait(false);
        }
    }

    private async Task<GeminiTurnResponse> PostAnthropicJsonAsync(SealedSecret apiKey, Uri endpoint, JsonObject payload, CancellationToken ct)
    {
        string json = payload.ToJsonString();
        using HttpResponseMessage response = await apiKey.UseSecretAsync(async secretBytes =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AddAnthropicHeaders(req, secretBytes.Span);
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException($"Blocked HTTP redirect ({(int)response.StatusCode}) from Anthropic endpoint.");

        string body = await GeminiApiClient.ReadBoundedStringAsync(response.Content, MaxResponseBytes, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new GeminiApiException((int)response.StatusCode, LogRedactor.Redact(body));

        return ParseAnthropicResponse(body);
    }

    private static JsonObject BuildAnthropicPayload(
        IReadOnlyList<JsonNode> history,
        string safeModel,
        GeminiRequestOptions options,
        string systemInstruction,
        bool includeThinking)
    {
        var messagesArr = new JsonArray();

        foreach (JsonNode node in history)
        {
            if (node is not JsonObject obj) continue;
            string role = obj["role"]?.GetValue<string>() ?? "user";
            var parts = obj["parts"] as JsonArray;
            if (parts is null) continue;

            if (role == "user")
            {
                var contentBlocks = new JsonArray();
                foreach (JsonNode? p in parts)
                {
                    if (p is not JsonObject po) continue;
                    if (po["text"]?.GetValue<string>() is string txt && !string.IsNullOrWhiteSpace(txt))
                    {
                        contentBlocks.Add(new JsonObject { ["type"] = "text", ["text"] = txt });
                    }
                    else if (po["inlineData"] is JsonObject idObj)
                    {
                        string mime = idObj["mimeType"]?.GetValue<string>() ?? "application/octet-stream";
                        string b64 = idObj["data"]?.GetValue<string>() ?? string.Empty;
                        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        {
                            contentBlocks.Add(new JsonObject
                            {
                                ["type"] = "image",
                                ["source"] = new JsonObject
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = mime,
                                    ["data"] = b64
                                }
                            });
                        }
                        else if (string.Equals(mime, "application/pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            contentBlocks.Add(new JsonObject
                            {
                                ["type"] = "document",
                                ["source"] = new JsonObject
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = mime,
                                    ["data"] = b64
                                }
                            });
                        }
                        else
                        {
                            contentBlocks.Add(new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = $"[Binary attachment: {mime} ({b64.Length * 3 / 4} bytes)]"
                            });
                        }
                    }
                }
                if (contentBlocks.Count > 0)
                {
                    messagesArr.Add(new JsonObject { ["role"] = "user", ["content"] = contentBlocks });
                }
            }
            else if (role == "model")
            {
                var contentBlocks = new JsonArray();
                foreach (JsonNode? p in parts)
                {
                    if (p is not JsonObject po) continue;
                    bool isThought = po["thought"]?.GetValue<bool>() ?? false;
                    if (!isThought && po["text"]?.GetValue<string>() is string txt && !string.IsNullOrWhiteSpace(txt))
                    {
                        contentBlocks.Add(new JsonObject { ["type"] = "text", ["text"] = txt });
                    }
                    else if (po["functionCall"] is JsonObject fc)
                    {
                        string callId = fc["id"]?.GetValue<string>() ?? ("toolu_" + Guid.NewGuid().ToString("N")[..12]);
                        string name = fc["name"]?.GetValue<string>() ?? string.Empty;
                        JsonNode inputObj = fc["args"]?.DeepClone() ?? new JsonObject();
                        contentBlocks.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = callId,
                            ["name"] = name,
                            ["input"] = inputObj
                        });
                    }
                }
                if (contentBlocks.Count > 0)
                {
                    messagesArr.Add(new JsonObject { ["role"] = "assistant", ["content"] = contentBlocks });
                }
            }
            else if (role == "function")
            {
                var toolResults = new JsonArray();
                foreach (JsonNode? p in parts)
                {
                    if (p is not JsonObject po || po["functionResponse"] is not JsonObject fr) continue;
                    string callId = fr["id"]?.GetValue<string>() ?? "toolu_0";
                    string outText = fr["response"]?.ToJsonString() ?? "{}";

                    var nested = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = outText }
                    };

                    if (fr["parts"] is JsonArray fParts)
                    {
                        foreach (JsonNode? fp in fParts)
                        {
                            if (fp is JsonObject fpo && fpo["inlineData"] is JsonObject idObj)
                            {
                                string mime = idObj["mimeType"]?.GetValue<string>() ?? "image/png";
                                string b64 = idObj["data"]?.GetValue<string>() ?? string.Empty;
                                nested.Add(new JsonObject
                                {
                                    ["type"] = "image",
                                    ["source"] = new JsonObject
                                    {
                                        ["type"] = "base64",
                                        ["media_type"] = mime,
                                        ["data"] = b64
                                    }
                                });
                            }
                        }
                    }

                    toolResults.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = callId,
                        ["content"] = nested
                    });
                }

                if (toolResults.Count > 0)
                {
                    messagesArr.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
                }
            }
        }

        int budget = options.ThinkingLevel.ToUpperInvariant() switch
        {
            "LOW" => 2048,
            "HIGH" => 16384,
            _ => 8192
        };

        var payload = new JsonObject
        {
            ["model"] = safeModel,
            ["system"] = systemInstruction,
            ["messages"] = messagesArr,
            ["max_tokens"] = includeThinking ? 32768 : 16384
        };

        if (includeThinking)
        {
            payload["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = budget
            };
        }

        var tools = BuildAnthropicTools(options);
        if (tools.Count > 0)
            payload["tools"] = tools;

        return payload;
    }

    private static GeminiTurnResponse ParseAnthropicResponse(string json)
    {
        using var doc = JsonDocument.Parse(json, SafeJsonDocOptions);
        JsonElement root = doc.RootElement;

        var textParts = new List<string>();
        var thoughtParts = new List<string>();
        var calls = new List<GeminiFunctionCall>();
        var modelParts = new JsonArray();

        if (root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in contentEl.EnumerateArray())
            {
                string type = block.TryGetProperty("type", out var tyEl) ? tyEl.GetString() ?? string.Empty : string.Empty;
                if (type == "thinking" && block.TryGetProperty("thinking", out var thEl) && thEl.GetString() is string th && !string.IsNullOrWhiteSpace(th))
                {
                    thoughtParts.Add(th);
                    modelParts.Add(new JsonObject { ["thought"] = true, ["text"] = th });
                }
                else if (type == "text" && block.TryGetProperty("text", out var tEl) && tEl.GetString() is string txt && !string.IsNullOrWhiteSpace(txt))
                {
                    textParts.Add(txt);
                    modelParts.Add(new JsonObject { ["text"] = txt });
                }
                else if (type == "tool_use")
                {
                    string id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                    string name = block.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? string.Empty : string.Empty;
                    JsonNode inputNode = block.TryGetProperty("input", out var inEl)
                        ? JsonNode.Parse(inEl.GetRawText()) ?? new JsonObject()
                        : new JsonObject();

                    calls.Add(new GeminiFunctionCall(name, inputNode, id));
                    modelParts.Add(new JsonObject
                    {
                        ["functionCall"] = new JsonObject
                        {
                            ["id"] = id,
                            ["name"] = name,
                            ["args"] = inputNode.DeepClone()
                        }
                    });
                }
            }
        }

        GeminiUsageMetadata? usage = null;
        if (root.TryGetProperty("usage", out var uEl) && uEl.ValueKind == JsonValueKind.Object)
        {
            int inp = uEl.TryGetProperty("input_tokens", out var p1) && p1.TryGetInt32(out int v1) ? v1 : 0;
            int outp = uEl.TryGetProperty("output_tokens", out var p2) && p2.TryGetInt32(out int v2) ? v2 : 0;
            int cached = uEl.TryGetProperty("cache_read_input_tokens", out var p3) && p3.TryGetInt32(out int v3) ? v3 : 0;
            usage = new GeminiUsageMetadata(inp, outp, inp + outp, cached, 0);
        }

        var rawModelContent = new JsonObject
        {
            ["role"] = "model",
            ["parts"] = modelParts
        };

        return new GeminiTurnResponse(
            string.Join("\n", textParts),
            calls,
            rawModelContent,
            string.Join("\n", thoughtParts),
            usage);
    }

    // ==================== Lowercase JSON-Schema Tools ====================

    private static JsonArray BuildOpenAiTools(GeminiRequestOptions options)
    {
        var arr = new JsonArray();
        foreach (var (name, desc, schema) in EnumerateToolDefinitions(options))
        {
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = name,
                ["description"] = desc,
                ["parameters"] = schema
            });
        }
        return arr;
    }

    private static JsonArray BuildAnthropicTools(GeminiRequestOptions options)
    {
        var arr = new JsonArray();
        foreach (var (name, desc, schema) in EnumerateToolDefinitions(options))
        {
            arr.Add(new JsonObject
            {
                ["name"] = name,
                ["description"] = desc,
                ["input_schema"] = schema
            });
        }
        return arr;
    }

    private static IEnumerable<(string Name, string Description, JsonObject Schema)> EnumerateToolDefinitions(GeminiRequestOptions options)
    {
        if (options.EnableFileCreation)
        {
            yield return (
                "create_file",
                "Create a file safely inside the user's sandboxed workspace (.txt, .md, .json, .xml, .yaml, .csv, .html, .css, .js, .ts, .cs, .py, .sql, .svg, .rtf, .docx, .xlsx, .pdf, .png, .bmp).",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["relative_path"] = new JsonObject { ["type"] = "string" },
                        ["content"] = new JsonObject { ["type"] = "string" },
                        ["encoding"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray { "relative_path", "content" }
                });
        }

        if (!options.EnableComputerUse)
            yield break;

        yield return ("click_at", "Left click at normalized coordinates (0..1000).", PointSchema());
        yield return ("double_click_at", "Double click at normalized coordinates (0..1000).", PointSchema());
        yield return ("right_click_at", "Right click at normalized coordinates (0..1000).", PointSchema());
        yield return ("hover_at", "Move mouse cursor to normalized coordinates (0..1000).", PointSchema());
        yield return (
            "type_text_at",
            "Click at (x, y) and type text.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["x"] = new JsonObject { ["type"] = "integer" },
                    ["y"] = new JsonObject { ["type"] = "integer" },
                    ["text"] = new JsonObject { ["type"] = "string" },
                    ["clear_before_typing"] = new JsonObject { ["type"] = "boolean" },
                    ["press_enter"] = new JsonObject { ["type"] = "boolean" }
                },
                ["required"] = new JsonArray { "x", "y", "text" }
            });
        yield return (
            "key_combination",
            "Press key combination such as 'ctrl+c', 'enter', 'alt+tab'.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["keys"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray { "keys" }
            });
        yield return (
            "scroll_at",
            "Scroll at (x, y) in direction up/down/left/right.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["x"] = new JsonObject { ["type"] = "integer" },
                    ["y"] = new JsonObject { ["type"] = "integer" },
                    ["direction"] = new JsonObject { ["type"] = "string" },
                    ["magnitude"] = new JsonObject { ["type"] = "integer" }
                },
                ["required"] = new JsonArray { "x", "y", "direction" }
            });
        yield return (
            "drag_and_drop",
            "Drag mouse from (x, y) to (destination_x, destination_y).",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["x"] = new JsonObject { ["type"] = "integer" },
                    ["y"] = new JsonObject { ["type"] = "integer" },
                    ["destination_x"] = new JsonObject { ["type"] = "integer" },
                    ["destination_y"] = new JsonObject { ["type"] = "integer" }
                },
                ["required"] = new JsonArray { "x", "y", "destination_x", "destination_y" }
            });
        yield return (
            "wait_5_seconds",
            "Wait 5 seconds for UI/page loading.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });
    }

    private static JsonObject PointSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["x"] = new JsonObject { ["type"] = "integer" },
            ["y"] = new JsonObject { ["type"] = "integer" }
        },
        ["required"] = new JsonArray { "x", "y" }
    };

    private static void AddBearerHeader(HttpRequestMessage req, ReadOnlySpan<byte> utf8Secret)
    {
        string key = Encoding.UTF8.GetString(utf8Secret);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private static void AddAnthropicHeaders(HttpRequestMessage req, ReadOnlySpan<byte> utf8Secret)
    {
        string key = Encoding.UTF8.GetString(utf8Secret);
        req.Headers.TryAddWithoutValidation("x-api-key", key);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
