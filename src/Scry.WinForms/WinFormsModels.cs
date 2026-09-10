using System.Drawing;

namespace Scry.WinForms;

public sealed class WinFormsAdapterOptions
{
    public int MaximumDepth { get; init; } = 32;

    public int MaximumNodes { get; init; } = 2048;

    public int MaximumScreenshotWidth { get; init; } = 4096;

    public int MaximumScreenshotHeight { get; init; } = 4096;

    public int MaximumScreenshotBytes { get; init; } = 4 * 1024 * 1024;

    public TimeSpan DefaultWaitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan DefaultPollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}

public sealed record WinFormsSnapshot(
    string Projection,
    IReadOnlyList<WinFormsNode> Roots,
    int NodesVisited,
    bool Truncated,
    string Limitation);

public sealed record WinFormsNode(
    string Path,
    string Type,
    string? Name,
    string? Text,
    Rectangle Bounds,
    bool Visible,
    bool Enabled,
    bool IsHandleCreated,
    bool Focused,
    int TabIndex,
    string? AccessibleName,
    string? AccessibleDescription,
    string? AccessibleRole,
    IReadOnlyList<WinFormsBinding> Bindings,
    IReadOnlyList<WinFormsOwnedForm> OwnedForms,
    IReadOnlyList<WinFormsMenuItem> MenuItems,
    IReadOnlyList<WinFormsNode> Children,
    int ProjectedChildCount,
    int ActualChildCount,
    bool Truncated);

public sealed record WinFormsOwnedForm(
    string Type,
    string? Name,
    string? Text,
    bool Visible,
    bool Enabled);

public sealed record WinFormsBinding(
    string Property,
    string DataMember,
    string? DataSourceType,
    bool FormattingEnabled,
    string ControlUpdateMode,
    string DataSourceUpdateMode);

public sealed record WinFormsMenuItem(
    string Path,
    string Type,
    string? Name,
    string Text,
    bool Available,
    bool Enabled,
    bool Selected,
    bool Checked,
    IReadOnlyList<WinFormsMenuItem> Children,
    int ActualChildCount,
    bool Truncated);

public sealed record WinFormsScreenshot(
    string MediaType,
    int Width,
    int Height,
    string Base64Data,
    string Limitation);

public sealed record WinFormsCondition(
    string? Root = null,
    string? Path = null,
    string? Name = null,
    string State = "exists",
    string? Expected = null);

public sealed record WinFormsWaitResult(
    bool Satisfied,
    bool Conclusive,
    TimeSpan Elapsed,
    WinFormsNode? Match,
    string Description);
