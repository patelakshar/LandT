using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PDFtoImage;
using SkiaSharp;

namespace PdfSolverMvc.Services;

public sealed class GeminiService : IGeminiService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _fallbackModel;
    private readonly bool _enabled;
    private readonly int _renderDpi;
    private readonly int _analyzerDpi;
    private readonly int _maxOutputTokens;
    private readonly string _layoutCachePath;
    private readonly ConcurrentDictionary<string, PageLayoutPlan> _layoutCache = new(StringComparer.Ordinal);
    private readonly ILogger<GeminiService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public GeminiService(
        HttpClient httpClient,
        IConfiguration configuration,
        IWebHostEnvironment env,
        ILogger<GeminiService> logger)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(60);

        _apiKey = (configuration["Gemini:ApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _apiKey = TryReadKeyFromDevSettings(env.ContentRootPath);
        }
        _model = (configuration["Gemini:Model"] ?? "gemini-2.5-flash").Trim();
        _fallbackModel = (configuration["Gemini:FallbackModel"] ?? (_model.Equals("gemini-2.5-flash", StringComparison.OrdinalIgnoreCase) ? "gemini-3.5-flash-lite" : "gemini-2.5-flash")).Trim();
        _enabled = configuration.GetValue<bool?>("Gemini:Enabled") ?? true;
        _renderDpi = Math.Clamp(configuration.GetValue<int?>("Gemini:RenderDpi") ?? 200, 120, 300);
        _analyzerDpi = Math.Clamp(configuration.GetValue<int?>("Gemini:AnalyzerDpi") ?? 110, 80, 160);
        _maxOutputTokens = Math.Clamp(configuration.GetValue<int?>("Gemini:MaxOutputTokens") ?? 2048, 512, 8192);
        _layoutCachePath = Path.Combine(env.ContentRootPath, "UploadedPdfs", "layout_patterns.json");
        _logger = logger;

        LoadLayoutCache();
    }

    public bool IsConfigured => _enabled && !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<PageLayoutPlan> AnalyzePageLayoutAsync(
        string pdfFilePath,
        int zeroBasedPageIndex,
        string layoutFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (_layoutCache.TryGetValue(layoutFingerprint, out var cached))
        {
            return cached with { FromCache = true };
        }

        if (!IsConfigured)
        {
            return new PageLayoutPlan(false, "unknown", null, new List<LayoutZonePlan>());
        }

        try
        {
            byte[] pngBytes = await Task.Run(() =>
            {
                using var pdfStream = File.OpenRead(pdfFilePath);
#pragma warning disable CA1416
                using var bitmap = Conversion.ToImage(pdfStream, page: new Index(zeroBasedPageIndex), options: new RenderOptions(Dpi: _analyzerDpi));
#pragma warning restore CA1416
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 80);
                return data.ToArray();
            }, cancellationToken);

            var prompt =
                "Analyze this engineering PDF page layout. Return ONLY compact JSON: " +
                "{\"pageType\":\"bordered_table|column_matrix|multi_zone|mixed_zones|prose|scanned\"," +
                "\"splitXPct\":null or 0..1," +
                "\"zones\":[{\"name\":\"Header|Left Notes|Valve Table|DN Matrix|Body\",\"tool\":\"bordered|column_grid|prose|ocr\",\"xLeftPct\":0..1,\"xRightPct\":0..1,\"yTopPct\":0..1,\"yBottomPct\":0..1}]," +
                "\"notes\":\"short 1-line summary\"}. " +
                "Coordinates are normalized 0..1 from TOP-LEFT. Keep zones non-overlapping and minimal.";

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = prompt },
                            new
                            {
                                inlineData = new
                                {
                                    mimeType = "image/png",
                                    data = Convert.ToBase64String(pngBytes)
                                }
                            }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.0,
                    maxOutputTokens = 512,
                    responseMimeType = "application/json",
                    thinkingConfig = new { thinkingBudget = 0 }
                }
            };

            var text = await PostAndGetTextAsync(payload, $"Layout p{zeroBasedPageIndex + 1}", cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new PageLayoutPlan(false, "unknown", null, new List<LayoutZonePlan>());
            }

            var cleanedJson = StripCodeFence(text);
            var parsed = JsonSerializer.Deserialize<LayoutPlanJson>(cleanedJson, JsonOptions);
            if (parsed == null || string.IsNullOrWhiteSpace(parsed.PageType))
            {
                return new PageLayoutPlan(false, "unknown", null, new List<LayoutZonePlan>());
            }

            var zones = (parsed.Zones ?? new List<LayoutZoneJson>())
                .Select(z => new LayoutZonePlan(
                    string.IsNullOrWhiteSpace(z.Name) ? "Zone" : z.Name.Trim(),
                    NormalizeTool(z.Tool),
                    Math.Clamp(z.XLeftPct, 0.0, 1.0),
                    Math.Clamp(z.XRightPct <= z.XLeftPct ? 1.0 : z.XRightPct, 0.0, 1.0),
                    Math.Clamp(z.YTopPct, 0.0, 1.0),
                    Math.Clamp(z.YBottomPct <= z.YTopPct ? 1.0 : z.YBottomPct, 0.0, 1.0)))
                .ToList();

            var plan = new PageLayoutPlan(
                true,
                parsed.PageType.Trim().ToLowerInvariant(),
                parsed.SplitXPct is >= 0.2 and <= 0.8 ? parsed.SplitXPct : null,
                zones,
                parsed.Notes?.Trim(),
                FromCache: false);

            _layoutCache[layoutFingerprint] = plan;
            SaveLayoutCache();
            return plan;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gemini layout analysis skipped on page {Page}", zeroBasedPageIndex + 1);
            return new PageLayoutPlan(false, "unknown", null, new List<LayoutZonePlan>());
        }
    }

    public async Task<GeminiExtractionResult> ExtractTableFromPageImageAsync(
        string pdfFilePath,
        int zeroBasedPageIndex,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new GeminiExtractionResult(false, new(), string.Empty, Error: "Gemini API is disabled or missing API key.");
        }

        try
        {
            byte[] pngBytes = await Task.Run(() =>
            {
                using var pdfStream = File.OpenRead(pdfFilePath);
#pragma warning disable CA1416
                using var bitmap = Conversion.ToImage(pdfStream, page: new Index(zeroBasedPageIndex), options: new RenderOptions(Dpi: _renderDpi));
#pragma warning restore CA1416
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
                return data.ToArray();
            }, cancellationToken);

            var prompt =
                "Extract EVERY SINGLE ROW and ALL text from top to bottom of this engineering PDF page accurately. " +
                "Do NOT stop early, summarize, or truncate rows. " +
                "IGNORE and OMIT any text, numbers, or rows that have a horizontal line/strikethrough drawn through them (crossed-out or deleted values in red, blue, or black). " +
                "For example, if '350' is crossed out in 'DN 15-350-200', output only 'DN 15-200'; if an entire row or note is crossed out, omit it completely. " +
                "If the page has two side-by-side sections (left panel and right panel), extract all rows of the left section and all rows of the right section without mixing or shifting columns. " +
                "Return ONLY valid JSON: {\"rows\":[[\"cell1\",\"cell2\"]],\"summary\":\"short 1-line description\"}. " +
                "Preserve blank cells as \"\" so columns stay aligned. Do not invent values.";

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = prompt },
                            new
                            {
                                inlineData = new
                                {
                                    mimeType = "image/png",
                                    data = Convert.ToBase64String(pngBytes)
                                }
                            }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.0,
                    maxOutputTokens = _maxOutputTokens,
                    responseMimeType = "application/json",
                    thinkingConfig = new { thinkingBudget = 0 }
                }
            };

            return await SendGeminiTableRequestAsync(payload, $"Vision p{zeroBasedPageIndex + 1}", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini Vision extraction failed on page {Page} ({Reason})", zeroBasedPageIndex + 1, reason);
            return new GeminiExtractionResult(false, new(), string.Empty, Error: ex.Message);
        }
    }

    public async Task<GeminiExtractionResult> RepairTableFromTextAsync(
        string compactPageText,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(compactPageText))
        {
            return new GeminiExtractionResult(false, new(), string.Empty, Error: "Gemini API is not configured or text is empty.");
        }

        try
        {
            var trimmed = compactPageText.Length > 8000 ? compactPageText[..8000] : compactPageText;
            var prompt =
                "Fix OCR typos and align the columns of this engineering PDF page text. " +
                "Return ONLY valid JSON: {\"rows\":[[\"cell1\",\"cell2\"]],\"summary\":\"short 1-line description\"}. " +
                "Keep all numbers, pipe classes, and units exact.\n\nTEXT:\n" + trimmed;

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = prompt }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.0,
                    maxOutputTokens = _maxOutputTokens,
                    responseMimeType = "application/json",
                    thinkingConfig = new { thinkingBudget = 0 }
                }
            };

            return await SendGeminiTableRequestAsync(payload, "TextRepair", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini text repair failed ({Reason})", reason);
            return new GeminiExtractionResult(false, new(), string.Empty, Error: ex.Message);
        }
    }

    private async Task<GeminiExtractionResult> SendGeminiTableRequestAsync(object payload, string tag, CancellationToken cancellationToken)
    {
        var text = await PostAndGetTextAsync(payload, tag, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new GeminiExtractionResult(false, new(), string.Empty, Error: "Empty response from Gemini.");
        }

        var cleanedJson = StripCodeFence(text);
        var parsed = JsonSerializer.Deserialize<GeminiJsonPayload>(cleanedJson, JsonOptions);
        var rows = parsed?.Rows?
            .Where(r => r != null && r.Any(c => !string.IsNullOrWhiteSpace(c)))
            .Select(r => r.Select(c => (c ?? string.Empty).Trim()).ToList())
            .ToList() ?? new List<List<string>>();

        if (rows.Count == 0)
        {
            return new GeminiExtractionResult(false, new(), string.Empty, Error: "Gemini returned 0 rows.");
        }

        int maxCols = rows.Max(r => r.Count);
        foreach (var r in rows)
        {
            while (r.Count < maxCols) r.Add(string.Empty);
        }

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            int lastNonEmpty = row.FindLastIndex(c => !string.IsNullOrWhiteSpace(c));
            if (lastNonEmpty >= 0)
            {
                sb.AppendLine(string.Join(" | ", row.Take(lastNonEmpty + 1)));
            }
        }

        return new GeminiExtractionResult(true, rows, sb.ToString().Trim(), parsed?.Summary);
    }

    private async Task<string?> PostAndGetTextAsync(object payload, string tag, CancellationToken cancellationToken)
    {
        var serializedPayload = JsonSerializer.Serialize(payload);
        var attempts = new List<(string Model, int DelayMs)>
        {
            (_model, 0),
            (_model, 900)
        };
        if (!string.IsNullOrWhiteSpace(_fallbackModel) && !_fallbackModel.Equals(_model, StringComparison.OrdinalIgnoreCase))
        {
            attempts.Add((_fallbackModel, 300));
        }

        for (int i = 0; i < attempts.Count; i++)
        {
            var (modelName, delayMs) = attempts[i];
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }

            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(modelName)}:generateContent";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Add("x-goog-api-key", _apiKey);
            request.Content = new StringContent(serializedPayload, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                bool isTransient = status is 429 or 500 or 503 or 504;

                if (isTransient && i < attempts.Count - 1)
                {
                    continue;
                }

                return null;
            }

            using var doc = JsonDocument.Parse(body);

            string? text = null;
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var cand = candidates[0];
                if (cand.TryGetProperty("content", out var contentEl) &&
                    contentEl.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0 &&
                    parts[0].TryGetProperty("text", out var textEl))
                {
                    text = textEl.GetString();
                }
            }

            return text;
        }

        return null;
    }

    private static string StripCodeFence(string text)
    {
        var cleaned = text.Trim();
        if (cleaned.StartsWith("```"))
        {
            var firstNewline = cleaned.IndexOf('\n');
            var lastFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && lastFence > firstNewline)
            {
                cleaned = cleaned[(firstNewline + 1)..lastFence].Trim();
            }
        }
        return cleaned;
    }

    private static string NormalizeTool(string? tool)
    {
        var t = (tool ?? "column_grid").Trim().ToLowerInvariant();
        return t switch
        {
            "bordered" or "column_grid" or "prose" or "ocr" => t,
            _ => "column_grid"
        };
    }

    private void LoadLayoutCache()
    {
        try
        {
            if (!File.Exists(_layoutCachePath)) return;
            var json = File.ReadAllText(_layoutCachePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, PageLayoutPlan>>(json, JsonOptions);
            if (dict == null) return;
            foreach (var kv in dict)
            {
                _layoutCache[kv.Key] = kv.Value;
            }
        }
        catch
        {
            // Ignore corrupt cache file
        }
    }

    private void SaveLayoutCache()
    {
        try
        {
            var dir = Path.GetDirectoryName(_layoutCachePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_layoutCache.ToDictionary(k => k.Key, v => v.Value));
            File.WriteAllText(_layoutCachePath, json);
        }
        catch
        {
            // Ignore disk cache write errors
        }
    }

    private static string TryReadKeyFromDevSettings(string contentRootPath)
    {
        try
        {
            var devPath = Path.Combine(contentRootPath, "appsettings.Development.json");
            if (!File.Exists(devPath)) return string.Empty;

            using var doc = JsonDocument.Parse(File.ReadAllText(devPath));
            if (doc.RootElement.TryGetProperty("Gemini", out var geminiEl) &&
                geminiEl.TryGetProperty("ApiKey", out var keyEl) &&
                keyEl.ValueKind == JsonValueKind.String)
            {
                return (keyEl.GetString() ?? string.Empty).Trim();
            }
        }
        catch
        {
            // Ignore malformed or unreadable appsettings.Development.json
        }

        return string.Empty;
    }

    private sealed class GeminiJsonPayload
    {
        public List<List<string?>>? Rows { get; set; }
        public string? Summary { get; set; }
    }

    private sealed class LayoutPlanJson
    {
        public string? PageType { get; set; }
        public double? SplitXPct { get; set; }
        public List<LayoutZoneJson>? Zones { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class LayoutZoneJson
    {
        public string? Name { get; set; }
        public string? Tool { get; set; }
        public double XLeftPct { get; set; }
        public double XRightPct { get; set; } = 1.0;
        public double YTopPct { get; set; }
        public double YBottomPct { get; set; } = 1.0;
    }
}
