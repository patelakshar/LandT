namespace PdfSolverMvc.Services;

public record GeminiExtractionResult(
    bool Success,
    List<List<string>> TableRows,
    string Content,
    string? Summary = null,
    string? Error = null);

public record LayoutZonePlan(
    string Name,
    string Tool,
    double XLeftPct,
    double XRightPct,
    double YTopPct,
    double YBottomPct);

public record PageLayoutPlan(
    bool Success,
    string PageType,
    double? SplitXPct,
    List<LayoutZonePlan> Zones,
    string? Notes = null,
    bool FromCache = false);

public interface IGeminiService
{
    bool IsConfigured { get; }

    Task<PageLayoutPlan> AnalyzePageLayoutAsync(
        string pdfFilePath,
        int zeroBasedPageIndex,
        string layoutFingerprint,
        CancellationToken cancellationToken = default);

    Task<GeminiExtractionResult> ExtractTableFromPageImageAsync(
        string pdfFilePath,
        int zeroBasedPageIndex,
        string reason,
        CancellationToken cancellationToken = default);

    Task<GeminiExtractionResult> RepairTableFromTextAsync(
        string compactPageText,
        string reason,
        CancellationToken cancellationToken = default);
}
