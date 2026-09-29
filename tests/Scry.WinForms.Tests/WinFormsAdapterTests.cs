using System.Text.Json;
using System.Windows.Forms;
using Scry.Contracts;
using Scry.Endpoint;
using Scry.Client;
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

    /// <summary>
    /// Reported regression: a single post-recovery call succeeding (as the test above verifies) is
    /// not sufficient - a real Control Center session reportedly stays permanently broken after its
    /// first recovery, with every subsequent call (even much later, even a no-op evaluate) failing
    /// with execution_timed_out, as if the recovered owner or its BeginInvoke plumbing silently stops
    /// completing after exactly one successful post-recovery call. This drives many InvokeAsync calls
    /// through the recovered owner, each separated by a real delay (letting the message pump idle
    /// between calls, not just DoEvents within a single call), to see whether it degrades after the
    /// first one.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_keeps_working_across_many_calls_after_recovery()
    {
        var ready = new TaskCompletionSource<(WinFormsDispatcher Dispatcher, Form First, Form Second)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

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
            await dispatcher.InvokeAsync(() =>
            {
                first.Close();
                return true;
            });

            // The first post-recovery call, as covered by the test above.
            Assert.Equal("Second", await dispatcher.InvokeAsync(() => second.Text));

            // Many further calls, each after a real delay so the UI thread's message pump goes idle
            // between them - not just one immediate follow-up call.
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(50);
                var callTask = dispatcher.InvokeAsync(() => second.Text);
                var completed = await Task.WhenAny(callTask, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.True(
                    completed == callTask,
                    $"InvokeAsync call #{i} after recovery did not complete within 5 seconds - " +
                    "the recovered owner appears to have stopped marshaling calls.");
                Assert.Equal("Second", await callTask);
            }
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

    /// <summary>
    /// Guards against a real regression, distinct from the one above: closing the owner form when
    /// *no other form is open yet* makes <c>WinFormsDispatcher.ResolveOwner</c> throw (there is
    /// nothing to recover through in that instant), and that throw used to be a bare
    /// <see cref="InvalidOperationException"/> - not classifiable by
    /// <c>OperationDispatcher.EvaluateConditionAsync</c>'s <c>wait</c>-loop tolerance (added in
    /// 92b9043 for <c>execution_timed_out</c>), so it fell into the generic <c>operation_failed</c>
    /// bucket and failed the whole <c>wait</c> outright on the very first iteration that hit the
    /// gap. This is exactly the case the owner-recovery fix (4f17386) was meant to handle
    /// end-to-end: a login dialog closes, its replacement hasn't appeared yet, and a caller
    /// <c>wait</c>ing for the new window should keep polling through that gap, not fail.
    /// <para>
    /// Exercises the *generic* <c>wait</c> operation (<c>OperationDispatcher.EvaluateConditionAsync</c>,
    /// via <c>"marshal": "ui"</c>) rather than <c>WinFormsAdapter</c>'s own separate
    /// <c>winforms.wait</c> polling loop, which is a different mechanism with no
    /// <c>execution_timed_out</c>/<c>dispatcher_owner_unavailable</c> tolerance of its own - the
    /// bug report and fix are specifically about the generic path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Wait_survives_a_zero_forms_gap_when_a_replacement_form_opens_in_time()
    {
        Form? first = null;
        Form? second = null;
        const string condition =
            "System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>()" +
            ".Any(f => f.Name == \"second\")";
        var uiReady = new TaskCompletionSource<Form>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Set by the test *after* it has confirmed "first" is closed (see below) - read by the
        // polling timer on the UI thread. This is what makes the gap deterministic rather than a
        // wall-clock guess: a cold Roslyn compile can itself take several seconds under load
        // (observed directly while writing this test), which would blow past any fixed delay
        // chosen up front. Ticking every 50ms and only *counting* once armed decouples "how long
        // until second opens" from "how long the warmup/close before it took".
        var armedAt = -1;
        var ticks = 0;

        var thread = new Thread(() =>
        {
            try
            {
                first = new Form { Name = "first", Text = "First" };
                first.Show();
                uiReady.SetResult(first);

                // Polls the flag above from this UI thread's own message loop, rather than being
                // marshalled in from outside - closing "first" removes the only Control this test
                // could otherwise BeginInvoke through.
                var pollTimer = new System.Windows.Forms.Timer { Interval = 50 };
                pollTimer.Tick += (_, _) =>
                {
                    ticks++;
                    if (armedAt < 0)
                    {
                        return;
                    }

                    if (ticks - armedAt >= 10) // ~500ms after arming
                    {
                        pollTimer.Stop();
                        second = new Form { Name = "second", Text = "Second" };
                        second.Show();
                    }
                };
                pollTimer.Start();

                Application.Run();
            }
            catch (Exception exception)
            {
                uiReady.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (await Task.WhenAny(uiReady.Task, Task.Delay(TimeSpan.FromSeconds(5))) != uiReady.Task)
        {
            throw new TimeoutException("The fixture did not start within 5 seconds.");
        }

        var firstForm = await uiReady.Task;
        await using var host = EndpointHost.Start(builder => builder.UseWinForms(firstForm));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);

        try
        {
            // Compile the condition, then close "first", both marshalled through it while it is
            // still a valid owner - mirrors ConditionOperationTests.
            // Wait_polls_until_a_condition_becomes_true's own "compile before timing anything"
            // reasoning, though here it matters even more: only once this returns do we arm the
            // "open second" countdown, so however long the cold compile actually took is
            // irrelevant to whether the gap below is hit.
            await client.EvaluateAsync(
                new ExecutionRequest(condition, Marshal: ExecutionMarshalTargets.UiThread));
            await client.EvaluateAsync(new ExecutionRequest(
                "System.Windows.Forms.Application.OpenForms[0].Close(); true",
                Marshal: ExecutionMarshalTargets.UiThread));
            armedAt = ticks;

            // No form is open at all right now - "first" just closed above, and "second" cannot
            // appear for ~500ms of poll-timer ticks counted from this exact point - so the wait's
            // very first poll is guaranteed to hit the zero-forms gap, not merely likely to.
            var result = await client.RequestAsync(
                "wait",
                new ConditionRequest(
                    condition,
                    Expected: JsonDocument.Parse("true").RootElement,
                    TimeoutMilliseconds: 15_000,
                    PollIntervalMilliseconds: 50,
                    Marshal: ExecutionMarshalTargets.UiThread));

            Assert.True(result.Success, result.Error?.Message);
            Assert.True(result.Result!.Value.GetProperty("satisfied").GetBoolean());
        }
        finally
        {
            await client.EvaluateAsync(new ExecutionRequest(
                "System.Windows.Forms.Application.ExitThread(); true",
                Marshal: ExecutionMarshalTargets.UiThread));
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Guards against a real regression found live in a Control Center process stuck permanently
    /// broken: <c>Application.OpenForms</c> can contain a form whose native window Win32 itself has
    /// already destroyed - here, a Syncfusion <c>XPThemes.ThemeChangeListenerForm</c>, a hidden
    /// helper window created once at process startup, well before the real main form - while .NET's
    /// own <see cref="Control.IsHandleCreated"/>/<see cref="Control.IsDisposed"/> bookkeeping still
    /// reports it as live, because that bookkeeping only updates via message processing on the
    /// thread that created the window, and that thread had already exited without ever running one.
    /// <para>
    /// Reproduced here by creating a decoy <see cref="Form"/> on a throwaway thread that creates its
    /// handle (which registers it in <c>Application.OpenForms</c> - a <see cref="Form"/> does this
    /// itself on handle creation, whether or not <c>Show()</c> is ever called) and then simply ends
    /// without ever pumping messages for it - the OS destroys a thread's windows when it exits, but
    /// nothing ever tells the <see cref="Form"/> object, so it keeps reporting itself as created and
    /// not disposed forever after.
    /// </para>
    /// <para>
    /// Before the fix, <c>WinFormsOwnerSelection.SelectOwner</c> trusted exactly those two flags:
    /// if the real main form had not yet turned <see cref="Control.Visible"/> at the moment
    /// selection ran (the exact race that hit this in Control Center - a login dialog closing before
    /// its replacement appears), "prefer first visible" found nothing and fell back to "first of any
    /// visibility" by creation order - the already-dead decoy, since it was created first. Once
    /// picked, nothing ever re-validated it, so the marshal owner was stuck on a window that could
    /// never again receive a posted message.
    /// </para>
    /// </summary>
    [Fact]
    public void SelectOwner_skips_a_form_whose_native_window_is_already_destroyed()
    {
        Form? decoy = null;
        var decoyThread = new Thread(() =>
        {
            decoy = new Form { Name = "decoy" };
            _ = decoy.Handle; // Registers it in Application.OpenForms without ever showing it.
        });
        decoyThread.SetApartmentState(ApartmentState.STA);
        decoyThread.Start();
        decoyThread.Join(TimeSpan.FromSeconds(5));

        Assert.NotNull(decoy);
        // The decoy's owning thread has now exited without ever running a message loop for it, so
        // the OS has already torn its window down - but .NET's own bookkeeping doesn't know that.
        Assert.True(decoy!.IsHandleCreated);
        Assert.False(decoy.IsDisposed);
        Assert.False(WinFormsOwnerSelection.IsAlive(decoy));

        using var main = new Form { Name = "main" };
        _ = main.Handle; // Alive, but - matching the real race - not yet Visible either.
        Assert.True(WinFormsOwnerSelection.IsAlive(main));

        // Neither is visible, so this only exercises the "first of any visibility" fallback - the
        // one the real bug fell through to. "decoy" is first by creation order; the fix must still
        // skip it as dead and land on "main" rather than the fallback's naive first-of-any-visibility
        // pick.
        var selected = WinFormsOwnerSelection.SelectOwner(new[] { decoy, main });

        Assert.Same(main, selected);
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
