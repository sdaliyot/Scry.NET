namespace Scry.Wpf;

public enum WpfTreeKind
{
    Visual,
    Logical
}

public sealed class WpfAdapterOptions
{
    public int MaximumDepth { get; init; } = 32;

    public int MaximumNodes { get; init; } = 2048;

    public int MaximumPreviewLength { get; init; } = 256;

    public int MaximumScreenshotWidth { get; init; } = 4096;

    public int MaximumScreenshotHeight { get; init; } = 4096;

    public long MaximumScreenshotPixels { get; init; } = 16_777_216;

    public int MaximumScreenshotBytes { get; init; } = 4 * 1024 * 1024;

    public TimeSpan DefaultWaitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan DefaultPollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}

public sealed record WpfApplicationInfo(
    string Type,
    string ShutdownMode,
    int WindowCount,
    string? MainWindowName,
    string? MainWindowTitle);

public sealed record WpfSnapshot(
    string TreeKind,
    IReadOnlyList<WpfNode> Roots,
    int NodesVisited,
    bool Truncated,
    string Limitation,
    WpfApplicationInfo? Application);

public sealed record WpfNode(
    string Path,
    string RootKind,
    string Type,
    string? Name,
    string? AutomationId,
    string? AutomationName,
    string? Text,
    WpfBounds? Bounds,
    bool? IsVisible,
    bool? IsEnabled,
    bool? IsLoaded,
    bool? IsKeyboardFocusWithin,
    bool? Focusable,
    int? TabIndex,
    double? Opacity,
    WpfDataContext? DataContext,
    IReadOnlyList<WpfBinding> Bindings,
    IReadOnlyList<WpfNode> Children,
    int ProjectedChildCount,
    int ProviderChildCount,
    int NonProjectableChildCount,
    bool ChildEnumerationTruncated,
    bool Truncated);

public sealed record WpfBounds(
    double X,
    double Y,
    double Width,
    double Height);

public sealed record WpfDataContext(
    string Type,
    string Preview,
    bool IsLocallySet);

public sealed record WpfBinding(
    string Property,
    string Kind,
    string? Path,
    string? Mode,
    string? UpdateSourceTrigger,
    string? ElementName,
    string? RelativeSource,
    string? SourceType,
    string Status,
    bool HasValidationError);

public sealed record WpfCondition(
    string? Root = null,
    WpfTreeKind TreeKind = WpfTreeKind.Visual,
    string? Path = null,
    string? Name = null,
    string? AutomationId = null,
    string State = "exists",
    string? Expected = null);

public sealed record WpfWaitResult(
    bool Satisfied,
    bool Conclusive,
    TimeSpan Elapsed,
    WpfNode? Match,
    string Description);

public sealed record WpfScreenshotResult(
    bool Success,
    string Status,
    string? MediaType,
    int? Width,
    int? Height,
    string? Base64Data,
    string? ErrorCode,
    string? Message,
    string Limitation);
