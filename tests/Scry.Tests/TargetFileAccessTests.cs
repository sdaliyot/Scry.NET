using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Scry.Injector;

namespace Scry.Tests;

/// <summary>
/// The pre-injection check that the target's own account can read the helper and payload. These run
/// against this test process's token: a file whose DACL denies the current user stands in for a
/// scry install a service account cannot read, which is the same AccessCheck evaluation either way.
/// Every test also returns early off Windows; the attribute only tells the platform analyzer what
/// those guards already ensure, which it cannot see through a helper method.
/// </summary>
#if !NETFRAMEWORK
[SupportedOSPlatform("windows")]
#endif
public sealed class TargetFileAccessTests
{
    [Fact]
    public void A_readable_file_is_not_reported()
    {
        if (!IsWindows())
        {
            return;
        }

        var path = CreateTempFile();
        try
        {
            Assert.Null(TargetFileAccess.FindDenied(
                Process.GetCurrentProcess().Id,
                new[] { (path, TargetFileAccess.ReadAndExecuteAccess) }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_the_account_cannot_read_is_reported()
    {
        if (!IsWindows())
        {
            return;
        }

        var readable = CreateTempFile();
        var denied = CreateTempFile();
        DenyRead(denied);
        try
        {
            Assert.Equal(
                denied,
                TargetFileAccess.FindDenied(
                    Process.GetCurrentProcess().Id,
                    new[]
                    {
                        (readable, TargetFileAccess.ReadAccess),
                        (denied, TargetFileAccess.ReadAccess)
                    }));
        }
        finally
        {
            RestoreRead(denied);
            File.Delete(denied);
            File.Delete(readable);
        }
    }

    [Fact]
    public void An_unanswerable_question_fails_open()
    {
        if (!IsWindows())
        {
            return;
        }

        var denied = CreateTempFile();
        DenyRead(denied);
        try
        {
            // No such process: the token cannot be opened, so the answer is unknown, never "denied".
            Assert.Null(TargetFileAccess.FindDenied(
                int.MaxValue - 1,
                new[] { (denied, TargetFileAccess.ReadAccess) }));

            // No such file: the descriptor cannot be read, which is unknown too.
            Assert.Null(TargetFileAccess.FindDenied(
                Process.GetCurrentProcess().Id,
                new[] { (denied + ".missing", TargetFileAccess.ReadAccess) }));
        }
        finally
        {
            RestoreRead(denied);
            File.Delete(denied);
        }
    }

#if !NETFRAMEWORK
    [Fact]
    public async Task Attach_refuses_before_injecting_when_the_target_cannot_read_the_helper()
    {
        if (!OperatingSystem.IsWindows() ||
            ProcessInspector.CurrentArchitecture != TargetArchitecture.X64)
        {
            return;
        }

        var repository = AttachIntegrationTests.FindRepositoryRoot();
        var staged = Path.Combine(repository, "src", "Scry.Cli", "bin", "Release", "net8.0");
        var targetAssembly = Path.Combine(
            repository, "samples", "Scry.AttachTarget", "bin", "Release", "net8.0", "Scry.AttachTarget.dll");
        Assert.True(File.Exists(targetAssembly), $"Attach target is missing: {targetAssembly}");

        // Only what ResolveComponents insists on: the check runs before any of it is used.
        var root = Path.Combine(Path.GetTempPath(), $"scry-unreadable-{Guid.NewGuid():N}");
        var helper = Path.Combine(root, "native", "win-x64", "Scry.Injector.Native.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
        Directory.CreateDirectory(Path.Combine(root, "payload", "net"));
        File.Copy(Path.Combine(staged, "native", "win-x64", "Scry.Injector.Native.dll"), helper);
        foreach (var name in new[] { "Scry.Injector.Payload.dll", "Scry.Injector.Payload.runtimeconfig.json" })
        {
            File.Copy(Path.Combine(staged, "payload", "net", name), Path.Combine(root, "payload", "net", name));
        }

        DenyRead(helper);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{targetAssembly}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the attach target.");

        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Equal(
                process.Id.ToString(),
                await process.StandardOutput.ReadLineAsync(startupTimeout.Token));

            var result = await AttachService.AttachAsync(
                process.Id,
                new AttachOptions { ComponentRoot = root });

            Assert.False(result.Success);
            Assert.Equal("target_cannot_read_components", result.Error!.Code);
            Assert.Contains(helper, result.Error.Message);
            Assert.Contains("icacls", result.Error.Message);

            // Refused before anything was written into the target, not after a failed load.
            process.Refresh();
            Assert.DoesNotContain(
                process.Modules.Cast<ProcessModule>(),
                module => string.Equals(
                    module.ModuleName, "Scry.Injector.Native.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            RestoreRead(helper);
            Directory.Delete(root, recursive: true);
        }
    }
#endif

    private static bool IsWindows() =>
#if NETFRAMEWORK
        Environment.OSVersion.Platform == PlatformID.Win32NT;
#else
        OperatingSystem.IsWindows();
#endif

    private static string CreateTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scry-access-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    private static FileSystemAccessRule DenyReadRule() =>
        new(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);

    private static void DenyRead(string path)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        security.AddAccessRule(DenyReadRule());
        file.SetAccessControl(security);
    }

    private static void RestoreRead(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return;
        }

        var security = file.GetAccessControl();
        security.RemoveAccessRuleAll(DenyReadRule());
        file.SetAccessControl(security);
    }
}
