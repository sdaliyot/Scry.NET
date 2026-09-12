using System.Drawing;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Forms;
using Scry.Contracts;
using Scry.Endpoint;

namespace Scry.WinForms;

public static class WinFormsEndpointBuilderExtensions
{
    public static EndpointBuilder UseWinForms(
        this EndpointBuilder builder,
        Control dispatcherOwner,
        Action<WinFormsAdapterBuilder>? configure = null,
        WinFormsAdapterOptions? options = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        var adapterBuilder = new WinFormsAdapterBuilder(dispatcherOwner, options);
        configure?.Invoke(adapterBuilder);
        var adapter = adapterBuilder.Build();
        var readOnlyUiPolicy = new OperationPolicy
        {
            ExecutionPolicy = OperationExecutionPolicy.UiOwner,
            IsReadOnly = true
        };

        builder.RegisterValue(
            "winforms",
            adapter,
            "Thread-marshalled Windows Forms inspection service.");

        // See Scry.Wpf: lets evaluate/execute opt into the owner control's thread with
        // "marshal": "ui" instead of failing the Control.InvokeRequired check.
        builder.UseExecutionMarshaller(async (callback, cancellationToken) =>
            await await adapter.Marshaller.InvokeAsync(callback, cancellationToken).ConfigureAwait(false));
        builder.RegisterOperation(
            "winforms.snapshot",
            async (arguments, cancellationToken) =>
                (object?)JsonSerializer.SerializeToElement(
                    await adapter.SnapshotAsync(arguments, cancellationToken).ConfigureAwait(false),
                    ScryJson.Options),
            "Projects Application.OpenForms and registered Control roots with explicit bounds.",
            readOnlyUiPolicy);
        builder.RegisterOperation(
            "winforms.wait",
            async (arguments, cancellationToken) =>
                (object?)JsonSerializer.SerializeToElement(
                    await adapter.WaitAsync(arguments, cancellationToken).ConfigureAwait(false),
                    ScryJson.Options),
            "Waits for a bounded Windows Forms condition without blocking the UI thread.",
            readOnlyUiPolicy);
        builder.RegisterOperation(
            "winforms.assert",
            async (arguments, cancellationToken) =>
                (object?)JsonSerializer.SerializeToElement(
                    await adapter.AssertAsync(arguments, cancellationToken).ConfigureAwait(false),
                    ScryJson.Options),
            "Asserts a bounded Windows Forms condition.",
            readOnlyUiPolicy);
        builder.RegisterOperation(
            "winforms.screenshot",
            async (arguments, cancellationToken) =>
                (object?)JsonSerializer.SerializeToElement(
                    await adapter.ScreenshotAsync(arguments, cancellationToken).ConfigureAwait(false),
                    ScryJson.Options),
            "Captures a registered Control with DrawToBitmap when supported.",
            readOnlyUiPolicy);
        return builder;
    }
}

public sealed class WinFormsAdapterBuilder
{
    private readonly Dictionary<string, Control> _roots = new(StringComparer.Ordinal);

    public WinFormsAdapterBuilder(Control dispatcherOwner, WinFormsAdapterOptions? options = null)
    {
        DispatcherOwner = dispatcherOwner ?? throw new ArgumentNullException(nameof(dispatcherOwner));
        Options = options ?? new WinFormsAdapterOptions();
    }

    internal Control DispatcherOwner { get; }

    internal WinFormsAdapterOptions Options { get; }

    public WinFormsAdapterBuilder RegisterRoot(string name, Control control)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            throw new ArgumentException(
                "Root names must contain 1-128 non-whitespace characters.",
                nameof(name));
        }

        if (control is null)
        {
            throw new ArgumentNullException(nameof(control));
        }
        if (!control.IsHandleCreated || control.InvokeRequired)
        {
            throw new ArgumentException(
                "Registered roots must have a created handle on the dispatcher owner's UI thread.",
                nameof(control));
        }
        if (_roots.ContainsKey(name))
        {
            throw new ArgumentException($"A Windows Forms root named '{name}' is already registered.", nameof(name));
        }

        _roots[name] = control;

        return this;
    }

    public WinFormsAdapter Build() =>
        new(DispatcherOwner, new Dictionary<string, Control>(_roots, StringComparer.Ordinal), Options);
}

public sealed class WinFormsAdapter
{
    private const int MaximumProtocolSafeScreenshotBytes = 4 * 1024 * 1024;
    private const string ProjectionLimitation =
        "This is a bounded managed Control projection, not a complete native window tree. " +
        "Owner-drawn content, WebView2, ActiveX, and native child HWND internals may be absent.";
    private const string ScreenshotLimitation =
        "DrawToBitmap may omit owner-drawn, hardware-accelerated, WebView2, ActiveX, or native child content.";

    private readonly WinFormsDispatcher _dispatcher;
    private readonly IReadOnlyDictionary<string, Control> _registeredRoots;
    private readonly WinFormsAdapterOptions _options;

    /// <summary>
    /// The owner-thread dispatcher every projection is marshalled through, exposed so registration
    /// can reuse it as the endpoint's execution marshaller.
    /// </summary>
    internal WinFormsDispatcher Marshaller => _dispatcher;

    internal WinFormsAdapter(
        Control dispatcherOwner,
        IReadOnlyDictionary<string, Control> registeredRoots,
        WinFormsAdapterOptions options)
    {
        ValidateOptions(options);
        _dispatcher = new(dispatcherOwner);
        _registeredRoots = registeredRoots;
        _options = options;
    }

    public Task<WinFormsSnapshot> SnapshotAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var rootName = OptionalString(arguments, "root");
        return _dispatcher.InvokeAsync(() => CreateSnapshot(rootName), cancellationToken);
    }

    public async ValueTask<object?> WaitAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var request = ParseCondition(arguments);
        var timeout = OptionalMilliseconds(arguments, "timeoutMilliseconds", _options.DefaultWaitTimeout);
        var pollInterval = OptionalMilliseconds(arguments, "pollIntervalMilliseconds", _options.DefaultPollInterval);
        return await WaitAsync(request, timeout, pollInterval, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WinFormsWaitResult> WaitAsync(
        WinFormsCondition condition,
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
                () => Evaluate(condition, CreateSnapshot(condition.Root)),
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

    public async ValueTask<object?> AssertAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var condition = ParseCondition(arguments);
        var result = await _dispatcher.InvokeAsync(
            () => Evaluate(condition, CreateSnapshot(condition.Root)),
            cancellationToken).ConfigureAwait(false);
        if (!result.Satisfied)
        {
            var qualification = result.Conclusive ? string.Empty : " (projection was truncated)";
            throw new InvalidOperationException(
                $"Windows Forms assertion failed{qualification}: {result.Description}");
        }

        return result;
    }

    public async ValueTask<object?> ScreenshotAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        // 'root' is optional here, unlike the typed overload: an injected target registers no
        // roots, so a caller has no way to learn a root name without first taking a snapshot.
        var rootName = OptionalString(arguments, "root");
        var path = OptionalString(arguments, "path");

        // Resolving the default reads Application.OpenForms, so it belongs inside the dispatcher
        // like everything else here.
        return await _dispatcher.InvokeAsync(
            () => CaptureScreenshot(rootName ?? DefaultScreenshotRootName(), path),
            cancellationToken).ConfigureAwait(false);
    }

    private WinFormsSnapshot CreateSnapshot(string? rootName)
    {
        var roots = ResolveRoots(rootName);
        var budget = new ProjectionBudget(_options.MaximumDepth, _options.MaximumNodes);
        var projected = new List<WinFormsNode>();
        foreach (var root in roots)
        {
            if (!budget.CanVisit)
            {
                budget.MarkTruncated(true);
                break;
            }

            projected.Add(ProjectControl(root.Value, $"root:{root.Key}", 0, budget));
        }

        budget.MarkTruncated(projected.Count < roots.Count);
        return new(
            "managed-control-hierarchy",
            projected,
            budget.NodesVisited,
            budget.Truncated,
            ProjectionLimitation);
    }

    /// <summary>
    /// The root a screenshot means when the caller did not name one: the only root if there is
    /// exactly one, otherwise an error naming the candidates. Needed because an injected target
    /// has no registered roots to name.
    /// </summary>
    private string DefaultScreenshotRootName()
    {
        var roots = AllRoots();
        if (roots.Count == 1)
        {
            return roots[0].Key;
        }

        throw new ArgumentException(
            roots.Count == 0
                ? "No Windows Forms roots are available to capture."
                : "'root' is required because this target has several roots. Available roots: " +
                    string.Join(", ", roots.Select(item => item.Key)) + ".");
    }

    private IReadOnlyList<KeyValuePair<string, Control>> ResolveRoots(string? rootName)
    {
        var roots = AllRoots();
        if (rootName is null)
        {
            return roots;
        }

        var exact = roots.FirstOrDefault(item =>
            string.Equals(item.Key, rootName, StringComparison.Ordinal));
        if (!exact.Equals(default(KeyValuePair<string, Control>)))
        {
            return [exact];
        }

        var matches = roots
            .Where(item =>
                item.Value is Form form &&
                (string.Equals(form.Name, rootName, StringComparison.Ordinal) ||
                 string.Equals(form.Text, rootName, StringComparison.Ordinal)))
            .ToArray();
        return matches.Length switch
        {
            1 => matches,
            0 => throw new KeyNotFoundException($"No Windows Forms root matches '{rootName}'."),
            _ => throw new InvalidOperationException(
                $"Multiple Windows Forms roots match '{rootName}'; use a canonical root key.")
        };
    }

    private IReadOnlyList<KeyValuePair<string, Control>> AllRoots()
    {
        var roots = _registeredRoots.ToList();
        var keys = new HashSet<string>(_registeredRoots.Keys, StringComparer.Ordinal);
        var seen = new HashSet<Control>(_registeredRoots.Values, ReferenceComparer<Control>.Instance);
        foreach (var form in Application.OpenForms.Cast<Form>())
        {
            if (form.InvokeRequired || !seen.Add(form))
            {
                continue;
            }

            var baseName = string.IsNullOrWhiteSpace(form.Name)
                ? $"openForm:{form.GetType().Name}"
                : $"openForm:{form.Name}";
            var name = baseName;
            var suffix = 2;
            while (!keys.Add(name))
            {
                name = $"{baseName}:{suffix++}";
            }

            roots.Add(new(name, form));
        }

        return roots;
    }

    private static WinFormsNode ProjectControl(
        Control control,
        string path,
        int depth,
        ProjectionBudget budget)
    {
        if (!budget.TryVisit(depth))
        {
            return TruncatedNode(control, path);
        }

        var actualChildren = control.Controls.Count;
        var children = new List<WinFormsNode>();
        var nodeTruncated = false;
        if (depth >= budget.MaximumDepth)
        {
            nodeTruncated = actualChildren > 0;
            budget.MarkTruncated(nodeTruncated);
        }
        else
        {
            for (var index = 0; index < actualChildren; index++)
            {
                if (!budget.CanVisit)
                {
                    nodeTruncated = true;
                    budget.MarkTruncated(true);
                    break;
                }

                children.Add(ProjectControl(
                    control.Controls[index],
                    $"{path}/controls[{index}]",
                    depth + 1,
                    budget));
            }
        }

        var menuItems = control is ToolStrip strip
            ? ProjectItems(strip.Items, $"{path}/items", depth + 1, budget)
            : [];
        var bindings = control.DataBindings.Cast<Binding>()
            .Select(binding => new WinFormsBinding(
                binding.PropertyName,
                binding.BindingMemberInfo.BindingMember,
                binding.DataSource?.GetType().FullName,
                binding.FormattingEnabled,
                binding.ControlUpdateMode.ToString(),
                binding.DataSourceUpdateMode.ToString()))
            .ToArray();
        var ownedForms = control is Form form
            ? form.OwnedForms.Select(owned => new WinFormsOwnedForm(
                owned.GetType().FullName ?? owned.GetType().Name,
                owned.Name,
                owned.Text,
                owned.Visible,
                owned.Enabled)).ToArray()
            : [];

        return new(
            path,
            control.GetType().FullName ?? control.GetType().Name,
            control.Name,
            control.Text,
            control.Bounds,
            control.Visible,
            control.Enabled,
            control.IsHandleCreated,
            control.Focused,
            control.TabIndex,
            control.AccessibilityObject.Name,
            control.AccessibilityObject.Description,
            control.AccessibilityObject.Role.ToString(),
            bindings,
            ownedForms,
            menuItems,
            children,
            children.Count,
            actualChildren,
            nodeTruncated || children.Any(item => item.Truncated) || menuItems.Any(item => item.Truncated));
    }

    private static IReadOnlyList<WinFormsMenuItem> ProjectItems(
        ToolStripItemCollection items,
        string path,
        int depth,
        ProjectionBudget budget)
    {
        var projected = new List<WinFormsMenuItem>();
        for (var index = 0; index < items.Count; index++)
        {
            if (!budget.TryVisit(depth))
            {
                break;
            }

            var item = items[index];
            var children = item is ToolStripDropDownItem dropDown && depth < budget.MaximumDepth
                ? ProjectItems(dropDown.DropDownItems, $"{path}[{index}]/items", depth + 1, budget)
                : [];
            var actualChildren = item is ToolStripDropDownItem parent ? parent.DropDownItems.Count : 0;
            var truncated = actualChildren > children.Count;
            budget.MarkTruncated(truncated);
            projected.Add(new(
                $"{path}[{index}]",
                item.GetType().FullName ?? item.GetType().Name,
                item.Name,
                item.Text ?? string.Empty,
                item.Available,
                item.Enabled,
                item.Selected,
                item is ToolStripMenuItem menuItem && menuItem.Checked,
                children,
                actualChildren,
                truncated || children.Any(child => child.Truncated)));
        }

        budget.MarkTruncated(projected.Count < items.Count);
        return projected;
    }

    private WinFormsScreenshot CaptureScreenshot(string rootName, string? path)
    {
        var root = ResolveRoots(rootName).Single().Value;
        var control = path is null
            ? root
            : FindControl(
                root,
                $"root:{rootName}",
                path,
                0,
                new ProjectionBudget(_options.MaximumDepth, _options.MaximumNodes))
                ?? throw new KeyNotFoundException(
                    $"No control in the bounded projection matches '{path}'.");
        if (!control.IsHandleCreated)
        {
            throw new InvalidOperationException("The selected control has not created a native handle.");
        }
        if (control.Width <= 0 || control.Height <= 0)
        {
            throw new InvalidOperationException("The selected control has no drawable area.");
        }
        if (control.Width > _options.MaximumScreenshotWidth ||
            control.Height > _options.MaximumScreenshotHeight)
        {
            throw new InvalidOperationException(
                $"The selected control exceeds the {_options.MaximumScreenshotWidth}x" +
                $"{_options.MaximumScreenshotHeight} screenshot limit.");
        }

        using var bitmap = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppArgb);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var bytes = stream.ToArray();
        if (bytes.Length > _options.MaximumScreenshotBytes)
        {
            throw new InvalidOperationException(
                $"The encoded screenshot exceeds the {_options.MaximumScreenshotBytes} byte limit.");
        }

        return new(
            "image/png",
            bitmap.Width,
            bitmap.Height,
            Convert.ToBase64String(bytes),
            ScreenshotLimitation);
    }

    private static WinFormsWaitResult Evaluate(
        WinFormsCondition condition,
        WinFormsSnapshot snapshot)
    {
        var candidates = Flatten(snapshot.Roots);
        var match = candidates.FirstOrDefault(node =>
            (condition.Path is null || string.Equals(node.Path, condition.Path, StringComparison.Ordinal)) &&
            (condition.Name is null || string.Equals(node.Name, condition.Name, StringComparison.Ordinal)));
        var conclusive = match is not null || !snapshot.Truncated;
        var satisfied = condition.State switch
        {
            "exists" => match is not null,
            "notExists" => match is null && !snapshot.Truncated,
            "visible" => match?.Visible is true,
            "enabled" => match?.Enabled is true,
            "focused" => match?.Focused is true,
            "textEquals" => match is not null &&
                string.Equals(match.Text, condition.Expected, StringComparison.Ordinal),
            _ => throw new ArgumentException(
                $"Unsupported condition state '{condition.State}'.",
                nameof(condition))
        };
        var description =
            $"state={condition.State}, root={condition.Root ?? "*"}, " +
            $"path={condition.Path ?? "*"}, name={condition.Name ?? "*"}, " +
            $"expected={condition.Expected ?? "<none>"}";
        return new(satisfied, conclusive, TimeSpan.Zero, match, description);
    }

    private static IEnumerable<WinFormsNode> Flatten(IEnumerable<WinFormsNode> roots)
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

    private static Control? FindControl(
        Control control,
        string currentPath,
        string requestedPath,
        int depth,
        ProjectionBudget budget)
    {
        if (!budget.TryVisit(depth))
        {
            return null;
        }
        if (string.Equals(currentPath, requestedPath, StringComparison.Ordinal))
        {
            return control;
        }

        for (var index = 0; index < control.Controls.Count; index++)
        {
            if (!budget.CanVisit || depth >= budget.MaximumDepth)
            {
                return null;
            }

            var found = FindControl(
                control.Controls[index],
                $"{currentPath}/controls[{index}]",
                requestedPath,
                depth + 1,
                budget);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static WinFormsNode TruncatedNode(Control control, string path) =>
        new(
            path,
            control.GetType().FullName ?? control.GetType().Name,
            control.Name,
            control.Text,
            control.Bounds,
            control.Visible,
            control.Enabled,
            control.IsHandleCreated,
            control.Focused,
            control.TabIndex,
            null,
            null,
            null,
            [],
            [],
            [],
            [],
            0,
            control.Controls.Count,
            true);

    private static WinFormsCondition ParseCondition(JsonElement arguments) =>
        new(
            OptionalString(arguments, "root"),
            OptionalString(arguments, "path"),
            OptionalString(arguments, "name"),
            OptionalString(arguments, "state") ?? "exists",
            OptionalString(arguments, "expected"));

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
        TimeSpan defaultValue)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }

        var milliseconds = value.GetDouble();
        if ((double.IsNaN(milliseconds) || double.IsInfinity(milliseconds)) || milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(propertyName);
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static void ValidateOptions(WinFormsAdapterOptions options)
    {
        if (options.MaximumDepth < 0 ||
            options.MaximumNodes < 1 ||
            options.MaximumScreenshotWidth < 1 ||
            options.MaximumScreenshotHeight < 1 ||
            options.MaximumScreenshotBytes < 1 ||
            options.MaximumScreenshotBytes > MaximumProtocolSafeScreenshotBytes ||
            options.DefaultWaitTimeout < TimeSpan.Zero ||
            options.DefaultPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Projection, screenshot, and wait limits must be valid positive values.");
        }
    }

    private sealed class ProjectionBudget(int maximumDepth, int maximumNodes)
    {
        public int MaximumDepth { get; } = maximumDepth;

        public int NodesVisited { get; private set; }

        public bool Truncated { get; private set; }

        public bool CanVisit => NodesVisited < maximumNodes;

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
