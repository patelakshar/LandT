using System.Text;
using System.Text.RegularExpressions;
using PdfSolverMvc.Models;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace PdfSolverMvc.Services;

/// <summary>
/// Core PDF extraction pipeline. Arranges raw PDF content into structured per-page grids:
/// 1. Reads vector letters/paths with PdfPig (or falls back to PDFtoImage + SkiaSharp + Tesseract OCR).
/// 2. Detects page zones (bordered tables, multi-zone splits, prose, or column-aligned matrices).
/// 3. Groups words by Y-center into rows and X-position into columns, padding each row to a uniform
///    2D grid (List&lt;List&lt;string&gt;&gt;) so blank cells stay aligned for UI tables and CSV export.
/// 4. Optionally refines/extracts each page via Gemini AI and combines all pages into PdfExtractionResult.
/// </summary>
public class PdfExtractionService : IPdfExtractionService
{
    private readonly IOcrService _ocrService;
    private readonly IGeminiService? _geminiService;
    private readonly int _minSelectableCharsPerPage;
    private readonly float _minGeminiOcrConfidence;
    private readonly bool _alwaysExtractEveryPage;
    private readonly ILogger<PdfExtractionService> _logger;

    public PdfExtractionService(
        IOcrService ocrService,
        IConfiguration configuration,
        ILogger<PdfExtractionService> logger,
        IGeminiService? geminiService = null)
    {
        _ocrService = ocrService;
        _geminiService = geminiService;
        _minSelectableCharsPerPage = Math.Max(1, configuration.GetValue<int?>("Ocr:MinSelectableCharsPerPage") ?? 15);
        _minGeminiOcrConfidence = Math.Clamp(configuration.GetValue<float?>("Gemini:MinOcrConfidenceForAutoFallback") ?? 0.65f, 0.1f, 0.95f);
        _alwaysExtractEveryPage = configuration.GetValue<bool?>("Gemini:AlwaysExtractEveryPage") ?? true;
        _logger = logger;
    }

    private sealed record WordBox(double Left, double Right, double Bottom, double Top, double Height, double Width, string Text);
    private sealed record CellToken(double Left, double Right, double CenterX, string Text);

    private sealed class ColumnCluster
    {
        public double MinLeft { get; set; }
        public double MaxRight { get; set; }
        public double CenterX => (MinLeft + MaxRight) / 2.0;
    }

    public async Task<PdfExtractionResult> ExtractAsync(
        string pdfFilePath,
        string? imageOutputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var pages = new List<PdfPageContent>();
        var fullTextBuilder = new StringBuilder();
        var notes = new List<string>();
        int vectorPages = 0;
        int ocrPages = 0;
        int emptyScannedPages = 0;

        if (!string.IsNullOrWhiteSpace(imageOutputDirectory))
        {
            Directory.CreateDirectory(imageOutputDirectory);
        }

        try
        {
            using var document = PdfDocument.Open(pdfFilePath, new ParsingOptions { UseLenientParsing = true });
            int pageCount = document.NumberOfPages;

            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var filteredLetters = FilterOutStrikethroughLetters(page, page.Letters);
                var streamWords = BuildStreamOrderedWords(filteredLetters);
                int selectableChars = streamWords.Sum(w => w.Text.Length);
                double pageRotation = page.Rotation.Value;

                var extractedImages = ExtractPageImages(page, streamWords, imageOutputDirectory);

                PdfPageContent pageContent;

                if (selectableChars >= _minSelectableCharsPerPage)
                {
                    PageLayoutPlan? layoutPlan = null;
                    if (_geminiService is { IsConfigured: true } && (_alwaysExtractEveryPage || !(TryExtractDrawnGridTable(page, streamWords, out var preGridText, out _) && (CountWords(preGridText) / (double)Math.Max(1, streamWords.Count)) >= 0.92)))
                    {
                        var fingerprint = BuildLayoutFingerprint(page, streamWords);
                        layoutPlan = await _geminiService.AnalyzePageLayoutAsync(pdfFilePath, page.Number - 1, fingerprint, cancellationToken);
                    }

                    pageContent = ExtractVectorPage(page, streamWords, pageRotation, layoutPlan);
                    vectorPages++;

                    // Multi-tool cross-check: if vector text is garbled OR page has a large image with sparse text, cross-check with Tesseract OCR
                    bool garbledVector = IsTextLikelyGarbled(pageContent.Content);
                    double pageArea = Math.Max(1.0, page.Width * page.Height);
                    bool hasLargeImage = extractedImages.Any(img => (img.BoundsWidth * img.BoundsHeight) / pageArea >= 0.30);

                    if (garbledVector || (hasLargeImage && pageContent.WordCount < 60))
                    {
                        var ocrCross = await ExtractScannedPageAsync(pdfFilePath, page, streamWords, pageRotation, cancellationToken);
                        if (ocrCross.ExtractionType.StartsWith("OCR", StringComparison.OrdinalIgnoreCase) &&
                            (garbledVector || ocrCross.WordCount > pageContent.WordCount * 1.25))
                        {
                            if (garbledVector || pageContent.WordCount < 25)
                            {
                                ocrCross.ExtractionType = "OCR Fallback (Cross-Checked)";
                                ocrCross.ExtractionNotes = garbledVector
                                    ? $"Cross-check: Vector font garbled -> switched to Tesseract OCR (Conf={ocrCross.OcrConfidence:P0})."
                                    : $"Cross-check: Large image + sparse vector text -> switched to Tesseract OCR (Conf={ocrCross.OcrConfidence:P0}).";
                                pageContent = ocrCross;
                                vectorPages--;
                                ocrPages++;
                            }
                            else
                            {
                                pageContent.ExtractionType = "Hybrid (Vector + OCR)";
                                pageContent.Content = $"{pageContent.Content}\n\n--- OCR Cross-Check (Embedded Image/Scan) ---\n{ocrCross.Content}".Trim();
                                pageContent.TableRows.Add(new List<string> { "[OCR Cross-Check]" });
                                pageContent.TableRows.AddRange(ocrCross.TableRows);
                                pageContent.WordCount = CountWords(pageContent.Content);
                                pageContent.OcrConfidence = ocrCross.OcrConfidence;
                                pageContent.SelectedPsm = ocrCross.SelectedPsm;
                                pageContent.ExtractionNotes = $"Cross-check: Merged PdfPig vector text with Tesseract OCR (+{ocrCross.WordCount} OCR words, Conf={ocrCross.OcrConfidence:P0}).";
                                ocrPages++;
                            }
                        }
                    }
                }
                else
                {
                    pageContent = await ExtractScannedPageAsync(pdfFilePath, page, streamWords, pageRotation, cancellationToken);
                    if (pageContent.ExtractionType.StartsWith("OCR", StringComparison.OrdinalIgnoreCase))
                    {
                        ocrPages++;
                    }
                    else
                    {
                        emptyScannedPages++;
                    }

                    if (!string.IsNullOrWhiteSpace(pageContent.ExtractionNotes) &&
                        !notes.Contains(pageContent.ExtractionNotes))
                    {
                        notes.Add(pageContent.ExtractionNotes);
                    }
                }

                // Gemini AI extraction on every page (when AlwaysExtractEveryPage is true) OR when Quality Gate triggers
                if (_geminiService is { IsConfigured: true })
                {
                    var qualityIssue = EvaluatePageQuality(pageContent, streamWords.Count);
                    bool runGemini = _alwaysExtractEveryPage || !string.IsNullOrWhiteSpace(qualityIssue);
                    if (runGemini)
                    {
                        string aiReason = !string.IsNullOrWhiteSpace(qualityIssue)
                            ? qualityIssue
                            : "AlwaysExtractEveryPage enabled";

                        GeminiExtractionResult? aiResult = null;
                        if (!_alwaysExtractEveryPage && pageContent.OcrConfidence.HasValue && !string.IsNullOrWhiteSpace(pageContent.Content) && pageContent.Content.Length >= 40)
                        {
                            aiResult = await _geminiService.RepairTableFromTextAsync(pageContent.Content, aiReason, cancellationToken);
                        }

                        if (aiResult is not { Success: true })
                        {
                            aiResult = await _geminiService.ExtractTableFromPageImageAsync(pdfFilePath, page.Number - 1, aiReason, cancellationToken);
                        }

                        int aiWordCount = aiResult is { Success: true } ? CountWords(aiResult.Content) : 0;
                        bool localIsCompleteVector = pageContent.WordCount >= 40 && !IsTextLikelyGarbled(pageContent.Content);
                        bool aiHasEnoughWords = localIsCompleteVector
                            ? (aiWordCount >= pageContent.WordCount * 0.78 &&
                               (pageContent.TableRows.Count < 10 || aiResult!.TableRows.Count >= pageContent.TableRows.Count * 0.65))
                            : (streamWords.Count < 15 || aiWordCount >= streamWords.Count * 0.55 || !string.IsNullOrWhiteSpace(qualityIssue));

                        if (aiResult is { Success: true } && aiResult.TableRows.Count > 0 && aiHasEnoughWords)
                        {
                            if (pageContent.ExtractionType.StartsWith("Scanned", StringComparison.OrdinalIgnoreCase))
                            {
                                emptyScannedPages = Math.Max(0, emptyScannedPages - 1);
                                ocrPages++;
                            }

                            pageContent.ExtractionType = "AI (Gemini)";
                            pageContent.TableRows = aiResult.TableRows;
                            pageContent.Content = aiResult.Content;
                            pageContent.WordCount = aiWordCount;
                            pageContent.ExtractionNotes = string.IsNullOrWhiteSpace(aiResult.Summary)
                                ? $"Extracted via Gemini AI ({aiReason})."
                                : $"Extracted via Gemini AI ({aiReason}): {aiResult.Summary}";

                            if (!notes.Contains(pageContent.ExtractionNotes))
                            {
                                notes.Add(pageContent.ExtractionNotes);
                            }
                        }
                    }
                }

                pageContent.Images = extractedImages;
                pages.Add(pageContent);

                fullTextBuilder.AppendLine($"--- Page {pageContent.PageNumber} ({pageContent.ExtractionType}) ---");
                fullTextBuilder.AppendLine(pageContent.Content);
                fullTextBuilder.AppendLine();
            }

            int aiPages = pages.Count(p => p.ExtractionType.StartsWith("AI", StringComparison.OrdinalIgnoreCase));
            string method = (vectorPages, ocrPages, aiPages, emptyScannedPages) switch
            {
                (> 0, 0, 0, 0) => "DirectText",
                (0, > 0, > 0, _) => "AiEnhancedOcr",
                (> 0, _, > 0, _) => "HybridAi",
                (0, 0, > 0, _) => "AiRescue",
                (0, > 0, 0, _) => "OcrFallback",
                (> 0, > 0, 0, _) => "HybridOcr",
                (_, 0, 0, > 0) => "NeedsOcr",
                _ => "DirectText"
            };

            string status = emptyScannedPages > 0 && vectorPages == 0 && ocrPages == 0
                ? "PartialOcrNeeded"
                : "Processed";

            return new PdfExtractionResult(
                pageCount,
                fullTextBuilder.ToString().Trim(),
                pages,
                method,
                status,
                notes.Count > 0 ? string.Join(" | ", notes) : null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract PDF {File}", pdfFilePath);
            return new PdfExtractionResult(
                0,
                string.Empty,
                pages,
                "Failed",
                "Error",
                $"PDF extraction error: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes any vector letters crossed out by a horizontal strikethrough line in page.Paths
    /// (for example deleted rows or crossed-out numbers like DN 15-~~350~~-200 -> DN 15-200).
    /// </summary>
    private static IReadOnlyList<Letter> FilterOutStrikethroughLetters(Page page, IReadOnlyList<Letter> letters)
    {
        if (letters.Count == 0 || page.Paths == null || page.Paths.Count == 0)
        {
            return letters;
        }

        var strikeSegments = new List<(double Left, double Right, double CenterY)>();

        void CollectIfHorizontalStrike(PdfRectangle r)
        {
            if (r.Height <= 2.6 && r.Width >= 2.0)
            {
                strikeSegments.Add((r.Left, r.Right, (r.Bottom + r.Top) / 2.0));
            }
        }

        foreach (var path in page.Paths)
        {
            var pathRect = path.GetBoundingRectangle();
            if (pathRect != null)
            {
                CollectIfHorizontalStrike(pathRect.Value);
            }

            foreach (var subpath in path)
            {
                var subRect = subpath.GetBoundingRectangle();
                if (subRect != null)
                {
                    CollectIfHorizontalStrike(subRect.Value);
                }
            }
        }

        if (strikeSegments.Count == 0)
        {
            return letters;
        }

        var struck = new bool[letters.Count];
        for (int i = 0; i < letters.Count; i++)
        {
            var letter = letters[i];
            if (string.IsNullOrWhiteSpace(letter.Value)) continue;

            var box = letter.BoundingBox;
            double h = box.Height;
            if (h < 3.2) continue;

            double cx = (box.Left + box.Right) / 2.0;
            double minY = box.Bottom + h * 0.25;
            double maxY = box.Top - h * 0.20;

            foreach (var seg in strikeSegments)
            {
                if (cx >= seg.Left - 0.4 && cx <= seg.Right + 0.4 &&
                    seg.CenterY >= minY && seg.CenterY <= maxY)
                {
                    struck[i] = true;
                    break;
                }
            }
        }

        // Also remove tiny punctuation (like '-') that sits directly on a strike line next to a struck letter
        for (int i = 0; i < letters.Count; i++)
        {
            if (struck[i]) continue;
            var letter = letters[i];
            if (string.IsNullOrWhiteSpace(letter.Value) || letter.BoundingBox.Height >= 3.2) continue;

            var box = letter.BoundingBox;
            double cx = (box.Left + box.Right) / 2.0;
            double cy = (box.Bottom + box.Top) / 2.0;
            bool neighborStruck = (i > 0 && struck[i - 1]) || (i + 1 < letters.Count && struck[i + 1]);
            if (!neighborStruck) continue;

            foreach (var seg in strikeSegments)
            {
                if (cx >= seg.Left - 1.0 && cx <= seg.Right + 1.0 &&
                    Math.Abs(seg.CenterY - cy) <= 1.6)
                {
                    struck[i] = true;
                    break;
                }
            }
        }

        var result = new List<Letter>(letters.Count);
        for (int i = 0; i < letters.Count; i++)
        {
            if (!struck[i])
            {
                result.Add(letters[i]);
            }
        }
        return result;
    }

    private static List<WordBox> BuildStreamOrderedWords(IReadOnlyList<Letter> letters)
    {
        var words = new List<WordBox>();
        if (letters.Count == 0) return words;

        var currentText = new StringBuilder();
        double left = 0, right = 0, bottom = 0, top = 0;
        Letter? prev = null;

        void Flush()
        {
            if (currentText.Length == 0) return;
            var cleaned = NormalizeSpaces(currentText.ToString());
            if (!string.IsNullOrEmpty(cleaned))
            {
                words.Add(new WordBox(
                    left,
                    right,
                    bottom,
                    top,
                    Math.Max(1.0, top - bottom),
                    Math.Max(1.0, right - left),
                    cleaned));
            }
            currentText.Clear();
        }

        foreach (var letter in letters)
        {
            if (string.IsNullOrWhiteSpace(letter.Value))
            {
                Flush();
                prev = null;
                continue;
            }

            var box = letter.BoundingBox;
            if (prev == null)
            {
                currentText.Append(letter.Value);
                left = box.Left;
                right = box.Right;
                bottom = box.Bottom;
                top = box.Top;
                prev = letter;
                continue;
            }

            var prevBox = prev.BoundingBox;
            double charHeight = Math.Max(4.0, Math.Max(prevBox.Height, box.Height));
            double avgCharWidth = Math.Max(charHeight * 0.48, prevBox.Width);
            double dy = Math.Abs(box.Bottom - prevBox.Bottom);
            double dx = box.Left - prevBox.Right;
            bool isPunctJoin = ".,-/():".Contains(letter.Value) || ".,-/(".Contains(prev.Value);
            double maxDx = isPunctJoin ? avgCharWidth * 1.15 : avgCharWidth * 0.72;

            bool sameWord = dy <= charHeight * 0.45 &&
                            dx >= -avgCharWidth * 0.45 &&
                            dx <= maxDx;

            if (sameWord)
            {
                currentText.Append(letter.Value);
                left = Math.Min(left, box.Left);
                right = Math.Max(right, box.Right);
                bottom = Math.Min(bottom, box.Bottom);
                top = Math.Max(top, box.Top);
            }
            else
            {
                Flush();
                currentText.Append(letter.Value);
                left = box.Left;
                right = box.Right;
                bottom = box.Bottom;
                top = box.Top;
            }

            prev = letter;
        }

        Flush();
        return words;
    }

    private static string NormalizeSpaces(string input)
    {
        return Regex.Replace(input, @"\s+", " ").Trim();
    }

    private List<PdfExtractedImage> ExtractPageImages(
        Page page,
        List<WordBox> pageWords,
        string? imageOutputDirectory)
    {
        var results = new List<PdfExtractedImage>();
        if (string.IsNullOrWhiteSpace(imageOutputDirectory))
        {
            return results;
        }

        int index = 0;
        foreach (var pdfImage in page.GetImages())
        {
            index++;
            try
            {
                byte[]? pngBytes = null;
                if (pdfImage.TryGetPng(out var directPng) && directPng is { Length: > 0 })
                {
                    pngBytes = directPng;
                }
                else if (pdfImage.TryGetBytesAsMemory(out var rawMemory) && !rawMemory.IsEmpty)
                {
                    using var decoded = SKBitmap.Decode(rawMemory.ToArray());
                    if (decoded != null)
                    {
                        using var encoded = decoded.Encode(SKEncodedImageFormat.Png, 100);
                        pngBytes = encoded.ToArray();
                    }
                }

                if (pngBytes == null || pngBytes.Length == 0)
                {
                    continue;
                }

                var fileName = $"page{page.Number}_img{index}.png";
                var fullPath = Path.Combine(imageOutputDirectory, fileName);
                File.WriteAllBytes(fullPath, pngBytes);

                var bounds = pdfImage.BoundingBox;
                var caption = FindNearbyCaption(bounds, pageWords);

                results.Add(new PdfExtractedImage
                {
                    ImageId = $"P{page.Number}-IMG{index}",
                    PageNumber = page.Number,
                    RelativeFileName = fileName,
                    WidthPx = pdfImage.WidthInSamples,
                    HeightPx = pdfImage.HeightInSamples,
                    BoundsLeft = Math.Round(bounds.Left, 1),
                    BoundsBottom = Math.Round(bounds.Bottom, 1),
                    BoundsWidth = Math.Round(bounds.Width, 1),
                    BoundsHeight = Math.Round(bounds.Height, 1),
                    NearbyCaption = caption
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not export embedded image {Index} on page {Page}", index, page.Number);
            }
        }

        return results;
    }

    private static string? FindNearbyCaption(PdfRectangle bounds, List<WordBox> words)
    {
        if (words.Count == 0) return null;

        double imgCenterX = (bounds.Left + bounds.Right) / 2.0;

        var candidateWords = words
            .Where(w =>
            {
                bool horizontallyAligned = w.Right >= bounds.Left - 40 &&
                                           w.Left <= bounds.Right + 40;
                double distBelow = bounds.Bottom - w.Top;
                double distAbove = w.Bottom - bounds.Top;
                bool nearBelow = distBelow >= -5 && distBelow <= 55;
                bool nearAbove = distAbove >= -5 && distAbove <= 40;
                return horizontallyAligned && (nearBelow || nearAbove);
            })
            .OrderByDescending(w => w.Bottom)
            .ThenBy(w => w.Left)
            .ToList();

        if (candidateWords.Count == 0) return null;

        var lines = new List<List<WordBox>>();
        foreach (var w in candidateWords)
        {
            var row = lines.FirstOrDefault(r => Math.Abs(r[0].Bottom - w.Bottom) <= 4.0);
            if (row != null) row.Add(w);
            else lines.Add(new List<WordBox> { w });
        }

        var bestLine = lines
            .OrderBy(line =>
            {
                double lineY = line.Average(w => w.Bottom);
                double lineX = line.Average(w => (w.Left + w.Right) / 2.0);
                double dy = lineY < bounds.Bottom
                    ? (bounds.Bottom - lineY)
                    : (lineY - bounds.Top) + 15;
                return dy + Math.Abs(lineX - imgCenterX) * 0.1;
            })
            .FirstOrDefault();

        if (bestLine == null) return null;
        var text = NormalizeSpaces(string.Join(" ", bestLine.OrderBy(w => w.Left).Select(w => w.Text)));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private string? EvaluatePageQuality(PdfPageContent pageContent, int rawWordCount)
    {
        if (pageContent.WordCount == 0) return "0 extracted words";
        if (IsTextLikelyGarbled(pageContent.Content)) return "garbled vector text";
        if (pageContent.ExtractionType.StartsWith("Scanned", StringComparison.OrdinalIgnoreCase)) return "local OCR unavailable";
        if (pageContent.OcrConfidence.HasValue && pageContent.OcrConfidence.Value < _minGeminiOcrConfidence)
            return $"low OCR confidence ({pageContent.OcrConfidence:P0})";

        if (rawWordCount >= 20)
        {
            double coverage = pageContent.WordCount / (double)rawWordCount;
            if (coverage < 0.80)
                return $"low word coverage ({coverage:P0})";
        }

        var rows = pageContent.TableRows;
        if (rows != null && rows.Count >= 10 && !pageContent.ExtractionType.Contains("Bordered", StringComparison.OrdinalIgnoreCase))
        {
            int maxCols = rows.Max(r => r.Count);
            double avgNonEmpty = rows.Average(r => r.Count(c => !string.IsNullOrWhiteSpace(c)));

            if (maxCols >= 20 && avgNonEmpty < 2.5)
                return $"column explosion ({maxCols} cols, avg {avgNonEmpty:F1} cells/row)";

            // Detect merged multi-code cells (e.g., "4121S 4141S 4161S" crammed in one cell)
            int multiCodeCells = rows.Sum(r => r.Count(c =>
                !string.IsNullOrWhiteSpace(c) &&
                Regex.IsMatch(c.Trim(), @"^(?:[A-Z0-9-]{2,8}\s+){2,}[A-Z0-9-]{2,8}$")));
            if (multiCodeCells >= 3)
                return $"merged multi-code cells ({multiCodeCells} cells)";

            if (!pageContent.ExtractionType.Contains("Prose", StringComparison.OrdinalIgnoreCase))
            {
                int scatteredProseRows = rows.Count(r =>
                    r.Count(c => !string.IsNullOrWhiteSpace(c)) >= 4 &&
                    r.Count(c => !string.IsNullOrWhiteSpace(c) && Regex.IsMatch(c.Trim(), @"^[a-z]{3,}")) >= 3);
                if (scatteredProseRows >= 2)
                    return $"prose sentences split across {maxCols} columns";

                int tinyRows = rows.Count(r =>
                {
                    int w = r.Sum(c => CountWords(c));
                    return w is >= 1 and <= 2;
                });
                if (tinyRows / (double)rows.Count > 0.60 && avgNonEmpty <= 1.6)
                    return $"fragmented rows ({tinyRows}/{rows.Count} tiny rows)";
            }
        }

        return null;
    }

    private static bool IsTextLikelyGarbled(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        int total = 0;
        int readable = 0;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;
            total++;
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') ||
                ".,;:-/()[]%+°&'".Contains(ch))
            {
                readable++;
            }
        }
        return total >= 15 && (readable / (double)total) < 0.55;
    }

    private static string BuildLayoutFingerprint(Page page, List<WordBox> words)
    {
        int wBucket = (int)(Math.Round(page.Width / 25.0) * 25);
        int hBucket = (int)(Math.Round(page.Height / 25.0) * 25);
        int pathBucket = (page.Paths?.Count ?? 0) switch
        {
            0 => 0,
            <= 10 => 1,
            <= 60 => 2,
            _ => 3
        };
        int wordBucket = words.Count / 40;
        return $"{wBucket}x{hBucket}_p{pathBucket}_w{wordBucket}";
    }

    private static PdfPageContent ExtractVectorPage(Page page, List<WordBox> streamWords, double pageRotation, PageLayoutPlan? layoutPlan = null)
    {
        string alignedText;
        List<List<string>> tableRows;
        string modeLabel;
        var notes = new List<string>();
        int streamWordCount = Math.Max(1, streamWords.Count);

        if (layoutPlan is { Success: true } && layoutPlan.PageType == "prose")
        {
            (alignedText, tableRows) = ExtractProseRows(streamWords);
            modeLabel = "Vector (AI-Guided Prose)";
            notes.Add(layoutPlan.FromCache
                ? "Reused learned prose layout (0 tokens)."
                : $"AI Layout Analyzer detected prose: {layoutPlan.Notes}");
        }
        else if (layoutPlan is { Success: true, Zones.Count: >= 2 } && TryExtractMultiToolZones(page, streamWords, layoutPlan, out var multiText, out var multiRows, out var toolsSummary))
        {
            int multiWords = CountWords(multiText);
            double coverage = multiWords / (double)streamWordCount;
            if (coverage >= 0.78)
            {
                alignedText = multiText;
                tableRows = multiRows;
                modeLabel = $"Multi-Tool ({toolsSummary})";
                notes.Add(layoutPlan.FromCache
                    ? $"Reused learned {layoutPlan.Zones.Count}-zone plan ({Math.Min(100, (int)Math.Round(coverage * 100))}% coverage)."
                    : $"AI-guided {layoutPlan.Zones.Count}-zone multi-tool extraction ({Math.Min(100, (int)Math.Round(coverage * 100))}% coverage).");
            }
            else
            {
                (alignedText, tableRows, modeLabel) = RunLocalMultiToolCrossCheck(page, streamWords, pageRotation, layoutPlan, notes);
            }
        }
        else
        {
            (alignedText, tableRows, modeLabel) = RunLocalMultiToolCrossCheck(page, streamWords, pageRotation, layoutPlan, notes);
        }

        var nnWords = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(nnWords);
        var orderedBlocks = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
        var readingOrderText = string.Join(
            Environment.NewLine + Environment.NewLine,
            orderedBlocks.Select(b => NormalizeSpaces(b.Text)).Where(t => !string.IsNullOrWhiteSpace(t)));

        var finalText = !string.IsNullOrWhiteSpace(alignedText) ? alignedText : readingOrderText;
        var wordCount = CountWords(finalText);

        if (pageRotation != 0) notes.Add($"Vector page rotated {pageRotation:0}°; coordinates normalized.");

        return new PdfPageContent
        {
            PageNumber = page.Number,
            ExtractionType = modeLabel,
            WordCount = wordCount,
            Content = finalText,
            TableRows = tableRows,
            RawOcrText = readingOrderText != finalText ? readingOrderText : null,
            RotationDegrees = pageRotation,
            ExtractionNotes = notes.Count > 0 ? string.Join(" ", notes) : null
        };
    }

    private static (string Text, List<List<string>> Rows, string ModeLabel) RunLocalMultiToolCrossCheck(
        Page page,
        List<WordBox> streamWords,
        double pageRotation,
        PageLayoutPlan? layoutPlan,
        List<string> notes)
    {
        int streamWordCount = Math.Max(1, streamWords.Count);
        double? forcedSplitX = layoutPlan is { Success: true, SplitXPct: not null }
            ? page.Width * layoutPlan.SplitXPct.Value
            : null;

        if (TryExtractDrawnGridTable(page, streamWords, out var gridText, out var gridRows))
        {
            int gridWords = CountWords(gridText);
            double coverage = gridWords / (double)streamWordCount;
            if (coverage < 0.94)
            {
                var (colText, colRows) = ExtractColumnAlignedGrid(streamWords);
                if (CountWords(colText) > gridWords)
                {
                    notes.Add($"Cross-check: Bordered Grid covered {coverage:P0} of words; fell back to Column-Grid.");
                    return (colText, colRows, pageRotation != 0 ? $"Vector (Rotated {pageRotation:0}°)" : "Vector (Column-Grid Fallback)");
                }
            }

            notes.Add($"Cross-check: Bordered Grid verified ({Math.Min(100, (int)Math.Round(coverage * 100))}% word coverage).");
            return (gridText, gridRows, pageRotation != 0 ? $"Vector Grid (Rotated {pageRotation:0}°)" : "Vector (Bordered Grid)");
        }

        if (TryExtractSplitZones(page, streamWords, out var zoneText, out var zoneRows, forcedSplitX))
        {
            int zoneWords = CountWords(zoneText);
            double coverage = zoneWords / (double)streamWordCount;
            if (coverage < 0.94)
            {
                var (colText, colRows) = ExtractColumnAlignedGrid(streamWords);
                if (CountWords(colText) > zoneWords)
                {
                    notes.Add($"Cross-check: Multi-Zone covered {coverage:P0}; fell back to Column-Grid.");
                    return (colText, colRows, pageRotation != 0 ? $"Vector (Rotated {pageRotation:0}°)" : "Vector (Column-Grid Fallback)");
                }
            }

            notes.Add("Detected left/right page zones; cross-checked word coverage.");
            return (zoneText, zoneRows, pageRotation != 0 ? $"Vector Multi-Zone (Rotated {pageRotation:0}°)" : "Vector (Multi-Zone Grid)");
        }

        var (alignedText, tableRows) = ExtractColumnAlignedGrid(streamWords);
        var modeLabel = pageRotation != 0 ? $"Vector (Rotated {pageRotation:0}°)" : "Vector (Column-Grid)";
        return (alignedText, tableRows, modeLabel);
    }

    private static bool TryExtractMultiToolZones(
        Page page,
        List<WordBox> allWords,
        PageLayoutPlan plan,
        out string combinedText,
        out List<List<string>> combinedRows,
        out string toolsSummary)
    {
        combinedText = string.Empty;
        combinedRows = new List<List<string>>();
        toolsSummary = string.Empty;

        var claimed = new HashSet<WordBox>();
        var usedTools = new List<string>();
        var sb = new StringBuilder();

        var orderedZones = plan.Zones
            .OrderBy(z => z.YTopPct)
            .ThenBy(z => z.XLeftPct)
            .ToList();

        foreach (var zone in orderedZones)
        {
            double xMin = page.Width * zone.XLeftPct - 4.0;
            double xMax = page.Width * zone.XRightPct + 4.0;
            double yTop = page.Height * (1.0 - zone.YTopPct) + 4.0;
            double yBot = page.Height * (1.0 - zone.YBottomPct) - 4.0;

            var zoneWords = allWords
                .Where(w => !claimed.Contains(w))
                .Where(w =>
                {
                    double cx = (w.Left + w.Right) / 2.0;
                    double cy = (w.Bottom + w.Top) / 2.0;
                    return cx >= xMin && cx <= xMax && cy >= yBot && cy <= yTop;
                })
                .ToList();

            if (zoneWords.Count == 0) continue;
            foreach (var w in zoneWords) claimed.Add(w);

            string zoneText;
            List<List<string>> zoneRows;
            string toolLabel;

            if (zone.Tool == "bordered" && TryExtractDrawnGridTable(page, zoneWords, out var bText, out var bRows))
            {
                zoneText = bText;
                zoneRows = bRows;
                toolLabel = "Bordered";
            }
            else if (zone.Tool == "prose")
            {
                (zoneText, zoneRows) = ExtractProseRows(zoneWords);
                toolLabel = "Prose";
            }
            else
            {
                (zoneText, zoneRows) = ExtractColumnAlignedGrid(zoneWords);
                toolLabel = "Column-Grid";
            }

            if (zoneRows.Count == 0) continue;
            if (!usedTools.Contains(toolLabel)) usedTools.Add(toolLabel);

            combinedRows.Add(new List<string> { $"[{zone.Name} - {toolLabel}]" });
            combinedRows.AddRange(zoneRows);
            sb.AppendLine($"[{zone.Name} - {toolLabel}]");
            sb.AppendLine(zoneText);
            sb.AppendLine();
        }

        var unclaimed = allWords.Where(w => !claimed.Contains(w)).ToList();
        if (unclaimed.Count >= 5)
        {
            var (extraText, extraRows) = ExtractColumnAlignedGrid(unclaimed);
            if (extraRows.Count > 0)
            {
                combinedRows.Add(new List<string> { "[Additional Text - Column-Grid]" });
                combinedRows.AddRange(extraRows);
                sb.AppendLine("[Additional Text - Column-Grid]");
                sb.AppendLine(extraText);
            }
        }

        if (combinedRows.Count == 0) return false;

        int maxCols = combinedRows.Max(r => r.Count);
        foreach (var row in combinedRows)
        {
            while (row.Count < maxCols) row.Add(string.Empty);
        }

        toolsSummary = string.Join(" + ", usedTools);
        combinedText = sb.ToString().Trim();
        return true;
    }

    private static (string Text, List<List<string>> Rows) ExtractProseRows(List<WordBox> words)
    {
        if (words.Count == 0) return (string.Empty, new List<List<string>>());

        var heights = words.Select(w => w.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        double medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 9.0;
        double yTol = Math.Clamp(medianHeight * 0.45, 1.5, 4.0);

        var lines = new List<List<WordBox>>();
        foreach (var w in words.OrderByDescending(w => (w.Bottom + w.Top) / 2.0))
        {
            double cy = (w.Bottom + w.Top) / 2.0;
            var row = lines.FirstOrDefault(r => Math.Abs(((r[0].Bottom + r[0].Top) / 2.0) - cy) <= yTol);
            if (row != null) row.Add(w);
            else lines.Add(new List<WordBox> { w });
        }

        var rows = new List<List<string>>();
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var text = NormalizeSpaces(string.Join(" ", line.OrderBy(w => w.Left).Select(w => w.Text)));
            if (string.IsNullOrWhiteSpace(text)) continue;
            rows.Add(new List<string> { text });
            sb.AppendLine(text);
        }
        return (sb.ToString().Trim(), rows);
    }

    /// <summary>
    /// Inspects both top-level PdfPath rectangles and individual PdfSubpath rectangles
    /// to detect drawn horizontal/vertical table borders even inside compound grid paths.
    /// </summary>
    private static bool TryExtractDrawnGridTable(
        Page page,
        List<WordBox> words,
        out string alignedText,
        out List<List<string>> tableRows)
    {
        alignedText = string.Empty;
        tableRows = new List<List<string>>();

        if (words.Count == 0 || page.Paths == null || page.Paths.Count == 0)
        {
            return false;
        }

        var hYs = new List<double>();
        var vXs = new List<double>();

        void InspectRect(PdfRectangle r)
        {
            if (r.Height <= 2.5 && r.Width >= page.Width * 0.25)
            {
                hYs.Add((r.Bottom + r.Top) / 2.0);
            }
            else if (r.Width <= 2.5 && r.Height >= page.Height * 0.12)
            {
                vXs.Add((r.Left + r.Right) / 2.0);
            }
        }

        foreach (var path in page.Paths)
        {
            var pathRect = path.GetBoundingRectangle();
            if (pathRect != null)
            {
                InspectRect(pathRect.Value);
            }

            foreach (var subpath in path)
            {
                var subRect = subpath.GetBoundingRectangle();
                if (subRect != null)
                {
                    InspectRect(subRect.Value);
                }
            }
        }

        var rowLines = ClusterCoordinates(hYs, tolerance: 2.5).OrderByDescending(y => y).ToList();
        var colLines = ClusterCoordinates(vXs, tolerance: 2.5).OrderBy(x => x).ToList();

        if (rowLines.Count < 5 || colLines.Count < 5)
        {
            return false;
        }

        double tableTop = rowLines[0];
        double tableBottom = rowLines[^1];
        double tableLeft = colLines[0];
        double tableRight = colLines[^1];

        var allRows = new List<List<string>>();

        var aboveWords = words.Where(w => w.Bottom > tableTop + 2.0).ToList();
        if (aboveWords.Count > 0)
        {
            allRows.AddRange(MapBandWordsToTableColumns(aboveWords, colLines));
        }

        var tableWords = words.Where(w =>
        {
            double cy = (w.Bottom + w.Top) / 2.0;
            double cx = (w.Left + w.Right) / 2.0;
            return cy <= tableTop + 2.0 && cy >= tableBottom - 2.0 &&
                   cx >= tableLeft - 4.0 && cx <= tableRight + 4.0;
        }).ToList();

        int numCols = colLines.Count - 1;
        for (int r = 0; r < rowLines.Count - 1; r++)
        {
            double yTop = rowLines[r];
            double yBot = rowLines[r + 1];
            if (yTop - yBot < 2.0) continue;

            var rowWords = tableWords.Where(w =>
            {
                double cy = (w.Bottom + w.Top) / 2.0;
                return cy <= yTop + 1.0 && cy >= yBot - 1.0;
            }).ToList();

            if (rowWords.Count == 0) continue;

            var cells = new List<string>(numCols);
            for (int c = 0; c < numCols; c++)
            {
                double xLeft = colLines[c];
                double xRight = colLines[c + 1];

                var cellWords = rowWords.Where(w =>
                {
                    double cx = (w.Left + w.Right) / 2.0;
                    return cx >= xLeft - 1.0 && cx <= xRight + 1.0;
                }).ToList();

                cells.Add(cellWords.Count == 0 ? string.Empty : FormatCellWordsTopToBottom(cellWords));
            }

            if (cells.Any(s => !string.IsNullOrWhiteSpace(s)))
            {
                allRows.Add(cells);
            }
        }

        var belowWords = words.Where(w => w.Top < tableBottom - 2.0).ToList();
        if (belowWords.Count > 0)
        {
            allRows.AddRange(MapBandWordsToTableColumns(belowWords, colLines));
        }

        var sideWords = words.Where(w =>
        {
            double cy = (w.Bottom + w.Top) / 2.0;
            double cx = (w.Left + w.Right) / 2.0;
            return cy <= tableTop + 2.0 && cy >= tableBottom - 2.0 &&
                   (cx < tableLeft - 4.0 || cx > tableRight + 4.0);
        }).ToList();
        if (sideWords.Count > 0)
        {
            allRows.AddRange(MapBandWordsToTableColumns(sideWords, colLines));
        }

        if (allRows.Count == 0)
        {
            return false;
        }

        int maxCols = allRows.Max(r => r.Count);
        foreach (var row in allRows)
        {
            while (row.Count < maxCols) row.Add(string.Empty);
        }

        tableRows = allRows;
        var sb = new StringBuilder();
        foreach (var row in tableRows)
        {
            int lastNonEmpty = row.FindLastIndex(c => !string.IsNullOrWhiteSpace(c));
            if (lastNonEmpty < 0) continue;
            sb.AppendLine(string.Join(" | ", row.Take(lastNonEmpty + 1)));
        }
        alignedText = sb.ToString().Trim();
        return true;
    }

    private static List<List<string>> MapBandWordsToTableColumns(List<WordBox> bandWords, List<double> colLines)
    {
        var result = new List<List<string>>();
        int numCols = colLines.Count - 1;
        if (bandWords.Count == 0 || numCols <= 0) return result;

        var heights = bandWords.Select(w => w.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        double medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 8.0;
        double yTolerance = Math.Clamp(medianHeight * 0.40, 1.2, 3.0);

        var rawRows = new List<List<WordBox>>();
        foreach (var word in bandWords.OrderByDescending(w => (w.Bottom + w.Top) / 2.0))
        {
            double wordCenterY = (word.Bottom + word.Top) / 2.0;
            var matchingRow = rawRows.FirstOrDefault(r => Math.Abs(((r[0].Bottom + r[0].Top) / 2.0) - wordCenterY) <= yTolerance);
            if (matchingRow != null) matchingRow.Add(word);
            else rawRows.Add(new List<WordBox> { word });
        }

        double maxSameCellGap = Math.Max(4.2, medianHeight * 0.62);

        foreach (var row in rawRows)
        {
            var ordered = row.OrderBy(w => w.Left).ToList();
            var cells = Enumerable.Repeat(string.Empty, numCols).ToList();

            var tokens = new List<CellToken>();
            var currentText = new StringBuilder(ordered[0].Text);
            double cellLeft = ordered[0].Left;
            double cellRight = ordered[0].Right;

            for (int i = 1; i < ordered.Count; i++)
            {
                var prev = ordered[i - 1];
                var curr = ordered[i];
                double gap = curr.Left - prev.Right;

                if (gap >= -1.5 && gap <= maxSameCellGap)
                {
                    currentText.Append(' ').Append(curr.Text);
                    cellRight = Math.Max(cellRight, curr.Right);
                }
                else
                {
                    tokens.Add(new CellToken(cellLeft, cellRight, (cellLeft + cellRight) / 2.0, NormalizeSpaces(currentText.ToString())));
                    currentText.Clear();
                    currentText.Append(curr.Text);
                    cellLeft = curr.Left;
                    cellRight = curr.Right;
                }
            }
            tokens.Add(new CellToken(cellLeft, cellRight, (cellLeft + cellRight) / 2.0, NormalizeSpaces(currentText.ToString())));

            foreach (var token in tokens)
            {
                int colIdx = numCols - 1;
                if (token.Left <= colLines[0])
                {
                    colIdx = 0;
                }
                else
                {
                    for (int c = 0; c < numCols; c++)
                    {
                        if (token.Left >= colLines[c] - 2.0 && token.Left < colLines[c + 1])
                        {
                            colIdx = c;
                            break;
                        }
                    }
                }

                cells[colIdx] = string.IsNullOrEmpty(cells[colIdx])
                    ? token.Text
                    : NormalizeSpaces($"{cells[colIdx]} {token.Text}");
            }

            if (cells.Any(s => !string.IsNullOrWhiteSpace(s)))
            {
                result.Add(cells);
            }
        }

        return result;
    }

    private static string FormatCellWordsTopToBottom(List<WordBox> cellWords)
    {
        if (cellWords.Count == 0) return string.Empty;

        var heights = cellWords.Select(w => w.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        double medianH = heights.Count > 0 ? heights[heights.Count / 2] : 6.0;
        double lineTol = Math.Clamp(medianH * 0.5, 1.0, 4.0);

        var lines = new List<List<WordBox>>();
        foreach (var w in cellWords.OrderByDescending(w => w.Bottom))
        {
            var row = lines.FirstOrDefault(r => Math.Abs(r[0].Bottom - w.Bottom) <= lineTol);
            if (row != null) row.Add(w);
            else lines.Add(new List<WordBox> { w });
        }

        var lineTexts = lines.Select(line =>
            NormalizeSpaces(string.Join(" ", line.OrderBy(w => w.Left).Select(w => w.Text))));

        return NormalizeSpaces(string.Join(" ", lineTexts));
    }

    private static List<double> ClusterCoordinates(List<double> coords, double tolerance)
    {
        var result = new List<double>();
        if (coords.Count == 0) return result;

        var sorted = coords.OrderBy(x => x).ToList();
        double sum = sorted[0];
        int count = 1;
        double anchor = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - anchor <= tolerance)
            {
                sum += sorted[i];
                count++;
            }
            else
            {
                result.Add(sum / count);
                anchor = sorted[i];
                sum = sorted[i];
                count = 1;
            }
        }

        result.Add(sum / count);
        return result;
    }

    private static bool TryExtractSplitZones(
        Page page,
        List<WordBox> words,
        out string combinedText,
        out List<List<string>> combinedRows,
        double? forcedSplitX = null)
    {
        double pageWidth = page.Width;
        double pageHeight = page.Height;
        combinedText = string.Empty;
        combinedRows = new List<List<string>>();

        if (words.Count < 16 || pageWidth <= 0 || pageHeight <= 0)
        {
            return false;
        }

        double headerCutY = pageHeight * 0.91;
        double footerCutY = pageHeight * 0.07;

        var bodyWords = words.Where(w => w.Bottom < headerCutY && w.Top > footerCutY).ToList();
        if (bodyWords.Count < 12)
        {
            return false;
        }

        double bestGutterX = forcedSplitX ?? -1;
        bool hasTallVerticalDivider = false;

        if (bestGutterX < 0 && page.Paths != null)
        {
            foreach (var path in page.Paths)
            {
                var r = path.GetBoundingRectangle();
                if (r != null && r.Value.Width <= 3.0 && r.Value.Height >= pageHeight * 0.45)
                {
                    double vx = (r.Value.Left + r.Value.Right) / 2.0;
                    if (vx >= pageWidth * 0.40 && vx <= pageWidth * 0.60 &&
                        bodyWords.Count(w => w.Left < vx && w.Right > vx) <= 2)
                    {
                        bestGutterX = vx;
                        hasTallVerticalDivider = true;
                        break;
                    }
                }
            }
        }

        if (bestGutterX < 0)
        {
            var heights = bodyWords.Select(w => w.Height).Where(h => h > 0).OrderBy(h => h).ToList();
            double medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 8.0;
            double minGutterWidth = Math.Max(28.0, medianHeight * 3.5);
            double bestGutterWidth = 0;

            for (double x = pageWidth * 0.25; x <= pageWidth * 0.75; x += 4.0)
            {
                var leftMax = bodyWords.Where(w => w.Right <= x).Select(w => w.Right).DefaultIfEmpty(0).Max();
                var rightMin = bodyWords.Where(w => w.Left >= x).Select(w => w.Left).DefaultIfEmpty(pageWidth).Min();
                var crossing = bodyWords.Count(w => w.Left < x && w.Right > x);

                if (crossing == 0)
                {
                    double gap = rightMin - leftMax;
                    if (gap >= minGutterWidth && gap > bestGutterWidth)
                    {
                        bestGutterWidth = gap;
                        bestGutterX = (leftMax + rightMin) / 2.0;
                    }
                }
            }
        }

        if (bestGutterX < 0)
        {
            return false;
        }

        // Only avoid splitting if a horizontal table border line crosses the gutter WITHOUT a tall vertical panel divider
        if (!hasTallVerticalDivider && page.Paths != null)
        {
            foreach (var path in page.Paths)
            {
                var r = path.GetBoundingRectangle();
                if (r != null && r.Value.Height <= 3.0 &&
                    r.Value.Bottom < headerCutY && r.Value.Top > footerCutY &&
                    r.Value.Left < bestGutterX - 10 && r.Value.Right > bestGutterX + 10)
                {
                    return false;
                }
            }
        }

        var leftWords = bodyWords.Where(w => (w.Left + w.Right) / 2.0 <= bestGutterX).ToList();
        var rightWords = bodyWords.Where(w => (w.Left + w.Right) / 2.0 > bestGutterX).ToList();

        if (leftWords.Count < 5 || rightWords.Count < 5)
        {
            return false;
        }

        var headerWords = words.Where(w => w.Bottom >= headerCutY).ToList();
        var footerWords = words.Where(w => w.Top <= footerCutY).ToList();

        var sb = new StringBuilder();

        var zones = new (string? Label, List<WordBox> Words)[]
        {
            (null, headerWords),
            ("Left Zone", leftWords),
            ("Right Zone", rightWords),
            (null, footerWords)
        };

        foreach (var (label, zoneWords) in zones)
        {
            if (zoneWords.Count == 0) continue;

            var (text, rows) = ExtractColumnAlignedGrid(zoneWords);
            if (rows.Count == 0) continue;

            if (!string.IsNullOrWhiteSpace(label))
            {
                combinedRows.Add(new List<string> { $"[{label}]" });
                sb.AppendLine($"[{label}]");
            }

            combinedRows.AddRange(rows);
            sb.AppendLine(text);
            sb.AppendLine();
        }

        combinedText = sb.ToString().Trim();
        return combinedRows.Count > 0;
    }

    private async Task<PdfPageContent> ExtractScannedPageAsync(
        string pdfFilePath,
        Page page,
        List<WordBox> fallbackWords,
        double pageRotation,
        CancellationToken cancellationToken)
    {
        var ocrResult = await _ocrService.ExtractTextFromPdfPageAsync(pdfFilePath, page.Number - 1, cancellationToken);

        if (ocrResult.Success && !string.IsNullOrWhiteSpace(ocrResult.Text))
        {
            var rawOcr = ocrResult.Text;
            var (reconstructed, tableRows) = ReconstructOcrLayout(rawOcr);

            return new PdfPageContent
            {
                PageNumber = page.Number,
                ExtractionType = "OCR",
                WordCount = CountWords(reconstructed),
                Content = reconstructed,
                TableRows = tableRows,
                RawOcrText = rawOcr,
                OcrConfidence = ocrResult.Confidence,
                SelectedPsm = ocrResult.SelectedPsm,
                RotationDegrees = ocrResult.AppliedRotationDegrees ?? pageRotation,
                ExtractionNotes = ocrResult.Message
            };
        }

        var sparseVectorText = NormalizeSpaces(string.Join(" ", fallbackWords.Select(w => w.Text)));
        var bestFallback = !string.IsNullOrWhiteSpace(ocrResult.RawFallbackText)
            ? ocrResult.RawFallbackText
            : sparseVectorText;

        var tableFallback = string.IsNullOrWhiteSpace(bestFallback)
            ? new List<List<string>>()
            : new List<List<string>> { new() { bestFallback } };

        return new PdfPageContent
        {
            PageNumber = page.Number,
            ExtractionType = "Scanned (No OCR)",
            WordCount = CountWords(bestFallback),
            Content = bestFallback,
            TableRows = tableFallback,
            RawOcrText = ocrResult.RawFallbackText,
            OcrConfidence = ocrResult.Confidence,
            SelectedPsm = ocrResult.SelectedPsm,
            RotationDegrees = ocrResult.AppliedRotationDegrees ?? pageRotation,
            ExtractionNotes = ocrResult.Message
        };
    }

    private static (string ReconstructedText, List<List<string>> TableRows) ReconstructOcrLayout(string rawOcrText)
    {
        var lines = rawOcrText
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var tableRows = new List<List<string>>();
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            var cells = Regex.Split(line.Trim(), @"\s{2,}|\t+")
                .Select(NormalizeSpaces)
                .Where(c => !string.IsNullOrEmpty(c))
                .ToList();

            if (cells.Count == 0) continue;
            tableRows.Add(cells);
            sb.AppendLine(string.Join(" | ", cells));
        }

        return (sb.ToString().Trim(), tableRows);
    }

    private static int CountWords(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static (string AlignedText, List<List<string>> TableRows) ExtractColumnAlignedGrid(List<WordBox> words)
    {
        if (words.Count == 0)
        {
            return (string.Empty, new List<List<string>>());
        }

        var heights = words.Select(w => w.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        double medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 8.0;
        double yTolerance = Math.Clamp(medianHeight * 0.48, 1.5, 3.6);

        var sortedByY = words.OrderByDescending(w => (w.Bottom + w.Top) / 2.0).ToList();
        var rawRows = new List<List<WordBox>>();

        foreach (var word in sortedByY)
        {
            double wordCenterY = (word.Bottom + word.Top) / 2.0;
            var matchingRow = rawRows.FirstOrDefault(r =>
                Math.Abs(((r[0].Bottom + r[0].Top) / 2.0) - wordCenterY) <= yTolerance);

            if (matchingRow != null)
            {
                matchingRow.Add(word);
            }
            else
            {
                rawRows.Add(new List<WordBox> { word });
            }
        }

        double maxSameCellGap = Math.Max(4.2, medianHeight * 0.62);
        var rowTokens = new List<List<CellToken>>();
        var rawRowYs = new List<double>();
        foreach (var row in rawRows)
        {
            rawRowYs.Add(row.Average(w => (w.Bottom + w.Top) / 2.0));
            var ordered = row.OrderBy(w => w.Left).ToList();
            var cells = new List<CellToken>();

            var currentText = new StringBuilder(ordered[0].Text);
            double cellLeft = ordered[0].Left;
            double cellRight = ordered[0].Right;

            for (int i = 1; i < ordered.Count; i++)
            {
                var prev = ordered[i - 1];
                var curr = ordered[i];
                double gap = curr.Left - prev.Right;

                if (gap >= -1.5 && gap <= maxSameCellGap)
                {
                    currentText.Append(' ').Append(curr.Text);
                    cellRight = Math.Max(cellRight, curr.Right);
                }
                else
                {
                    cells.Add(new CellToken(cellLeft, cellRight, (cellLeft + cellRight) / 2.0, NormalizeSpaces(currentText.ToString())));
                    currentText.Clear();
                    currentText.Append(curr.Text);
                    cellLeft = curr.Left;
                    cellRight = curr.Right;
                }
            }

            cells.Add(new CellToken(cellLeft, cellRight, (cellLeft + cellRight) / 2.0, NormalizeSpaces(currentText.ToString())));
            if (cells.Count > 0)
            {
                rowTokens.Add(cells);
            }
        }

        static bool IsProseNoteRow(List<CellToken> rowTokensOnLine, double medH)
        {
            if (rowTokensOnLine.Count < 2) return false;
            var allWords = rowTokensOnLine.SelectMany(t => t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
            if (allWords.Count < 4) return false;

            int lowerWords = allWords.Count(w => Regex.IsMatch(w, @"^[a-z]{3,}[.,;:)]*$"));
            bool startsLikeBulletOrNote = rowTokensOnLine[0].Text.StartsWith("-") ||
                                          rowTokensOnLine[0].Text.StartsWith("(") ||
                                          rowTokensOnLine[0].Text.StartsWith("Notes", StringComparison.OrdinalIgnoreCase);

            if (lowerWords < 3 && !(startsLikeBulletOrNote && lowerWords >= 2))
                return false;

            for (int i = 1; i < rowTokensOnLine.Count; i++)
            {
                if (rowTokensOnLine[i].Left - rowTokensOnLine[i - 1].Right > medH * 2.8)
                    return false;
            }
            return true;
        }

        var isProseRowFlags = rowTokens.Select(r => IsProseNoteRow(r, medianHeight)).ToList();
        var tableOnlyTokens = rowTokens
            .Where((_, idx) => !isProseRowFlags[idx])
            .SelectMany(r => r)
            .ToList();
        if (tableOnlyTokens.Count == 0)
        {
            tableOnlyTokens = rowTokens.SelectMany(r => r).ToList();
        }

        var allCells = tableOnlyTokens.OrderBy(c => c.Right - c.Left).ToList();
        var leftAnchors = ClusterCoordinates(allCells.Select(c => c.Left).ToList(), tolerance: 4.0)
            .Where(ax => allCells.Count(c => Math.Abs(c.Left - ax) <= 4.0) >= 3)
            .ToList();

        var columns = new List<ColumnCluster>();

        foreach (var cell in allCells)
        {
            bool cellOnOwnLeftAnchor = leftAnchors.Any(ax => Math.Abs(cell.Left - ax) <= 5.0);

            var match = columns
                .Select(col => new
                {
                    Col = col,
                    LeftDist = Math.Abs(col.MinLeft - cell.Left),
                    CenterDist = Math.Abs(col.CenterX - cell.CenterX)
                })
                .Where(x =>
                {
                    if (cellOnOwnLeftAnchor && (cell.Left - x.Col.MinLeft) > medianHeight * 2.0)
                    {
                        return false;
                    }
                    return x.LeftDist <= medianHeight * 1.4 || x.CenterDist <= medianHeight * 1.2;
                })
                .OrderBy(x => x.LeftDist <= medianHeight * 1.4 ? x.LeftDist : x.CenterDist + 100)
                .FirstOrDefault();

            if (match != null)
            {
                if ((cell.Right - cell.Left) <= (match.Col.MaxRight - match.Col.MinLeft) * 1.8 &&
                    (cell.Left - match.Col.MinLeft) <= medianHeight * 1.8)
                {
                    match.Col.MinLeft = Math.Min(match.Col.MinLeft, cell.Left);
                    match.Col.MaxRight = Math.Max(match.Col.MaxRight, cell.Right);
                }
            }
            else
            {
                columns.Add(new ColumnCluster { MinLeft = cell.Left, MaxRight = cell.Right });
            }
        }

        columns = columns.OrderBy(c => c.MinLeft).ToList();

        var tableRows = new List<List<string>>();
        var rowYs = new List<double>();
        for (int rIdx = 0; rIdx < rowTokens.Count; rIdx++)
        {
            var row = rowTokens[rIdx];
            var gridRow = Enumerable.Repeat(string.Empty, Math.Max(1, columns.Count)).ToList();

            if (isProseRowFlags[rIdx])
            {
                gridRow[0] = NormalizeSpaces(string.Join(" ", row.Select(c => c.Text)));
            }
            else
            {
                foreach (var cell in row)
                {
                    int bestCol = 0;
                    double bestScore = double.MaxValue;

                    for (int c = 0; c < columns.Count; c++)
                    {
                        var col = columns[c];
                        double leftDist = Math.Abs(col.MinLeft - cell.Left);
                        double overlap = Math.Min(col.MaxRight, cell.Right) - Math.Max(col.MinLeft, cell.Left);
                        double dist = Math.Abs(col.CenterX - cell.CenterX);
                        double score = leftDist <= medianHeight * 1.4
                            ? leftDist - 500
                            : (overlap > 0 ? -overlap : dist + 1000);

                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestCol = c;
                        }
                    }

                    gridRow[bestCol] = string.IsNullOrEmpty(gridRow[bestCol])
                        ? cell.Text
                        : NormalizeSpaces($"{gridRow[bestCol]} {cell.Text}");
                }
            }

            if (gridRow.Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                tableRows.Add(gridRow);
                rowYs.Add(rawRowYs[rIdx]);
            }
        }

        var nonEmptyColIndices = new List<int>();
        for (int c = 0; c < columns.Count; c++)
        {
            if (tableRows.Any(r => !string.IsNullOrWhiteSpace(r[c])))
            {
                nonEmptyColIndices.Add(c);
            }
        }

        var compactRows = tableRows
            .Select(r => nonEmptyColIndices.Select(c => r[c]).ToList())
            .ToList();

        compactRows = MergeNumberedContinuationRows(compactRows, rowYs);

        var textBuilder = new StringBuilder();
        foreach (var row in compactRows)
        {
            int lastNonEmpty = row.FindLastIndex(c => !string.IsNullOrWhiteSpace(c));
            if (lastNonEmpty < 0) continue;

            var slice = row.Take(lastNonEmpty + 1);
            textBuilder.AppendLine(string.Join(" | ", slice));
        }

        return (textBuilder.ToString().Trim(), compactRows);
    }

    /// <summary>
    /// Assigns wrapped continuation lines (even lines slightly above a vertically centered Sr. No)
    /// to the nearest numbered Sr. No row in Y-coordinate, avoiding row-to-row bleeding.
    /// </summary>
    private static List<List<string>> MergeNumberedContinuationRows(List<List<string>> rows, List<double> rowYs)
    {
        if (rows.Count < 6 || rows[0].Count < 4)
        {
            return rows;
        }

        int srCol = -1;
        for (int col = 0; col <= Math.Min(1, rows[0].Count - 1); col++)
        {
            int numericCount = rows.Count(r => Regex.IsMatch(r[col].Trim(), @"^\d{1,4}$"));
            if (numericCount >= 4)
            {
                srCol = col;
                break;
            }
        }

        if (srCol < 0)
        {
            return rows;
        }

        var numberedIndices = new List<int>();
        for (int i = 0; i < rows.Count; i++)
        {
            if (Regex.IsMatch(rows[i][srCol].Trim(), @"^\d{1,4}$"))
            {
                numberedIndices.Add(i);
            }
        }

        if (numberedIndices.Count < 4)
        {
            return rows;
        }

        int firstNumIdx = numberedIndices[0];
        int lastNumIdx = numberedIndices[^1];
        double firstNumY = rowYs[firstNumIdx];
        double lastNumY = rowYs[lastNumIdx];

        // Estimate average row height from consecutive numbered rows
        var diffs = new List<double>();
        for (int k = 1; k < numberedIndices.Count; k++)
        {
            double dy = Math.Abs(rowYs[numberedIndices[k - 1]] - rowYs[numberedIndices[k]]);
            if (dy > 4.0) diffs.Add(dy);
        }
        double medianRowGap = diffs.Count > 0 ? diffs.OrderBy(d => d).ElementAt(diffs.Count / 2) : 20.0;

        // Build target rows for every numbered item
        var mergedMap = numberedIndices.ToDictionary(idx => idx, idx => new List<string>(rows[idx]));
        var result = new List<List<string>>();

        for (int i = 0; i < rows.Count; i++)
        {
            if (mergedMap.ContainsKey(i))
            {
                continue; // Added in order when we reach its position
            }

            string joined = string.Join(" ", rows[i]).Trim();
            bool isFooter = Regex.IsMatch(joined, @"Rev\s*\d|^\d+\s+of\s+\d+$", RegexOptions.IgnoreCase);
            bool isHeaderBand = i < firstNumIdx && (rowYs[i] - firstNumY) > medianRowGap * 0.85;
            bool isTailBand = i > lastNumIdx && ((lastNumY - rowYs[i]) > medianRowGap * 0.85 || isFooter);

            if (!isHeaderBand && !isTailBand && !isFooter)
            {
                // Find the nearest numbered row in Y-coordinate
                int nearestNumIdx = numberedIndices.OrderBy(nIdx => Math.Abs(rowYs[nIdx] - rowYs[i])).First();
                var target = mergedMap[nearestNumIdx];
                for (int c = 0; c < Math.Min(target.Count, rows[i].Count); c++)
                {
                    var val = rows[i][c].Trim();
                    if (!string.IsNullOrEmpty(val))
                    {
                        target[c] = string.IsNullOrEmpty(target[c])
                            ? val
                            : NormalizeSpaces($"{target[c]} {val}");
                    }
                }
            }
        }

        // Reassemble: header rows + merged numbered rows + footer rows
        for (int i = 0; i < firstNumIdx; i++)
        {
            if ((rowYs[i] - firstNumY) > medianRowGap * 0.85)
            {
                result.Add(rows[i]);
            }
        }
        foreach (var nIdx in numberedIndices)
        {
            result.Add(mergedMap[nIdx]);
        }
        for (int i = lastNumIdx + 1; i < rows.Count; i++)
        {
            string joined = string.Join(" ", rows[i]).Trim();
            bool isFooter = Regex.IsMatch(joined, @"Rev\s*\d|^\d+\s+of\s+\d+$", RegexOptions.IgnoreCase);
            if ((lastNumY - rowYs[i]) > medianRowGap * 0.85 || isFooter)
            {
                result.Add(rows[i]);
            }
        }

        return result;
    }
}
