using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSolverMvc.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace PdfSolverMvc.Tests;

public class PdfExtractionTests : IDisposable
{
    private readonly string _tempDir;

    public PdfExtractionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PdfSolverTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private sealed class StubOcrService : IOcrService
    {
        private readonly Func<int, OcrResult> _handler;
        public List<int> InvokedPages { get; } = new();

        public StubOcrService(Func<int, OcrResult> handler) => _handler = handler;

        public bool IsAvailable(out string? statusMessage)
        {
            statusMessage = null;
            return true;
        }

        public IReadOnlyList<string> GetConfiguredAvailableLanguages() => new[] { "eng" };

        public Task<OcrResult> ExtractTextFromPdfPageAsync(
            string pdfFilePath,
            int zeroBasedPageIndex,
            CancellationToken cancellationToken = default)
        {
            InvokedPages.Add(zeroBasedPageIndex);
            return Task.FromResult(_handler(zeroBasedPageIndex));
        }
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PdfSolverMvc.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public StubWebHostEnvironment(string contentRoot) => ContentRootPath = contentRoot;
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?>? overrides = null)
    {
        var defaults = new Dictionary<string, string?>
        {
            ["Ocr:TessDataPath"] = "tessdata",
            ["Ocr:Language"] = "eng",
            ["Ocr:Dpi"] = "300",
            ["Ocr:MinSelectableCharsPerPage"] = "15"
        };

        if (overrides != null)
        {
            foreach (var kv in overrides) defaults[kv.Key] = kv.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(defaults).Build();
    }

    [Fact]
    public async Task VectorPdf_WithSparseTable_PreservesBlankColumnsAndMetadata()
    {
        var pdfPath = Path.Combine(_tempDir, "vector_table.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);

        page.AddText("Item", 10, new PdfPoint(50, 750), font);
        page.AddText("DN15", 10, new PdfPoint(200, 750), font);
        page.AddText("DN20", 10, new PdfPoint(280, 750), font);
        page.AddText("DN25", 10, new PdfPoint(360, 750), font);

        page.AddText("Valve", 10, new PdfPoint(50, 720), font);
        page.AddText("0651", 10, new PdfPoint(360, 720), font);

        File.WriteAllBytes(pdfPath, builder.Build());

        var ocr = new StubOcrService(_ => new OcrResult(false, string.Empty));
        var service = new PdfExtractionService(ocr, BuildConfig(), NullLogger<PdfExtractionService>.Instance);

        var result = await service.ExtractAsync(pdfPath);

        Assert.Equal("DirectText", result.ExtractionMethod);
        Assert.Equal(1, result.PageCount);
        Assert.Empty(ocr.InvokedPages);
        Assert.True(result.Pages[0].TableRows.Count >= 2);
        var sparseRow = result.Pages[0].TableRows[1];
        Assert.Equal("Valve", sparseRow[0]);
        Assert.Equal("0651", sparseRow[^1]);
    }

    [Fact]
    public async Task MixedPdf_RoutesVectorAndScannedPagesSeparately()
    {
        var pdfPath = Path.Combine(_tempDir, "mixed.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        var p1 = builder.AddPage(PageSize.A4);
        p1.AddText("Searchable Engineering Specification Header", 12, new PdfPoint(50, 750), font);

        builder.AddPage(PageSize.A4);

        File.WriteAllBytes(pdfPath, builder.Build());

        var ocr = new StubOcrService(idx => new OcrResult(
            Success: true,
            Text: "Pump Model  SBR200/8\nRated Flow  718.7 m3/h",
            Confidence: 0.87f,
            SelectedPsm: "Auto",
            AppliedRotationDegrees: 0));

        var service = new PdfExtractionService(ocr, BuildConfig(), NullLogger<PdfExtractionService>.Instance);
        var result = await service.ExtractAsync(pdfPath);

        Assert.Equal("HybridOcr", result.ExtractionMethod);
        Assert.Equal(2, result.PageCount);
        Assert.Single(ocr.InvokedPages);
        Assert.Equal(1, ocr.InvokedPages[0]);
        Assert.Equal(0.87f, result.Pages[1].OcrConfidence);
        Assert.Equal("Auto", result.Pages[1].SelectedPsm);
        Assert.Contains("SBR200/8", result.Pages[1].RawOcrText);
    }

    [Fact]
    public async Task LowQualityOrHandwrittenPage_PreservesFallbackTextAndConfidenceNote()
    {
        var pdfPath = Path.Combine(_tempDir, "low_quality.pdf");
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        File.WriteAllBytes(pdfPath, builder.Build());

        var ocr = new StubOcrService(_ => new OcrResult(
            Success: true,
            Text: "Rough handwritten note 104-J/JA",
            Message: "Low OCR confidence (38%); best-effort result.",
            RawFallbackText: "Rough handwritten note 104-J/JA",
            Confidence: 0.38f,
            SelectedPsm: "SparseText",
            AppliedRotationDegrees: 90));

        var service = new PdfExtractionService(ocr, BuildConfig(), NullLogger<PdfExtractionService>.Instance);
        var result = await service.ExtractAsync(pdfPath);

        Assert.Equal("OcrFallback", result.ExtractionMethod);
        Assert.Equal(0.38f, result.Pages[0].OcrConfidence);
        Assert.Equal("SparseText", result.Pages[0].SelectedPsm);
        Assert.Equal(90, result.Pages[0].RotationDegrees);
        Assert.Contains("Low OCR confidence", result.Pages[0].ExtractionNotes);
    }

    [Fact]
    public void OcrService_ResolvesConfiguredAvailableLanguagesOnly()
    {
        var tessDir = Path.Combine(_tempDir, "tessdata");
        Directory.CreateDirectory(tessDir);
        File.WriteAllBytes(Path.Combine(tessDir, "eng.traineddata"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(tessDir, "deu.traineddata"), new byte[] { 1, 2, 3 });

        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Ocr:TessDataPath"] = tessDir,
            ["Ocr:Language"] = "eng",
            ["Ocr:AdditionalLanguages:0"] = "deu",
            ["Ocr:AdditionalLanguages:1"] = "fra"
        });

        var ocr = new OcrService(config, new StubWebHostEnvironment(_tempDir), NullLogger<OcrService>.Instance);
        var langs = ocr.GetConfiguredAvailableLanguages();

        Assert.Equal(new[] { "eng", "deu" }, langs);
        Assert.True(ocr.IsAvailable(out var msg));
        Assert.Null(msg);
    }

    [Fact]
    public async Task MultiLineNumberedRows_MergeContinuationLinesIntoSingleRow()
    {
        var pdfPath = Path.Combine(_tempDir, "multiline_pms.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);

        page.AddText("Sr", 9, new PdfPoint(30, 760), font);
        page.AddText("Class", 9, new PdfPoint(80, 760), font);
        page.AddText("Service", 9, new PdfPoint(160, 760), font);
        page.AddText("Rating", 9, new PdfPoint(320, 760), font);

        // Row 94 with wrapped Service line
        page.AddText("94", 9, new PdfPoint(30, 730), font);
        page.AddText("17320NX", 9, new PdfPoint(80, 730), font);
        page.AddText("COOLING WATER", 9, new PdfPoint(160, 730), font);
        page.AddText("150", 9, new PdfPoint(320, 730), font);
        page.AddText("WASH DOWN", 9, new PdfPoint(160, 718), font);

        // Rows 95-97 so numbered table heuristic activates
        page.AddText("95", 9, new PdfPoint(30, 695), font);
        page.AddText("31008N", 9, new PdfPoint(80, 695), font);
        page.AddText("PROCESS", 9, new PdfPoint(160, 695), font);
        page.AddText("300", 9, new PdfPoint(320, 695), font);

        page.AddText("96", 9, new PdfPoint(30, 675), font);
        page.AddText("11008N", 9, new PdfPoint(80, 675), font);
        page.AddText("CAUSTIC", 9, new PdfPoint(160, 675), font);
        page.AddText("150", 9, new PdfPoint(320, 675), font);

        page.AddText("97", 9, new PdfPoint(30, 655), font);
        page.AddText("11210NX", 9, new PdfPoint(80, 655), font);
        page.AddText("SULPHUR", 9, new PdfPoint(160, 655), font);
        page.AddText("150", 9, new PdfPoint(320, 655), font);

        File.WriteAllBytes(pdfPath, builder.Build());

        var ocr = new StubOcrService(_ => new OcrResult(false, string.Empty));
        var service = new PdfExtractionService(ocr, BuildConfig(), NullLogger<PdfExtractionService>.Instance);

        var result = await service.ExtractAsync(pdfPath);
        var row94 = result.Pages[0].TableRows.FirstOrDefault(r => r[0] == "94");

        Assert.NotNull(row94);
        Assert.Contains("COOLING WATER WASH DOWN", row94!);
    }

    [Fact]
    public async Task SplitZones_SeparatesLeftNotesFromRightMatrix()
    {
        var pdfPath = Path.Combine(_tempDir, "split_zones.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);

        // Left zone (X = 40..170)
        page.AddText("Notes", 10, new PdfPoint(40, 700), font);
        page.AddText("Design limits", 9, new PdfPoint(40, 680), font);
        page.AddText("For bending", 9, new PdfPoint(40, 660), font);
        page.AddText("Table schedules", 9, new PdfPoint(40, 640), font);
        page.AddText("DN 15 160", 9, new PdfPoint(40, 620), font);

        // Right zone (X = 330..500), wide > 150pt vertical gutter
        page.AddText("Branch connections", 10, new PdfPoint(330, 700), font);
        page.AddText("Run 600 N P E", 9, new PdfPoint(330, 680), font);
        page.AddText("Run 500 N P E", 9, new PdfPoint(330, 660), font);
        page.AddText("Code Explanation", 9, new PdfPoint(330, 640), font);
        page.AddText("A Equal tee", 9, new PdfPoint(330, 620), font);

        File.WriteAllBytes(pdfPath, builder.Build());

        var ocr = new StubOcrService(_ => new OcrResult(false, string.Empty));
        var service = new PdfExtractionService(ocr, BuildConfig(), NullLogger<PdfExtractionService>.Instance);

        var result = await service.ExtractAsync(pdfPath);

        Assert.Contains("Left Zone", result.RawExtractedText);
        Assert.Contains("Right Zone", result.RawExtractedText);
    }

    [Fact]
    public async Task HyosungDatasheet_ExtractsAllRowsAndBothPages()
    {
        var pdfPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "hyosung offer_51_1.pdf"));
        if (!File.Exists(pdfPath)) return;

        var ocr = new StubOcrService(_ => new OcrResult(false, string.Empty));
        var service = new PdfExtractionService(ocr, BuildConfig(), NullLogger<PdfExtractionService>.Instance);

        var result = await service.ExtractAsync(pdfPath);

        Assert.Equal(2, result.PageCount);
        Assert.True(result.Pages[0].TableRows.Count >= 60, $"Expected >=60 rows on p1, got {result.Pages[0].TableRows.Count} ({result.Pages[0].ExtractionType})");
        Assert.Contains("3443", result.Pages[0].Content);
        Assert.Contains("3620", result.Pages[0].Content);
        Assert.True(result.Pages[1].TableRows.Count >= 50, $"Expected >=50 rows on p2, got {result.Pages[1].TableRows.Count} ({result.Pages[1].ExtractionType})");
        Assert.Contains("VG", result.Pages[1].Content);
    }

    [Fact]
    public async Task StrikethroughText_IsExcludedFromExtractedOutput()
    {
        var pdfPath = Path.Combine(_tempDir, "strikethrough.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);

        // Normal row
        page.AddText("BLFL", 10, new PdfPoint(40, 740), font);
        page.AddText("Blind flange", 10, new PdfPoint(120, 740), font);
        page.AddText("DN 15-600", 10, new PdfPoint(260, 740), font);
        page.AddText("ASTM A182-F316", 10, new PdfPoint(360, 740), font);

        // Struck-through row (horizontal line through middle of 10pt text at Y ~ 723.5)
        page.AddText("LJFL", 10, new PdfPoint(40, 720), font);
        page.AddText("Lap joint flange", 10, new PdfPoint(120, 720), font);
        page.AddText("ASTM A105N", 10, new PdfPoint(360, 720), font);
        page.DrawLine(new PdfPoint(38, 723.5), new PdfPoint(440, 723.5), 1.0);

        // Partial strikethrough on "350" inside "DN 15-350-200"
        page.AddText("SBNF", 10, new PdfPoint(40, 700), font);
        page.AddText("Spectacle blind", 10, new PdfPoint(120, 700), font);
        page.AddText("DN 15-350 200", 10, new PdfPoint(260, 700), font);
        page.DrawLine(new PdfPoint(290, 703.5), new PdfPoint(312, 703.5), 1.0);

        File.WriteAllBytes(pdfPath, builder.Build());

        var ocr = new StubOcrService(_ => new OcrResult(false, string.Empty));
        var service = new PdfExtractionService(ocr, BuildConfig(), NullLogger<PdfExtractionService>.Instance);

        var result = await service.ExtractAsync(pdfPath);

        Assert.Contains("BLFL", result.RawExtractedText);
        Assert.DoesNotContain("LJFL", result.RawExtractedText);
        Assert.DoesNotContain("ASTM A105N", result.RawExtractedText);
        Assert.Contains("SBNF", result.RawExtractedText);
        Assert.DoesNotContain("350", result.RawExtractedText);
        Assert.Contains("200", result.RawExtractedText);
    }

    [Fact]
    public async Task Pms15_ExcludesSingleCharacterAndRowStrikethroughs()
    {
        var pdfPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "kaa-main", "15_PDFsam_Extracted PMS 2_1.pdf"));
        if (!File.Exists(pdfPath)) return;

        var ocr = new StubOcrService(_ => new OcrResult(false, string.Empty));
        var service = new PdfExtractionService(ocr, BuildConfig(), NullLogger<PdfExtractionService>.Instance);

        var result = await service.ExtractAsync(pdfPath);

        Assert.DoesNotContain("ASTM A105N", result.RawExtractedText);
        Assert.DoesNotContain("TP316L", result.RawExtractedText);
        Assert.Contains("TP316", result.RawExtractedText);
        Assert.DoesNotContain("WP316L", result.RawExtractedText);
        Assert.Contains("WP316", result.RawExtractedText);
        Assert.Contains("33421N", result.RawExtractedText);
    }

    [Fact]
    public async Task Pms4_ExcludesStruckOutRows100To106()
    {
        var pdfPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "kaa-main", "4_PDFsam_Extracted PMS 2_1.pdf"));
        if (!File.Exists(pdfPath)) return;

        var ocr = new StubOcrService(_ => new OcrResult(false, string.Empty));
        var service = new PdfExtractionService(ocr, BuildConfig(), NullLogger<PdfExtractionService>.Instance);

        var result = await service.ExtractAsync(pdfPath);

        Assert.Contains("17320NX", result.RawExtractedText);
        Assert.DoesNotContain("31177NX3", result.RawExtractedText);
        Assert.DoesNotContain("11057NX", result.RawExtractedText);
        Assert.DoesNotContain("17011X", result.RawExtractedText);
    }
}
