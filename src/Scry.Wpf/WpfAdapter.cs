using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Scry.Contracts;
using Scry.Sdk;

namespace Scry.Wpf;

public static class WpfAgentBuilderExtensions
{
    public static AgentBuilder UseWpf(
        this AgentBuilder builder,
        Dispatcher dispatcher,
        Action<WpfAdapterBuilder>? configure = null,
        WpfAdapterOptions? options = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        var adapterBuilder = new WpfAdapterBuilder(dispatcher, options);
        configure?.Invoke(adapterBuilder);
        Register(builder, adapterBuilder.Build());
        return builder;
    }

    public static AgentBuilder UseWpf(
        this AgentBuilder builder,
        Application application,
        Action<WpfAdapterBuilder>? configure = null,
        WpfAdapterOptions? options = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        var adapterBuilder = new WpfAdapterBuilder(application, options);
        configure?.Invoke(adapterBuilder);
        Register(builder, adapterBuilder.Build());
        return builder;
    }

    private static void Register(AgentBuilder builder, WpfAdapter adapter)
    {
        var readOnlyUiPolicy = new AgentOperationPolicy
        {
            ExecutionPolicy = AgentOperationExecutionPolicy.UiOwner,
            IsReadOnly = true
        };
        builder.RegisterValue("wpf", adapter, "Dispatcher-marshalled WPF inspection service.");

        // Lets evaluate/execute opt into the dispatcher with "marshal": "ui", so a submission can
        // touch DependencyObjects instead of failing VerifyAccess. Double await: InvokeAsync hands
        // back a Task whose result is the submission's own Task.
        builder.UseExecutionMarshaller(async (callback, cancellationToken) =>
            await await adapter.Marshaller.InvokeAsync(callback, cancellationToken).ConfigureAwait(false));
        builder.RegisterOperation(
            "wpf.snapshot",
            async (arguments, cancellationToken) =>
                (object?)JsonSerializer.SerializeToElement(
                    await adapter.SnapshotAsync(arguments, cancellationToken).ConfigureAwait(false),
                    ScryJson.Options),
            "Creates a bounded visual or logical WPF tree projection.",
            readOnlyUiPolicy);
        builder.RegisterOperation(
            "wpf.wait",
            async (arguments, cancellationToken) =>
                (object?)JsonSerializer.SerializeToElement(
                    await adapter.WaitAsync(arguments, cancellationToken).ConfigureAwait(false),
                    ScryJson.Options),
            "Waits for a bounded WPF element-state condition without blocking the dispatcher.",
            readOnlyUiPolicy);
        builder.RegisterOperation(
            "wpf.assert",
            async (arguments, cancellationToken) =>
                (object?)JsonSerializer.SerializeToElement(
                    await adapter.AssertAsync(arguments, cancellationToken).ConfigureAwait(false),
                    ScryJson.Options),
            "Asserts a bounded WPF element-state condition.",
            readOnlyUiPolicy);
        builder.RegisterOperation(
            "wpf.screenshot",
            async (arguments, cancellationToken) =>
                (object?)JsonSerializer.SerializeToElement(
                    await adapter.ScreenshotAsync(arguments, cancellationToken).ConfigureAwait(false),
                    ScryJson.Options),
            "Attempts a bounded RenderTargetBitmap capture and explicitly reports unsupported cases.",
            readOnlyUiPolicy);
    }
}

public sealed class WpfAdapterBuilder
{
    private readonly Dictionary<string, DependencyObject> _roots = new(StringComparer.Ordinal);
    private Application? _application;

    public WpfAdapterBuilder(Dispatcher dispatcher, WpfAdapterOptions? options = null)
    {
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Options = options ?? new WpfAdapterOptions();
    }

    public WpfAdapterBuilder(Application application, WpfAdapterOptions? options = null)
        : this(
            (application ?? throw new ArgumentNullException(nameof(application))).Dispatcher,
            options)
    {
        _application = application;
    }

    internal Dispatcher Dispatcher { get; }

    internal WpfAdapterOptions Options { get; }

    public WpfAdapterBuilder IncludeApplication(Application application)
    {
        if (application is null)
        {
            throw new ArgumentNullException(nameof(application));
        }
        EnsureDispatcher(application.Dispatcher, nameof(application));
        _application = application;
        return this;
    }

    public WpfAdapterBuilder RegisterRoot(string name, DependencyObject root)
    {
        ValidateName(name);
        if (root is null)
        {
            throw new ArgumentNullException(nameof(root));
        }
        if (root is DispatcherObject dispatcherObject)
        {
            EnsureDispatcher(dispatcherObject.Dispatcher, nameof(root));
        }

        if (_roots.ContainsKey(name))
        {
            throw new ArgumentException($"A WPF root named '{name}' is already registered.", nameof(name));
        }

        _roots[name] = root;

        return this;
    }

    public WpfAdapterBuilder RegisterWindow(string name, Window window) =>
        RegisterRoot(name, window);

    public WpfAdapter Build()
    {
        var application = _application;
        if (application is null &&
            Application.Current is { } current &&
            current.Dispatcher == Dispatcher)
        {
            application = current;
        }

        return new(
            Dispatcher,
            application,
            new Dictionary<string, DependencyObject>(_roots, StringComparer.Ordinal),
            Options);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            throw new ArgumentException(
                "Root names must contain 1-128 non-whitespace characters.",
                nameof(name));
        }
    }

    private void EnsureDispatcher(Dispatcher dispatcher, string parameterName)
    {
        if (dispatcher != Dispatcher)
        {
            throw new ArgumentException(
                "The application and all roots must belong to the adapter dispatcher.",
                parameterName);
        }
    }
}

public sealed class WpfAdapter
{
    private const int MaximumProtocolSafeScreenshotBytes = 4 * 1024 * 1024;
    private const string VisualLimitation =
        "This is a bounded WPF visual-tree projection, not a complete UI or HWND tree. " +
        "Logical-only content, closed templates, Popup content, HwndHost internals, and native or " +
        "out-of-process surfaces may be absent even when Truncated is false.";
    private const string LogicalLimitation =
        "This is a bounded WPF logical-tree projection, not a complete visual or automation tree. " +
        "Template-generated visuals, Popup content, non-DependencyObject logical values, HwndHost " +
        "internals, and native or out-of-process surfaces may be absent even when Truncated is false.";
    private const string ScreenshotLimitation =
        "RenderTargetBitmap captures only the selected WPF Visual. Popup and separate HWND content, " +
        "HwndHost/WebView content, protected surfaces, and some hardware-rendered effects may be absent.";

    private readonly WpfDispatcher _dispatcher;
    private readonly Application? _application;
    private readonly IReadOnlyDictionary<string, DependencyObject> _registeredRoots;
    private readonly WpfAdapterOptions _options;

    /// <summary>
    /// The dispatcher every projection is marshalled through, exposed so registration can reuse it
    /// as the endpoint's execution marshaller.
    /// </summary>
    internal WpfDispatcher Marshaller => _dispatcher;

    internal WpfAdapter(
        Dispatcher dispatcher,
        Application? application,
        IReadOnlyDictionary<string, DependencyObject> registeredRoots,
        WpfAdapterOptions options)
    {
        ValidateOptions(options);
        _dispatcher = new(dispatcher);
        _application = application;
        _registeredRoots = registeredRoots;
        _options = options;
    }

    public Task<WpfSnapshot> SnapshotAsync(
        WpfTreeKind treeKind = WpfTreeKind.Visual,
        string? root = null,
        CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(() => CreateSnapshot(treeKind, root), cancellationToken);

    public Task<WpfSnapshot> SnapshotAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var root = OptionalString(arguments, "root");
        var treeKind = ParseTreeKind(OptionalString(arguments, "tree"));
        return SnapshotAsync(treeKind, root, cancellationToken);
    }

    public async ValueTask<object?> WaitAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var condition = ParseCondition(arguments);
        var timeout = OptionalMilliseconds(
            arguments,
            "timeoutMilliseconds",
            _options.DefaultWaitTimeout,
            allowZero: true);
        var pollInterval = OptionalMilliseconds(
            arguments,
            "pollIntervalMilliseconds",
            _options.DefaultPollInterval,
            allowZero: false);
        return await WaitAsync(
            condition,
            timeout,
            pollInterval,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<WpfWaitResult> WaitAsync(
        WpfCondition condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        if (condition is null)
        {
            throw new ArgumentNullException(nameof(condition));
        }
        var selectedTimeout = timeout ?? _options.DefaultWaitTimeout;
        var selectedPoll = pollInterval ?? _options.DefaultPollInterval;
        if (selectedTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        if (selectedPoll <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _dispatcher.InvokeAsync(
                () => Evaluate(condition, CreateSnapshot(condition.TreeKind, condition.Root)),
                cancellationToken).ConfigureAwait(false);
            if (result.Satisfied || stopwatch.Elapsed >= selectedTimeout)
            {
                return result with { Elapsed = stopwatch.Elapsed };
            }

            var remaining = selectedTimeout - stopwatch.Elapsed;
            await Task.Delay(
                remaining < selectedPoll ? remaining : selectedPoll,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<WpfWaitResult> AssertAsync(
        WpfCondition condition,
        CancellationToken cancellationToken = default)
    {
        if (condition is null)
        {
            throw new ArgumentNullException(nameof(condition));
        }
        var result = await _dispatcher.InvokeAsync(
            () => Evaluate(condition, CreateSnapshot(condition.TreeKind, condition.Root)),
            cancellationToken).ConfigureAwait(false);
        if (!result.Satisfied)
        {
            var qualification = result.Conclusive ? string.Empty : " (projection was truncated)";
            throw new InvalidOperationException($"WPF assertion failed{qualification}: {result.Description}");
        }

        return result;
    }

    public async ValueTask<object?> AssertAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        await AssertAsync(ParseCondition(arguments), cancellationToken).ConfigureAwait(false);

    public Task<WpfScreenshotResult> ScreenshotAsync(
        string root,
        string? path = null,
        WpfTreeKind treeKind = WpfTreeKind.Visual,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A root name is required.", nameof(root));
        }

        return _dispatcher.InvokeAsync(
            () => CaptureScreenshot(root, path, treeKind),
            cancellationToken);
    }

    public async ValueTask<object?> ScreenshotAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        // 'root' is optional here, unlike the typed overload above: an injected target registers no
        // roots, so a caller has no way to learn a root name without first taking a snapshot.
        var requestedRoot = OptionalString(arguments, "root");
        var path = OptionalString(arguments, "path");
        var treeKind = ParseTreeKind(OptionalString(arguments, "tree"));

        // Resolving the default has to happen inside the dispatcher, because it reads
        // Application.Windows, which is UI-thread affine like everything else here.
        return await _dispatcher.InvokeAsync(
            () => CaptureScreenshot(requestedRoot ?? DefaultScreenshotRootName(), path, treeKind),
            cancellationToken).ConfigureAwait(false);
    }

    private WpfSnapshot CreateSnapshot(WpfTreeKind treeKind, string? rootName)
    {
        var roots = ResolveRoots(rootName);
        var budget = new ProjectionBudget(_options.MaximumDepth, _options.MaximumNodes);
        var projected = new List<WpfNode>();
        foreach (var root in roots)
        {
            if (!budget.CanVisit)
            {
                budget.MarkTruncated(true);
                break;
            }

            projected.Add(ProjectNode(
                root.Value,
                $"root:{root.Key}",
                root.RootKind,
                treeKind,
                0,
                budget));
        }

        budget.MarkTruncated(projected.Count < roots.Count);
        return new(
            TreeKindName(treeKind),
            projected,
            budget.NodesVisited,
            budget.Truncated,
            treeKind == WpfTreeKind.Visual ? VisualLimitation : LogicalLimitation,
            CreateApplicationInfo());
    }

    /// <summary>
    /// The root a screenshot means when the caller did not name one: the application main window
    /// if there is one, otherwise the only root, and otherwise an error naming the candidates so
    /// the caller can pick. Needed because an injected target has no registered roots to name.
    /// </summary>
    private string DefaultScreenshotRootName()
    {
        var roots = AllRoots();
        var main = roots.FirstOrDefault(item =>
            string.Equals(item.Key, "application.mainWindow", StringComparison.Ordinal));
        if (main is not null)
        {
            return main.Key;
        }

        if (roots.Count == 1)
        {
            return roots[0].Key;
        }

        throw new ArgumentException(
            roots.Count == 0
                ? "No WPF roots are available to capture."
                : "'root' is required because this target has several roots and no application " +
                    "main window. Available roots: " + string.Join(", ", roots.Select(item => item.Key)) + ".");
    }

    private IReadOnlyList<ResolvedRoot> ResolveRoots(string? rootName)
    {
        var roots = AllRoots();
        if (rootName is null)
        {
            return roots;
        }

        var exact = roots.FirstOrDefault(item =>
            string.Equals(item.Key, rootName, StringComparison.Ordinal));
        if (exact is not null)
        {
            return [exact];
        }

        var window = roots.FirstOrDefault(item =>
            item.Value is Window candidate &&
            (string.Equals(candidate.Name, rootName, StringComparison.Ordinal) ||
             string.Equals(candidate.Title, rootName, StringComparison.Ordinal)));
        return window is null
            ? throw new KeyNotFoundException($"No WPF root matches '{rootName}'.")
            : [window];
    }

    private IReadOnlyList<ResolvedRoot> AllRoots()
    {
        var roots = _registeredRoots
            .Select(pair => new ResolvedRoot(pair.Key, "registered", pair.Value))
            .ToList();
        var seen = new HashSet<DependencyObject>(
            roots.Select(item => item.Value),
            ReferenceComparer<DependencyObject>.Instance);

        if (_application is not null)
        {
            var index = 0;
            foreach (Window window in _application.Windows)
            {
                if (seen.Add(window))
                {
                    var key = ReferenceEquals(window, _application.MainWindow)
                        ? "application.mainWindow"
                        : $"application.windows[{index}]";
                    roots.Add(new(key, "application-window", window));
                }

                index++;
            }
        }

        return roots;
    }

    private WpfApplicationInfo? CreateApplicationInfo()
    {
        if (_application is null)
        {
            return null;
        }

        return new(
            TypeName(_application),
            _application.ShutdownMode.ToString(),
            _application.Windows.Count,
            _application.MainWindow?.Name,
            _application.MainWindow?.Title);
    }

    private WpfNode ProjectNode(
        DependencyObject value,
        string path,
        string rootKind,
        WpfTreeKind treeKind,
        int depth,
        ProjectionBudget budget)
    {
        _ = budget.TryVisit(depth);
        var sourceChildren = ProjectChildren(value, path, rootKind, treeKind, depth, budget);
        var truncated = sourceChildren.Truncated ||
            sourceChildren.Children.Any(child => child.Truncated);
        budget.MarkTruncated(truncated);
        return new(
            path,
            rootKind,
            TypeName(value),
            GetName(value),
            GetAttachedString(value, AutomationProperties.AutomationIdProperty),
            GetAttachedString(value, AutomationProperties.NameProperty),
            GetText(value),
            GetBounds(value),
            value is UIElement element ? element.IsVisible : null,
            value is UIElement enabledElement ? enabledElement.IsEnabled : null,
            GetIsLoaded(value),
            value is UIElement focusedElement ? focusedElement.IsKeyboardFocusWithin : null,
            value is UIElement focusableElement ? focusableElement.Focusable : null,
            value is Control control ? control.TabIndex : null,
            value is UIElement opacityElement ? opacityElement.Opacity : null,
            GetDataContext(value),
            GetBindings(value),
            sourceChildren.Children,
            sourceChildren.Children.Count,
            sourceChildren.ProviderCount,
            sourceChildren.NonProjectableCount,
            sourceChildren.EnumerationTruncated,
            truncated);
    }

    private ChildProjection ProjectChildren(
        DependencyObject value,
        string path,
        string rootKind,
        WpfTreeKind treeKind,
        int depth,
        ProjectionBudget budget)
    {
        if (treeKind == WpfTreeKind.Visual)
        {
            if (value is not Visual && value is not Visual3D)
            {
                return new([], 0, 0, false, false);
            }

            var count = VisualTreeHelper.GetChildrenCount(value);
            if (depth >= budget.MaximumDepth)
            {
                return new([], count, 0, false, count > 0);
            }

            var children = new List<WpfNode>();
            for (var index = 0; index < count; index++)
            {
                if (!budget.CanVisit)
                {
                    return new(children, count, 0, false, true);
                }

                children.Add(ProjectNode(
                    VisualTreeHelper.GetChild(value, index),
                    $"{path}/{TreeKindName(treeKind)}[{index}]",
                    rootKind,
                    treeKind,
                    depth + 1,
                    budget));
            }

            return new(children, count, 0, false, false);
        }

        var projected = new List<WpfNode>();
        var providerCount = 0;
        var nonProjectableCount = 0;
        var enumerator = LogicalTreeHelper.GetChildren(value).GetEnumerator();
        try
        {
            while (budget.CanInspectProviderItem)
            {
                if (!enumerator.MoveNext())
                {
                    return new(projected, providerCount, nonProjectableCount, false, false);
                }

                budget.RecordProviderItem();
                providerCount++;
                if (enumerator.Current is DependencyObject dependencyObject)
                {
                    if (depth >= budget.MaximumDepth || !budget.CanVisit)
                    {
                        return new(projected, providerCount, nonProjectableCount, true, true);
                    }

                    projected.Add(ProjectNode(
                        dependencyObject,
                        $"{path}/{TreeKindName(treeKind)}[{projected.Count}]",
                        rootKind,
                        treeKind,
                        depth + 1,
                        budget));
                }
                else
                {
                    nonProjectableCount++;
                }
            }

            return new(projected, providerCount, nonProjectableCount, true, true);
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    private WpfDataContext? GetDataContext(DependencyObject value)
    {
        object? dataContext;
        bool locallySet;
        switch (value)
        {
            case FrameworkElement element:
                dataContext = element.DataContext;
                locallySet = element.ReadLocalValue(FrameworkElement.DataContextProperty) !=
                    DependencyProperty.UnsetValue;
                break;
            case FrameworkContentElement contentElement:
                dataContext = contentElement.DataContext;
                locallySet = contentElement.ReadLocalValue(FrameworkContentElement.DataContextProperty) !=
                    DependencyProperty.UnsetValue;
                break;
            default:
                return null;
        }

        return dataContext is null
            ? null
            : new(TypeName(dataContext), Preview(dataContext), locallySet);
    }

    private IReadOnlyList<WpfBinding> GetBindings(DependencyObject value)
    {
        var bindings = new List<WpfBinding>();
        var localValues = value.GetLocalValueEnumerator();
        while (localValues.MoveNext())
        {
            var entry = localValues.Current;
            if (!BindingOperations.IsDataBound(value, entry.Property))
            {
                continue;
            }

            var expression = BindingOperations.GetBindingExpressionBase(value, entry.Property);
            if (expression is null)
            {
                continue;
            }

            string? path = null;
            string? mode = null;
            string? trigger = null;
            string? elementName = null;
            string? relativeSource = null;
            string? sourceType = null;
            switch (expression)
            {
                case BindingExpression bindingExpression:
                    var binding = bindingExpression.ParentBinding;
                    path = binding.Path?.Path;
                    mode = binding.Mode.ToString();
                    trigger = binding.UpdateSourceTrigger.ToString();
                    elementName = binding.ElementName;
                    relativeSource = binding.RelativeSource?.Mode.ToString();
                    sourceType = bindingExpression.ResolvedSource?.GetType().FullName;
                    break;
                case MultiBindingExpression multiBindingExpression:
                    mode = multiBindingExpression.ParentMultiBinding.Mode.ToString();
                    trigger = multiBindingExpression.ParentMultiBinding.UpdateSourceTrigger.ToString();
                    break;
            }

            bindings.Add(new(
                $"{entry.Property.OwnerType.FullName}.{entry.Property.Name}",
                expression.ParentBindingBase.GetType().Name,
                path,
                mode,
                trigger,
                elementName,
                relativeSource,
                sourceType,
                expression.Status.ToString(),
                expression.HasError));
        }

        return bindings
            .OrderBy(binding => binding.Property, StringComparer.Ordinal)
            .ToArray();
    }

    private WpfScreenshotResult CaptureScreenshot(
        string rootName,
        string? path,
        WpfTreeKind treeKind)
    {
        var resolvedRoot = ResolveRoots(rootName).Single();
        var selected = path is null
            ? resolvedRoot.Value
            : FindByPath(
                resolvedRoot.Value,
                $"root:{resolvedRoot.Key}",
                path,
                treeKind,
                0,
                new ProjectionBudget(_options.MaximumDepth, _options.MaximumNodes));
        if (selected is null)
        {
            return ScreenshotFailure(
                "failed",
                "path_not_found",
                $"No element within the bounded {TreeKindName(treeKind)} projection matches '{path}'.");
        }
        if (selected is not FrameworkElement element || selected is not Visual visual)
        {
            return ScreenshotFailure(
                "unsupported",
                "not_renderable",
                "The selected object is not a renderable FrameworkElement Visual.");
        }
        if (!element.IsLoaded)
        {
            return ScreenshotFailure(
                "unsupported",
                "not_loaded",
                "The selected element is not loaded into a presentation source.");
        }

        if ((double.IsNaN(element.ActualWidth) || double.IsInfinity(element.ActualWidth)) ||
            (double.IsNaN(element.ActualHeight) || double.IsInfinity(element.ActualHeight)))
        {
            return ScreenshotFailure(
                "unsupported",
                "empty_bounds",
                "The selected element has no finite rendered area.");
        }
        var dpi = VisualTreeHelper.GetDpi(visual);
        var width = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
        if (width <= 0 || height <= 0)
        {
            return ScreenshotFailure(
                "unsupported",
                "empty_bounds",
                "The selected element has no finite rendered area.");
        }
        if (width > _options.MaximumScreenshotWidth ||
            height > _options.MaximumScreenshotHeight ||
            (long)width * height > _options.MaximumScreenshotPixels)
        {
            return ScreenshotFailure(
                "unsupported",
                "size_limit",
                $"The selected element exceeds the {_options.MaximumScreenshotWidth}x" +
                $"{_options.MaximumScreenshotHeight} and {_options.MaximumScreenshotPixels} pixel limits.");
        }

        try
        {
            var bitmap = new RenderTargetBitmap(
                width,
                height,
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var bytes = stream.ToArray();
            if (bytes.Length > _options.MaximumScreenshotBytes)
            {
                return ScreenshotFailure(
                    "unsupported",
                    "size_limit",
                    $"The encoded screenshot exceeds the {_options.MaximumScreenshotBytes} byte limit.");
            }

            return new(
                true,
                "captured",
                "image/png",
                width,
                height,
                Convert.ToBase64String(bytes),
                null,
                null,
                ScreenshotLimitation);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException or NotSupportedException or
            IOException or COMException)
        {
            return ScreenshotFailure("failed", "render_failed", exception.Message);
        }
    }

    private static DependencyObject? FindByPath(
        DependencyObject current,
        string currentPath,
        string requestedPath,
        WpfTreeKind treeKind,
        int depth,
        ProjectionBudget budget)
    {
        if (!budget.TryVisit(depth))
        {
            return null;
        }
        if (string.Equals(currentPath, requestedPath, StringComparison.Ordinal))
        {
            return current;
        }
        if (depth >= budget.MaximumDepth)
        {
            return null;
        }

        if (treeKind == WpfTreeKind.Visual)
        {
            if (current is not Visual && current is not Visual3D)
            {
                return null;
            }

            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < count && budget.CanVisit; index++)
            {
                var found = FindByPath(
                    VisualTreeHelper.GetChild(current, index),
                    $"{currentPath}/{TreeKindName(treeKind)}[{index}]",
                    requestedPath,
                    treeKind,
                    depth + 1,
                    budget);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        var childIndex = 0;
        var enumerator = LogicalTreeHelper.GetChildren(current).GetEnumerator();
        try
        {
            while (budget.CanVisit &&
                budget.CanInspectProviderItem &&
                enumerator.MoveNext())
            {
                budget.RecordProviderItem();
                if (enumerator.Current is not DependencyObject child)
                {
                    continue;
                }

                var found = FindByPath(
                    child,
                    $"{currentPath}/{TreeKindName(treeKind)}[{childIndex++}]",
                    requestedPath,
                    treeKind,
                    depth + 1,
                    budget);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }

        return null;
    }

    private static WpfWaitResult Evaluate(WpfCondition condition, WpfSnapshot snapshot)
    {
        var candidates = Flatten(snapshot.Roots);
        var match = candidates.FirstOrDefault(node =>
            (condition.Path is null ||
             string.Equals(node.Path, condition.Path, StringComparison.Ordinal)) &&
            (condition.Name is null ||
             string.Equals(node.Name, condition.Name, StringComparison.Ordinal)) &&
            (condition.AutomationId is null ||
             string.Equals(node.AutomationId, condition.AutomationId, StringComparison.Ordinal)));
        var conclusive = match is not null || !snapshot.Truncated;
        var satisfied = condition.State switch
        {
            "exists" => match is not null,
            "notExists" => match is null && !snapshot.Truncated,
            "visible" => match?.IsVisible is true,
            "enabled" => match?.IsEnabled is true,
            "loaded" => match?.IsLoaded is true,
            "focused" => match?.IsKeyboardFocusWithin is true,
            "textEquals" => match is not null &&
                string.Equals(match.Text, condition.Expected, StringComparison.Ordinal),
            "dataContextTypeEquals" => match is not null &&
                string.Equals(match.DataContext?.Type, condition.Expected, StringComparison.Ordinal),
            "hasBindingError" => match?.Bindings.Any(binding => binding.HasValidationError) is true,
            _ => throw new ArgumentException(
                $"Unsupported condition state '{condition.State}'.",
                nameof(condition))
        };
        var description =
            $"state={condition.State}, tree={snapshot.TreeKind}, root={condition.Root ?? "*"}, " +
            $"path={condition.Path ?? "*"}, name={condition.Name ?? "*"}, " +
            $"automationId={condition.AutomationId ?? "*"}, expected={condition.Expected ?? "<none>"}";
        return new(satisfied, conclusive, TimeSpan.Zero, match, description);
    }

    private static IEnumerable<WpfNode> Flatten(IEnumerable<WpfNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children))
            {
                yield return child;
            }
        }
    }

    private static string? GetName(DependencyObject value) =>
        value switch
        {
            FrameworkElement element => NullIfEmpty(element.Name),
            FrameworkContentElement contentElement => NullIfEmpty(contentElement.Name),
            _ => null
        };

    private string? GetText(DependencyObject value)
    {
        object? text = value switch
        {
            TextBlock textBlock => textBlock.Text,
            TextBox textBox => textBox.Text,
            RichTextBox richTextBox => new TextRange(
                richTextBox.Document.ContentStart,
                richTextBox.Document.ContentEnd).Text,
            ContentControl contentControl when contentControl.Content is string => contentControl.Content,
            HeaderedContentControl headered when headered.Header is string => headered.Header,
            Window window => window.Title,
            _ => null
        };
        return text is null ? null : Limit(Convert.ToString(text, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static WpfBounds? GetBounds(DependencyObject value)
    {
        if (value is not FrameworkElement element)
        {
            return null;
        }

        var offset = VisualTreeHelper.GetOffset(element);
        return new(offset.X, offset.Y, element.ActualWidth, element.ActualHeight);
    }

    private static bool? GetIsLoaded(DependencyObject value) =>
        value switch
        {
            FrameworkElement element => element.IsLoaded,
            FrameworkContentElement contentElement => contentElement.IsLoaded,
            _ => null
        };

    private static string? GetAttachedString(
        DependencyObject value,
        DependencyProperty property) =>
        NullIfEmpty(value.GetValue(property) as string);

    private string Preview(object value)
    {
        try
        {
            return Limit(Convert.ToString(value, CultureInfo.InvariantCulture) ?? TypeName(value));
        }
        catch (Exception exception)
        {
            return $"<{TypeName(value)} preview failed: {exception.GetType().Name}>";
        }
    }

    private string Limit(string value) =>
        value.Length <= _options.MaximumPreviewLength
            ? value
            : $"{value.Substring(0, _options.MaximumPreviewLength)}…";

    private static WpfScreenshotResult ScreenshotFailure(
        string status,
        string code,
        string message) =>
        new(false, status, null, null, null, null, code, message, ScreenshotLimitation);

    private static WpfCondition ParseCondition(JsonElement arguments) =>
        new(
            OptionalString(arguments, "root"),
            ParseTreeKind(OptionalString(arguments, "tree")),
            OptionalString(arguments, "path"),
            OptionalString(arguments, "name"),
            OptionalString(arguments, "automationId"),
            OptionalString(arguments, "state") ?? "exists",
            OptionalString(arguments, "expected"));

    private static WpfTreeKind ParseTreeKind(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "visual" => WpfTreeKind.Visual,
            "logical" => WpfTreeKind.Logical,
            _ => throw new ArgumentException("'tree' must be either 'visual' or 'logical'.")
        };

    private static string TreeKindName(WpfTreeKind treeKind) =>
        treeKind == WpfTreeKind.Visual ? "visual" : "logical";

    private static string RequiredString(JsonElement arguments, string propertyName) =>
        OptionalString(arguments, propertyName)
        ?? throw new ArgumentException($"'{propertyName}' is required.");

    private static string? OptionalString(JsonElement arguments, string propertyName)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new ArgumentException($"'{propertyName}' must be a string.");
    }

    private static TimeSpan OptionalMilliseconds(
        JsonElement arguments,
        string propertyName,
        TimeSpan defaultValue,
        bool allowZero)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var milliseconds) ||
            (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds)) ||
            milliseconds < 0 ||
            (!allowZero && milliseconds == 0))
        {
            throw new ArgumentOutOfRangeException(propertyName);
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static void ValidateOptions(WpfAdapterOptions options)
    {
        if (options.MaximumDepth < 0 ||
            options.MaximumNodes < 1 ||
            options.MaximumPreviewLength < 1 ||
            options.MaximumScreenshotWidth < 1 ||
            options.MaximumScreenshotHeight < 1 ||
            options.MaximumScreenshotPixels < 1 ||
            options.MaximumScreenshotBytes < 1 ||
            options.MaximumScreenshotBytes > MaximumProtocolSafeScreenshotBytes ||
            options.DefaultWaitTimeout < TimeSpan.Zero ||
            options.DefaultPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Projection, preview, screenshot, and wait limits must be valid positive values.");
        }
    }

    private static string TypeName(object value) =>
        value.GetType().FullName ?? value.GetType().Name;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private sealed record ResolvedRoot(string Key, string RootKind, DependencyObject Value);

    private sealed record ChildProjection(
        IReadOnlyList<WpfNode> Children,
        int ProviderCount,
        int NonProjectableCount,
        bool EnumerationTruncated,
        bool Truncated);

    private sealed class ProjectionBudget(int maximumDepth, int maximumNodes)
    {
        public int MaximumDepth { get; } = maximumDepth;

        public int NodesVisited { get; private set; }

        public bool Truncated { get; private set; }

        public bool CanVisit => NodesVisited < maximumNodes;

        public int RemainingNodes => maximumNodes - NodesVisited;

        public bool CanInspectProviderItem => ProviderItemsInspected < maximumNodes;

        private int ProviderItemsInspected { get; set; }

        public void RecordProviderItem() => ProviderItemsInspected++;

        public bool TryVisit(int depth)
        {
            if (depth > MaximumDepth || !CanVisit)
            {
                Truncated = true;
                return false;
            }

            NodesVisited++;
            return true;
        }

        public void MarkTruncated(bool value) => Truncated |= value;
    }
}
