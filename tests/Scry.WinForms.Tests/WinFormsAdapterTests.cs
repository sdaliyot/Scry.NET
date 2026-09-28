using System.Text.Json;
using System.Windows.Forms;
using Scry.Contracts;
using Scry.WinForms;

namespace Scry.WinForms.Tests;

public sealed class WinFormsAdapterTests
{
    [Fact]
    public async Task Snapshot_marshals_to_owner_and_projects_controls_bindings_and_menus()
    {
        await using var fixture = await WinFormsFixture.StartAsync();
        var snapshot = await fixture.Adapter.SnapshotAsync(
            JsonSerializer.SerializeToElement(new { root = "main" }, ScryJson.Options));

        Assert.Equal("managed-control-hierarchy", snapshot.Projection);
        var form = Assert.Single(snapshot.Roots);
        Assert.Equal("main", form.Name);
        var button = Assert.Single(form.Children, item => item.Name == "save");
        Assert.Equal("save", button.Name);
        Assert.Equal("Save", button.Text);
        Assert.Single(button.Bindings);
        var strip = Assert.Single(form.Children, item => item.Name == "menu");
        Assert.Single(strip.MenuItems);
        Assert.Contains("not a complete native window tree", snapshot.Limitation);
    }

    [Fact]
    public async Task Registered_open_forms_are_projected_once()
    {
        await using var fixture = await WinFormsFixture.StartAsync();

        var snapshot = await fixture.Adapter.SnapshotAsync(
            JsonSerializer.SerializeToElement(new { }, ScryJson.Options));

        Assert.Single(snapshot.Roots);
        Assert.Equal("root:main", snapshot.Roots[0].Path);
    }

    [Fact]
    public void Dispatcher_requires_an_owner_with_a_created_handle()
    {
        using var control = new Control();

        var exception = Assert.Throws<InvalidOperationException>(() => new WinFormsDispatcher(control));

        Assert.Contains("created handle", exception.Message);
    }

    /// <summary>
    /// Guards against a real regression: the marshal owner is captured once at injection time and
    /// never otherwise re-evaluated, so an application whose first visible window is transient - a
    /// login dialog that closes once the main window appears is the motivating case - used to wedge
    /// permanently. Once the original owner's handle was destroyed, every future
    /// <see cref="WinFormsDispatcher.InvokeAsync{T}"/> call threw against it forever, including the
    /// very <c>winforms.wait</c> poll a caller would use to detect the transition, with no way to
    /// recover from outside the target process. <see cref="WinFormsDispatcher"/> now re-resolves a
    /// currently-valid owner from <c>Application.OpenForms</c> (via the same heuristic
    /// <see cref="WinFormsOwnerSelection"/> uses at initial wiring) instead of throwing immediately.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_recovers_when_the_owner_form_closes_and_another_is_open()
    {
        var ready = new TaskCompletionSource<(WinFormsDispatcher Dispatcher, Form First, Form Second)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Application.Run() (parameterless), not Application.Run(firstForm) - the latter would tie
        // the message loop's lifetime to the first form and exit the whole thread when it closes,
        // which is exactly the transition this test needs to survive.
        var thread = new Thread(() =>
        {
            try
            {
                var first = new Form { Name = "first", Text = "First" };
                first.Show();
                var second = new Form { Name = "second", Text = "Second" };
                second.Show();

                var dispatcher = new WinFormsDispatcher(first);
                ready.SetResult((dispatcher, first, second));
                Application.Run();
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(5))) != ready.Task)
        {
            throw new TimeoutException("The fixture did not start within 5 seconds.");
        }

        var (dispatcher, first, second) = await ready.Task;
        try
        {
            // Close the original owner while a second form is still open and visible - the handle
            // is destroyed synchronously as part of Close(), so the very next call must recover
            // rather than throw against a form that no longer exists.
            await dispatcher.InvokeAsync(() =>
            {
                first.Close();
                return true;
            });

            var text = await dispatcher.InvokeAsync(() => second.Text);
            Assert.Equal("Second", text);
        }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                Application.ExitThread();
                return true;
            });
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Projection_is_bounded_and_reports_truncation()
    {
        await using var fixture = await WinFormsFixture.StartAsync(maximumNodes: 1);
        var snapshot = await fixture.Adapter.SnapshotAsync(
            JsonSerializer.SerializeToElement(new { root = "main" }, ScryJson.Options));

        Assert.True(snapshot.Truncated);
        Assert.Equal(1, snapshot.NodesVisited);
        Assert.Empty(Assert.Single(snapshot.Roots).Children);

        var result = await fixture.Adapter.WaitAsync(
            new WinFormsCondition(Root: "main", Name: "missing", State: "notExists"),
            TimeSpan.Zero);
        Assert.False(result.Satisfied);
        Assert.False(result.Conclusive);
    }

    [Fact]
    public async Task Wait_and_assert_use_projected_control_state()
    {
        await using var fixture = await WinFormsFixture.StartAsync();

        var result = await fixture.Adapter.WaitAsync(
            new WinFormsCondition(Root: "main", Name: "save", State: "textEquals", Expected: "Save"),
            TimeSpan.FromSeconds(1));

        Assert.True(result.Satisfied);
        Assert.True(result.Conclusive);
        Assert.Equal("save", result.Match?.Name);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Adapter.AssertAsync(
                JsonSerializer.SerializeToElement(
                    new { root = "main", name = "missing", state = "exists" },
                    ScryJson.Options),
                CancellationToken.None));
    }

    [Fact]
    public async Task Screenshot_returns_a_bounded_png_with_limitations()
    {
        await using var fixture = await WinFormsFixture.StartAsync();

        var screenshot = (WinFormsScreenshot?)await fixture.Adapter.ScreenshotAsync(
            JsonSerializer.SerializeToElement(new { root = "main" }, ScryJson.Options),
            CancellationToken.None);

        Assert.NotNull(screenshot);
        Assert.Equal("image/png", screenshot.MediaType);
        Assert.NotEmpty(screenshot.Base64Data);
        Assert.Contains("owner-drawn", screenshot.Limitation);
    }

    [Fact]
    public async Task Screenshot_rejects_an_encoded_payload_over_the_byte_limit()
    {
        await using var fixture = await WinFormsFixture.StartAsync(maximumScreenshotBytes: 1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Adapter.ScreenshotAsync(
                JsonSerializer.SerializeToElement(new { root = "main" }, ScryJson.Options),
                CancellationToken.None));

        Assert.Contains("encoded screenshot", exception.Message);
    }

    private sealed class WinFormsFixture : IAsyncDisposable
    {
        private readonly Thread _thread;
        private readonly ApplicationContext _context;

        private WinFormsFixture(
            Thread thread,
            ApplicationContext context,
            WinFormsAdapter adapter)
        {
            _thread = thread;
            _context = context;
            Adapter = adapter;
        }

        public WinFormsAdapter Adapter { get; }

        public static async Task<WinFormsFixture> StartAsync(
            int maximumNodes = 128,
            int maximumScreenshotBytes = 4 * 1024 * 1024)
        {
            var completion = new TaskCompletionSource<WinFormsFixture>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Thread? thread = null;
            thread = new Thread(() =>
            {
                try
                {
                    var form = new Form { Name = "main", Text = "Main" };
                    var model = new BindingModel { Caption = "Save" };
                    var button = new Button { Name = "save" };
                    button.DataBindings.Add(nameof(Control.Text), model, nameof(BindingModel.Caption));
                    var menu = new MenuStrip { Name = "menu" };
                    menu.Items.Add(new ToolStripMenuItem("File") { Name = "file" });
                    form.Controls.Add(button);
                    form.Controls.Add(menu);
                    _ = form.Handle;

                    var context = new ApplicationContext(form);
                    var adapter = new WinFormsAdapterBuilder(
                        form,
                        new WinFormsAdapterOptions
                        {
                            MaximumNodes = maximumNodes,
                            MaximumScreenshotBytes = maximumScreenshotBytes
                        })
                        .RegisterRoot("main", form)
                        .Build();
                    form.Show();
                    completion.SetResult(new(thread!, context, adapter));
                    Application.Run(context);
                    form.Dispose();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            if (await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5))) != completion.Task)
            {
                throw new TimeoutException("The Windows Forms fixture did not start within 5 seconds.");
            }

            return await completion.Task;
        }

        public async ValueTask DisposeAsync()
        {
            await Adapter.SnapshotAsync(
                JsonSerializer.SerializeToElement(new { root = "main" }, ScryJson.Options));
            _context.MainForm?.BeginInvoke((MethodInvoker)_context.ExitThread);
            await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    private sealed class BindingModel
    {
        public string Caption { get; set; } = string.Empty;
    }
}
