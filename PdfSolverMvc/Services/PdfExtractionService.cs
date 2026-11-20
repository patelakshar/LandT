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

public class PdfExtractionService : IPdfExtractionService
{
    private readonly IOcrService _ocrService;
    private readonly int _minSelectableCharsPerPage;
    private readonly ILogger<PdfExtractionService> _logger;

    public PdfExtractionService(
        IOcrService ocrService,
        IConfiguration configuration,
        ILogger<PdfExtractionService> logger)
    {
        _ocrService = ocrService;
        _minSelectableCharsPerPage = Math.Max(1, configuration.GetValue<int?>("Ocr:MinSelectableCharsPerPage") ?? 15);
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
            var fileName = Path.GetFileName(pdfFilePath);
            using var document = PdfDocument.Open(pdfFilePath, new ParsingOptions { UseLenientParsing = true });
            int pageCount = document.NumberOfPages;
            _logger.LogInformation("[PDF] 2. Inspect: Opened '{File}' ({Pages}p) using PdfPig", fileName, pageCount);

            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var streamWords = BuildStreamOrderedWords(page.Letters);
                int selectableChars = streamWords.Sum(w => w.Text.Length);
                double pageRotation = page.Rotation.Value;

                var extractedImages = ExtractPageImages(page, streamWords, imageOutputDirectory);

                PdfPageContent pageContent;

                if (selectableChars >= _minSelectableCharsPerPage)
                {
                    pageContent = ExtractVectorPage(page, streamWords, pageRotation);
                    vectorPages++;
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

                pageContent.Images = extractedImages;
                pages.Add(pageContent);

                string toolUsed;
                string whyReason;
                if (pageContent.ExtractionType.StartsWith("OCR", StringComparison.OrdinalIgnoreCase))
                {
                    toolUsed = $"PDFtoImage + SkiaSharp + Tesseract OCR (Conf={pageContent.OcrConfidence:P0}, PSM={pageContent.SelectedPsm})";
                    whyReason = $"only {selectableChars} selectable chars (<{_minSelectableCharsPerPage}, scanned page)";
                }
                else if (pageContent.ExtractionType.StartsWith("Scanned", StringComparison.OrdinalIgnoreCase))
                {
                    toolUsed = "Tesseract OCR (unavailable)";
                    whyReason = $"only {selectableChars} selectable chars (<{_minSelectableCharsPerPage}, tessdata missing)";
                }
                else
                {
                    toolUsed = $"PdfPig ({pageContent.ExtractionType})";
                    whyReason = pageContent.ExtractionType.Contains("Bordered", StringComparison.OrdinalIgnoreCase)
                        ? $"{selectableChars} chars + solid cell borders (page.Paths)"
                        : pageContent.ExtractionType.Contains("Multi-Zone", StringComparison.OrdinalIgnoreCase)
                            ? $"{selectableChars} chars + left/right gutter gap"
                            : $"{selectableChars} chars, no full borders -> tight Y-row & X-col clustering";
                }

                var imgTool = extractedImages.Count > 0 ? " + PdfPig.GetImages/SkiaSharp" : "";

                _logger.LogInformation(
                    "[PDF] 3. p{Page}: Tool={Tool}{ImgTool} | Why={Why} | {Rows} rows, {Words} words, {Images} imgs",
                    pageContent.PageNumber, toolUsed, imgTool, whyReason, pageContent.TableRows.Count, pageContent.WordCount, extractedImages.Count);

                fullTextBuilder.AppendLine($"--- Page {pageContent.PageNumber} ({pageContent.ExtractionType}) ---");
                fullTextBuilder.AppendLine(pageContent.Content);
                fullTextBuilder.AppendLine();
            }

            string method = (vectorPages, ocrPages, emptyScannedPages) switch
            {
                (> 0, 0, 0) => "DirectText",
                (0, > 0, _) => "OcrFallback",
                (> 0, > 0, _) => "HybridOcr",
                (_, 0, > 0) => "NeedsOcr",
                _ => "DirectText"
            };

            string status = emptyScannedPages > 0 && vectorPages == 0 && ocrPages == 0
                ? "PartialOcrNeeded"
                : "Processed";

            _logger.LogInformation("[PDF]    Summary: Method={Method}, Status={Status}", method, status);

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

            bool sameWord = dy <= charHeight * 0.45 &&
                            dx >= -avgCharWidth * 0.45 &&
                            dx <= avgCharWidth * 0.65;

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

    private static PdfPageContent ExtractVectorPage(Page page, List<WordBox> streamWords, double pageRotation)
    {
        string alignedText;
        List<List<string>> tableRows;
        string modeLabel;
        string? modeNote = null;

        if (TryExtractDrawnGridTable(page, streamWords, out var gridText, out var gridRows))
        {
            alignedText = gridText;
            tableRows = gridRows;
            modeLabel = pageRotation != 0 ? $"Vector Grid (Rotated {pageRotation:0}°)" : "Vector (Bordered Grid)";
        }
        else if (TryExtractSplitZones(page, streamWords, out var zoneText, out var zoneRows))
        {
            alignedText = zoneText;
            tableRows = zoneRows;
            modeLabel = pageRotation != 0 ? $"Vector Multi-Zone (Rotated {pageRotation:0}°)" : "Vector (Multi-Zone Grid)";
            modeNote = "Detected left/right page zones; extracted independently.";
        }
        else
        {
            (alignedText, tableRows) = ExtractColumnAlignedGrid(streamWords);
            modeLabel = pageRotation != 0 ? $"Vector (Rotated {pageRotation:0}°)" : "Vector (Column-Grid)";
        }

        var nnWords = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(nnWords);
        var orderedBlocks = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
        var readingOrderText = string.Join(
            Environment.NewLine + Environment.NewLine,
            orderedBlocks.Select(b => NormalizeSpaces(b.Text)).Where(t => !string.IsNullOrWhiteSpace(t)));

        var finalText = !string.IsNullOrWhiteSpace(alignedText) ? alignedText : readingOrderText;
        var wordCount = CountWords(finalText);

        var notes = new List<string>();
        if (pageRotation != 0) notes.Add($"Vector page rotated {pageRotation:0}°; coordinates normalized.");
        if (!string.IsNullOrWhiteSpace(modeNote)) notes.Add(modeNote);

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
        double yTolerance = Math.Clamp(medianHeight * 0.30, 0.8, 2.5);

        var rawRows = new List<List<WordBox>>();
        foreach (var word in bandWords.OrderByDescending(w => w.Bottom))
        {
            var matchingRow = rawRows.FirstOrDefault(r => Math.Abs(r[0].Bottom - word.Bottom) <= yTolerance);
            if (matchingRow != null) matchingRow.Add(word);
            else rawRows.Add(new List<WordBox> { word });
        }

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
                double avgCharWidth = Math.Max(2.2, prev.Width / Math.Max(1, prev.Text.Length));

                if (gap >= -1.5 && gap <= Math.Max(4.5, avgCharWidth * 1.9))
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
        out List<List<string>> combinedRows)
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

        var heights = bodyWords.Select(w => w.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        double medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 8.0;
        double minGutterWidth = Math.Max(28.0, medianHeight * 3.5);

        double bestGutterX = -1;
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

        if (bestGutterX < 0)
        {
            return false;
        }

        // Only avoid splitting if a horizontal table border line crosses the gutter (single wide table)
        if (page.Paths != null)
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

        var leftWords = bodyWords.Where(w => w.Right <= bestGutterX).ToList();
        var rightWords = bodyWords.Where(w => w.Left >= bestGutterX).ToList();

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
        double yTolerance = Math.Clamp(medianHeight * 0.30, 0.8, 2.5);

        var sortedByY = words.OrderByDescending(w => w.Bottom).ToList();
        var rawRows = new List<List<WordBox>>();

        foreach (var word in sortedByY)
        {
            var matchingRow = rawRows.FirstOrDefault(r =>
                Math.Abs(r[0].Bottom - word.Bottom) <= yTolerance);

            if (matchingRow != null)
            {
                matchingRow.Add(word);
            }
            else
            {
                rawRows.Add(new List<WordBox> { word });
            }
        }

        var rowTokens = new List<List<CellToken>>();
        var rawRowYs = new List<double>();
        foreach (var row in rawRows)
        {
            rawRowYs.Add(row.Average(w => w.Bottom));
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
                double avgCharWidth = Math.Max(2.2, prev.Width / Math.Max(1, prev.Text.Length));

                if (gap >= -1.5 && gap <= Math.Max(4.5, avgCharWidth * 1.9))
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

        var allCells = rowTokens.SelectMany(r => r).OrderBy(c => c.Right - c.Left).ToList();
        var columns = new List<ColumnCluster>();

        foreach (var cell in allCells)
        {
            var match = columns
                .Select(col => new
                {
                    Col = col,
                    Overlap = Math.Min(col.MaxRight, cell.Right) - Math.Max(col.MinLeft, cell.Left),
                    CenterDist = Math.Abs(col.CenterX - cell.CenterX)
                })
                .Where(x => x.Overlap > 0 || x.CenterDist <= medianHeight * 1.2)
                .OrderBy(x => x.CenterDist)
                .FirstOrDefault();

            if (match != null)
            {
                if ((cell.Right - cell.Left) <= (match.Col.MaxRight - match.Col.MinLeft) * 1.8)
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
            var gridRow = Enumerable.Repeat(string.Empty, columns.Count).ToList();
            foreach (var cell in row)
            {
                int bestCol = 0;
                double bestScore = double.MaxValue;

                for (int c = 0; c < columns.Count; c++)
                {
                    var col = columns[c];
                    double overlap = Math.Min(col.MaxRight, cell.Right) - Math.Max(col.MinLeft, cell.Left);
                    double dist = Math.Abs(col.CenterX - cell.CenterX);
                    double score = overlap > 0 ? -overlap : dist + 1000;

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
