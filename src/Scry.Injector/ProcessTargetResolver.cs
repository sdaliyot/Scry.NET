using System.Diagnostics;

namespace Scry.Injector;

public static class ProcessTargetResolver
{
    public static int Resolve(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InjectionException(
                InjectionErrorCode.TargetNotFound,
                "A process ID or process name is required.");
        }

        if (int.TryParse(target, out var processId))
        {
            if (processId <= 0)
            {
                throw new InjectionException(
                    InjectionErrorCode.TargetNotFound,
                    $"Process ID '{target}' is invalid.");
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                _ = process.HasExited;
                return processId;
            }
            catch (ArgumentException exception)
            {
                throw new InjectionException(
                    InjectionErrorCode.TargetNotFound,
                    $"Process {processId} does not exist.",
                    innerException: exception);
            }
        }

        var processName = Path.GetFileNameWithoutExtension(target);
        var matches = Process.GetProcessesByName(processName);
        try
        {
            return matches.Length switch
            {
                1 => matches[0].Id,
                0 => throw new InjectionException(
                    InjectionErrorCode.TargetNotFound,
                    $"No process named '{processName}' is running."),
                _ => throw new InjectionException(
                    InjectionErrorCode.AmbiguousTarget,
                    $"Process name '{processName}' matches {matches.Length} processes; use a process ID.")
            };
        }
        finally
        {
            foreach (var process in matches)
            {
                process.Dispose();
            }
        }
    }
}
