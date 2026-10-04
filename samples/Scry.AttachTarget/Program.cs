using System.Diagnostics;

using var process = Process.GetCurrentProcess();
Console.WriteLine(process.Id);
Console.Out.Flush();
new Scry.AttachTarget.HookProbe().Start();
await Task.Delay(Timeout.InfiniteTimeSpan);
