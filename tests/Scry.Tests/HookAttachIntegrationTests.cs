#if !NETFRAMEWORK
using System.Diagnostics;
using System.Text.Json;
using Scry.Injector;

namespace Scry.Tests;

/// <summary>
/// End-to-end proof of method hooks in processes that never referenced Scry: the whole chain from
/// the CLI through the injected endpoint to a real Harmony patch applied inside the target, on .NET
/// and on .NET Framework (including a plugin in a non-default AppDomain). Hooks use a persistent
/// session, so each CLI call below is a separate connection that resumes the session named by the
/// hook handle.
/// <para>
/// In the serialized "Scry integration" collection: each test starts a target, attaches, and makes
/// a dozen CLI invocations, and run alongside the other attach and endpoint tests that burst of
/// process starts was enough, on a loaded machine, to make unrelated tests fail publishing their
/// descriptor (<c>File.Move</c> hitting a sharing violation on the file it had just written).
/// </para>
/// </summary>
[Collection("Scry integration")]
public sealed class HookAttachIntegrationTests
{
    [Theory]
    [InlineData("net8.0")]
    [InlineData("net462")]
    public async Task Hooks_record_calls_in_an_attached_process_and_removal_restores_the_method(string framework)
    {
        if (!CanAttach())
        {
            return;
        }

        var root = AttachIntegrationTests.FindRepositoryRoot();
        var cli = CliPath(root);
        var directory = Path.Combine(root, "samples", "Scry.AttachTarget", "bin", "Release", framework);
        var (fileName, arguments) = framework == "net8.0"
            ? ("dotnet", $"\"{Path.Combine(directory, "Scry.AttachTarget.dll")}\"")
            : (Path.Combine(directory, "Scry.AttachTarget.exe"), string.Empty);
        Assert.True(
            File.Exists(framework == "net8.0" ? Path.Combine(directory, "Scry.AttachTarget.dll") : fileName),
            $"Attach target is missing for {framework}: {directory}");

        using var process = StartTarget(fileName, arguments);
        try
        {
            await ExpectProcessIdAsync(process);
            var alias = $"hooks-{framework}-{Guid.NewGuid():N}";
            await CliAsync(cli, $"attach {process.Id} --alias {alias}", null, 60);

            await ExerciseHooksAsync(cli, alias, "Scry.AttachTarget.HookProbe");
        }
        finally
        {
            Kill(process);
        }
    }

    /// <summary>
    /// The motivating shape: a plugin in a second AppDomain of a .NET Framework process. The hook is
    /// applied where the type lives - through a sibling endpoint started with <c>appdomain.start</c>,
    /// which loads Harmony into that AppDomain - and the default domain, which cannot see the type,
    /// refuses to hook it.
    /// </summary>
    [Fact]
    public async Task Hooks_apply_in_a_non_default_appdomain_through_a_sibling_endpoint()
    {
        if (!CanAttach())
        {
            return;
        }

        var root = AttachIntegrationTests.FindRepositoryRoot();
        var cli = CliPath(root);
        var exe = Path.Combine(
            root, "samples", "Scry.MultiDomainAttachTarget", "bin", "Release", "net462",
            "Scry.MultiDomainAttachTarget.exe");
        Assert.True(File.Exists(exe), $"Multi-domain attach target is missing: {exe}");

        using var process = StartTarget(exe, string.Empty);
        try
        {
            await ExpectProcessIdAsync(process);
            var defaultAlias = $"hooks-md-default-{Guid.NewGuid():N}";
            await CliAsync(cli, $"attach {process.Id} --alias {defaultAlias}", null, 60);

            var invisible = await AttachIntegrationTests.RunCliAsync(
                cli,
                $"hooks add --target {defaultAlias}",
                JsonSerializer.Serialize(new { type = PluginWorkerType, method = "PushAndAwait" }),
                TimeSpan.FromSeconds(30));
            Assert.Equal(5, invisible.ExitCode);
            using (var refused = JsonDocument.Parse(invisible.StandardOutput))
            {
                Assert.Equal("type_not_found", refused.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            var started = await CliAsync(
                cli,
                $"appdomain.start --target {defaultAlias}",
                """{"selector":"PluginDomain"}""",
                30);
            var siblingAlias = started.GetProperty("result").GetProperty("alias").GetString()!;

            await ExerciseHooksAsync(cli, siblingAlias, PluginWorkerType);
        }
        finally
        {
            Kill(process);
        }
    }

    private const string PluginWorkerType = "Scry.MultiDomainAttachTarget.Plugin.PluginWorker";

    private static async Task ExerciseHooksAsync(string cli, string alias, string type)
    {
        var added = await CliAsync(
            cli,
            $"hooks add --target {alias}",
            JsonSerializer.Serialize(new { type, method = "PushAndAwait", parameterTypes = new[] { "string[]" } }),
            60);
        var addResult = added.GetProperty("result");
        Assert.True(addResult.GetProperty("created").GetBoolean());
        Assert.StartsWith("2.4.2", addResult.GetProperty("harmony").GetProperty("version").GetString());
        Assert.Equal("none", addResult.GetProperty("inlining").GetProperty("risk").GetString());
        var hook = addResult.GetProperty("hook");
        var sessionId = hook.GetProperty("sessionId").GetString()!;

        // The patch really is in the target process, applied by this runtime's Harmony.
        var patched = await EvaluatePatchedAsync(cli, alias, type);
        Assert.True(patched, "Harmony reports no patch on the hooked method inside the target.");

        // The handle carries the session, so this separate CLI process resumes it.
        var waited = await CliAsync(
            cli,
            $"hooks wait --target {alias}",
            JsonSerializer.Serialize(new
            {
                hook,
                predicate = "Convert.ToInt32(((string[])Args[0])[0].Substring(1)) % 2 == 0 && (bool)ReturnValue",
                timeoutMilliseconds = 30_000
            }),
            60);
        var waitResult = waited.GetProperty("result");
        Assert.True(waitResult.GetProperty("satisfied").GetBoolean());
        var call = waitResult.GetProperty("call");
        Assert.Equal("returned", call.GetProperty("outcome").GetString());
        Assert.True(call.GetProperty("returnValue").GetProperty("value").GetBoolean());
        Assert.Equal("System.String[]", call.GetProperty("arguments")[0].GetProperty("type").GetString());

        var read = await CliAsync(
            cli,
            $"hooks read --target {alias}",
            JsonSerializer.Serialize(new { hook, limit = 50 }),
            30);
        var calls = read.GetProperty("result").GetProperty("calls").EnumerateArray().ToArray();
        Assert.NotEmpty(calls);
        Assert.Equal(
            calls.Select(item => item.GetProperty("sequence").GetInt64()).OrderBy(sequence => sequence),
            calls.Select(item => item.GetProperty("sequence").GetInt64()));

        var listed = await CliAsync(
            cli,
            $"hooks list --target {alias} --session {sessionId}",
            "{}",
            30);
        Assert.Single(listed.GetProperty("result").GetProperty("hooks").EnumerateArray());

        var removed = await CliAsync(
            cli,
            $"hooks remove --target {alias}",
            JsonSerializer.Serialize(new { hook }),
            30);
        Assert.True(removed.GetProperty("result").GetProperty("removed").GetBoolean());

        var gone = await AttachIntegrationTests.RunCliAsync(
            cli,
            $"hooks read --target {alias}",
            JsonSerializer.Serialize(new { hook }),
            TimeSpan.FromSeconds(30));
        Assert.Equal(5, gone.ExitCode);
        using (var goneJson = JsonDocument.Parse(gone.StandardOutput))
        {
            Assert.Equal("hook_not_found", goneJson.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        Assert.False(
            await EvaluatePatchedAsync(cli, alias, type),
            "The method is still patched inside the target after the hook was removed.");
    }

    private static async Task<bool> EvaluatePatchedAsync(string cli, string alias, string type)
    {
        var script = $$"""
            var method = typeof({{type}}).GetMethod(
                "PushAndAwait",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null,
                new[] { typeof(string[]) },
                null);
            var info = HarmonyLib.Harmony.GetPatchInfo(method);
            return info != null && info.Owners.Contains("scry.net.hooks");
            """;
        var evaluated = await CliAsync(cli, $"evaluate --target {alias}", script, 60);
        return evaluated.GetProperty("result").GetProperty("value").GetProperty("value").GetBoolean();
    }

    private static bool CanAttach() =>
        OperatingSystem.IsWindows() && ProcessInspector.CurrentArchitecture == TargetArchitecture.X64;

    private static string CliPath(string root)
    {
        var cli = Path.Combine(root, "src", "Scry.Cli", "bin", "Release", "net8.0", "scry.dll");
        Assert.True(
            File.Exists(Path.Combine(Path.GetDirectoryName(cli)!, "native", "win-x64", "Scry.Injector.Native.dll")),
            "Build the native x64 helper before running the attach integration test.");
        return cli;
    }

    private static Process StartTarget(string fileName, string arguments) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"Could not start {fileName}.");

    private static async Task ExpectProcessIdAsync(Process process)
    {
        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(process.Id.ToString(), await process.StandardOutput.ReadLineAsync(startupTimeout.Token));
    }

    private static void Kill(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }

    private static async Task<JsonElement> CliAsync(string cli, string arguments, string? input, int timeoutSeconds)
    {
        var result = await AttachIntegrationTests.RunCliAsync(cli, arguments, input, TimeSpan.FromSeconds(timeoutSeconds));
        Assert.True(
            result.ExitCode == 0,
            $"`scry {arguments}` failed ({result.ExitCode}): {result.StandardOutput}{result.StandardError}");
        using var json = JsonDocument.Parse(result.StandardOutput);
        return json.RootElement.Clone();
    }
}
#endif
