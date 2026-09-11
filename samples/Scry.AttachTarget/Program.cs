using System.Diagnostics;

using var process = Process.GetCurrentProcess();
Console.WriteLine(process.Id);
Console.Out.Flush();
await Task.Delay(Timeout.InfiniteTimeSpan);
