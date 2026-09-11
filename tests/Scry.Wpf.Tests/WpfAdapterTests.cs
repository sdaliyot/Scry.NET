using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text.Json;
using Scry.Contracts;
using Scry.Sdk;

namespace Scry.Wpf.Tests;

public sealed class WpfAdapterTests(WpfFixture fixture) : IClassFixture<WpfFixture>
{
    [Fact]
    public async Task Visual_snapshot_marshals_and_includes_metadata_data_context_and_bindings()
    {
        var snapshot = await fixture.Adapter.SnapshotAsync(WpfTreeKind.Visual, "main");

        Assert.Equal("visual", snapshot.TreeKind);
        Assert.False(snapshot.Truncated);
        Assert.Contains("not a complete UI", snapshot.Limitation);
        var editor = Assert.Single(Flatten(snapshot.Roots), node => node.Name == "editor");
        Assert.Equal("editor-id", editor.AutomationId);
        Assert.Equal("Hello", editor.Text);
        Assert.Equal(typeof(ViewModel).FullName, editor.DataContext?.Type);
        var binding = Assert.Single(editor.Bindings);
        Assert.EndsWith(".Text", binding.Property, StringComparison.Ordinal);
        Assert.Equal("Caption", binding.Path);
        Assert.Equal("Active", binding.Status);
    }

    [Fact]
    public async Task Logical_snapshot_is_explicitly_labeled_and_not_claimed_complete()
    {
        var snapshot = await fixture.Adapter.SnapshotAsync(WpfTreeKind.Logical, "main");

        Assert.Equal("logical", snapshot.TreeKind);
        Assert.Contains("not a complete visual", snapshot.Limitation);
        Assert.Contains(Flatten(snapshot.Roots), node => node.Name == "editor");
        Assert.All(snapshot.Roots, root => Assert.Equal("registered", root.RootKind));
    }

    [Fact]
    public async Task Default_roots_include_registered_roots_and_application_windows_without_duplicates()
    {
        var snapshot = await fixture.Adapter.SnapshotAsync();

        Assert.Contains(snapshot.Roots, root => root.Path == "root:main");
        Assert.Contains(snapshot.Roots, root =>
            root.RootKind == "application-window" && root.Name == "secondary");
        Assert.NotNull(snapshot.Application);
        Assert.Equal(2, snapshot.Application.WindowCount);
    }

    [Fact]
    public async Task Projection_budget_reports_truncation_and_makes_absence_inconclusive()
    {
        var adapter = await fixture.Dispatcher.InvokeAsync(
            () => new WpfAdapterBuilder(
                    fixture.Application,
                    new WpfAdapterOptions { MaximumNodes = 1 })
                .RegisterRoot("main", fixture.MainWindow)
                .Build());

        var snapshot = await adapter.SnapshotAsync(WpfTreeKind.Visual, "main");
        var logicalSnapshot = await adapter.SnapshotAsync(WpfTreeKind.Logical, "main");
        var result = await adapter.WaitAsync(
            new WpfCondition(Root: "main", Name: "missing", State: "notExists"),
            timeout: TimeSpan.Zero);

        Assert.True(snapshot.Truncated);
        Assert.Equal(1, snapshot.NodesVisited);
        Assert.True(Assert.Single(snapshot.Roots).Truncated);
        Assert.True(Assert.Single(logicalSnapshot.Roots).ChildEnumerationTruncated);
        Assert.False(result.Satisfied);
        Assert.False(result.Conclusive);
    }

    [Fact]
    public async Task Wait_and_assert_are_reusable_typed_helpers()
    {
        var result = await fixture.Adapter.WaitAsync(
            new WpfCondition(Root: "main", Name: "editor", State: "textEquals", Expected: "Hello"),
            TimeSpan.FromSeconds(1));

        Assert.True(result.Satisfied);
        Assert.True(result.Conclusive);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Adapter.AssertAsync(
                new WpfCondition(Root: "main", Name: "missing", State: "exists")));
    }

    [Fact]
    public async Task Screenshot_captures_loaded_visual_and_reports_unsupported_root()
    {
        var captured = await fixture.Adapter.ScreenshotAsync("main");
        var unsupported = await fixture.Adapter.ScreenshotAsync("drawing");

        Assert.True(captured.Success, captured.Message);
        Assert.Equal("captured", captured.Status);
        Assert.Equal("image/png", captured.MediaType);
        Assert.False(string.IsNullOrEmpty(captured.Base64Data));
        Assert.Contains("separate HWND", captured.Limitation);
        Assert.False(unsupported.Success);
        Assert.Equal("unsupported", unsupported.Status);
        Assert.Equal("not_renderable", unsupported.ErrorCode);
    }

    [Fact]
    public async Task Screenshot_reports_an_encoded_payload_over_the_byte_limit()
    {
        var adapter = await fixture.Dispatcher.InvokeAsync(
            () => new WpfAdapterBuilder(
                    fixture.Application,
                    new WpfAdapterOptions { MaximumScreenshotBytes = 1 })
                .RegisterRoot("main", fixture.MainWindow)
                .Build());

        var result = await adapter.ScreenshotAsync("main");

        Assert.False(result.Success);
        Assert.Equal("unsupported", result.Status);
        Assert.Equal("size_limit", result.ErrorCode);
        Assert.Contains("encoded screenshot", result.Message);
    }

    [Fact]
    public async Task Registered_snapshot_returns_inline_structured_json()
    {
        await using var host = AgentHost.Start(
            builder => builder.UseWpf(
                fixture.Application,
                adapter => adapter.RegisterWindow("main", fixture.MainWindow)));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);

        var response = await client.RequestAsync(
            "invoke",
            new
            {
                registeredOperation = "wpf.snapshot",
                arguments = new { root = "main", tree = "visual" }
            });

        Assert.True(response.Success, response.Error?.Message);
        var remoteValue = response.Result!.Value.GetProperty("value")
            .Deserialize<RemoteValue>(ScryJson.Options)!;
        Assert.Equal("scalar", remoteValue.Kind);
        Assert.Equal(typeof(JsonElement).FullName, remoteValue.Type);
        var snapshot = remoteValue.Value!.Value.Deserialize<WpfSnapshot>(ScryJson.Options)!;
        Assert.Equal("visual", snapshot.TreeKind);
        Assert.NotEmpty(snapshot.Roots);
    }

    /// <summary>
    /// The structured operations run on whichever endpoint thread serves the request, so reading a
    /// property off a DependencyObject used to be impossible without falling back to evaluate.
    /// They now accept the same <c>marshal</c> field execution takes. Asserted as an A/B, because
    /// a test that only checked the marshalled call would pass even if marshalling did nothing.
    /// </summary>
    [Fact]
    public async Task Structured_get_reaches_ui_owned_state_only_when_marshalled()
    {
        await using var host = AgentHost.Start(
            builder => builder.UseWpf(
                fixture.Application,
                adapter => adapter.RegisterWindow("main", fixture.MainWindow)));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);

        // Read through the dispatcher: this test is subject to the very thread affinity it covers.
        var expectedTitle = fixture.Application.Dispatcher.Invoke(() => fixture.MainWindow.Title);

        var reference = await client.RequestAsync(
            "evaluate",
            new ExecutionRequest(
                "System.Windows.Application.Current.MainWindow",
                Marshal: ExecutionMarshalTargets.UiThread));
        Assert.True(reference.Success, reference.Error?.Message);
        var window = reference.Result!.Value.GetProperty("value")
            .Deserialize<RemoteValue>(ScryJson.Options)!
            .Reference;
        Assert.NotNull(window);

        var unmarshalled = await client.RequestAsync(
            "get",
            new { reference = window, member = "Title" });
        Assert.False(unmarshalled.Success);
        Assert.Equal("operation_failed", unmarshalled.Error?.Code);
        Assert.Contains("different thread", unmarshalled.Error!.Message);

        var marshalled = await client.RequestAsync(
            "get",
            new { reference = window, member = "Title", marshal = ExecutionMarshalTargets.UiThread });
        Assert.True(marshalled.Success, marshalled.Error?.Message);
        var title = marshalled.Result!.Value.GetProperty("value")
            .Deserialize<RemoteValue>(ScryJson.Options)!;
        Assert.Equal("scalar", title.Kind);
        Assert.Equal(expectedTitle, title.Value!.Value.GetString());
    }

    /// <summary>
    /// wait marshals each evaluation rather than the polling loop, so a condition can read
    /// UI-owned state without the wait occupying the UI thread between attempts.
    /// </summary>
    [Fact]
    public async Task Wait_can_observe_ui_owned_state_through_marshalled_evaluations()
    {
        await using var host = AgentHost.Start(
            builder => builder.UseWpf(
                fixture.Application,
                adapter => adapter.RegisterWindow("main", fixture.MainWindow)));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);
        var expectedTitle = fixture.Application.Dispatcher.Invoke(() => fixture.MainWindow.Title);

        var response = await client.RequestAsync(
            "wait",
            new ConditionRequest(
                "System.Windows.Application.Current.MainWindow.Title",
                ConditionOperators.EqualTo,
                Expected: JsonSerializer.SerializeToElement(expectedTitle, ScryJson.Options),
                TimeoutMilliseconds: 5000,
                PollIntervalMilliseconds: 50,
                Marshal: ExecutionMarshalTargets.UiThread));

        Assert.True(response.Success, response.Error?.Message);
        Assert.True(response.Result!.Value.GetProperty("satisfied").GetBoolean());
    }

    /// <summary>
    /// A real application's window is far deeper than the toy trees the rest of this fixture uses -
    /// the first one this was tried against measured 114 visual levels. The adapter caps projection
    /// at 32, but a projection nests two JSON levels per tree level (a children array plus a node
    /// object), so 32 levels alone reached System.Text.Json's default MaxDepth of 64 and the
    /// snapshot failed with a misleading "possible object cycle" error. Guards the raised
    /// ScryJson.MaximumJsonDepth.
    /// </summary>
    [Fact]
    public async Task Registered_snapshot_survives_a_tree_deeper_than_the_projection_cap()
    {
        const int depth = 60;
        var deepRoot = await fixture.Dispatcher.InvokeAsync(() =>
        {
            var root = new Border();
            var current = (Decorator)root;
            for (var level = 0; level < depth; level++)
            {
                var child = new Border();
                current.Child = child;
                current = child;
            }

            current.Child = new TextBlock { Text = "bottom" };
            return root;
        });

        await using var host = AgentHost.Start(
            builder => builder.UseWpf(
                fixture.Application,
                adapter => adapter.RegisterRoot("deep", deepRoot)));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);

        var response = await client.RequestAsync(
            "invoke",
            new
            {
                registeredOperation = "wpf.snapshot",
                arguments = new { root = "deep", tree = "visual" }
            });

        Assert.True(response.Success, response.Error?.Message);
        var snapshot = response.Result!.Value.GetProperty("value")
            .Deserialize<RemoteValue>(ScryJson.Options)!
            .Value!.Value
            .Deserialize<WpfSnapshot>(ScryJson.Options)!;

        // Truncated by the adapter's own depth budget, which is the bound that should apply -
        // rather than failing outright in the serializer.
        Assert.True(snapshot.Truncated);
        Assert.True(Flatten(snapshot.Roots).Count() > 8);
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
}

public sealed class WpfFixture : IAsyncLifetime
{
    private Thread? _thread;

    public Application Application { get; private set; } = null!;

    public Window MainWindow { get; private set; } = null!;

    public WpfAdapter Adapter { get; private set; } = null!;

    public WpfDispatcher Dispatcher { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                Application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Dispatcher = new WpfDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher);

                var model = new ViewModel { Caption = "Hello" };
                var editor = new TextBox
                {
                    Name = "editor",
                    Width = 180,
                    Height = 30,
                    DataContext = model
                };
                AutomationProperties.SetAutomationId(editor, "editor-id");
                BindingOperations.SetBinding(
                    editor,
                    TextBox.TextProperty,
                    new Binding(nameof(ViewModel.Caption)) { Mode = BindingMode.OneWay });
                MainWindow = new Window
                {
                    Name = "mainWindow",
                    Title = "Main",
                    Width = 320,
                    Height = 180,
                    Content = new Grid { Children = { editor } }
                };
                Application.MainWindow = MainWindow;
                var secondary = new Window
                {
                    Name = "secondary",
                    Title = "Secondary",
                    Width = 120,
                    Height = 80,
                    ShowInTaskbar = false
                };

                Adapter = new WpfAdapterBuilder(Application)
                    .RegisterWindow("main", MainWindow)
                    .RegisterRoot("drawing", new DrawingVisual())
                    .Build();
                MainWindow.Show();
                secondary.Show();
                completion.SetResult(true);
                Application.Run();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Scry.Wpf.Tests.Dispatcher"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(10))) != completion.Task)
        {
            throw new TimeoutException("The WPF fixture did not start within 10 seconds.");
        }

        await completion.Task;
    }

    public async Task DisposeAsync()
    {
        if (_thread is null)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            foreach (Window window in Application.Windows.Cast<Window>().ToArray())
            {
                window.Close();
            }

            Application.Shutdown();
        });
        Assert.True(_thread.Join(TimeSpan.FromSeconds(10)), "The WPF dispatcher thread did not stop.");
    }
}

public sealed class ViewModel
{
    public string Caption { get; set; } = string.Empty;
}
