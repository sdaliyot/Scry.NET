using System.Diagnostics;

// A local proof for AppDomain targeting, before any of it touches a real server: this process
// carries a second AppDomain with a type in it that the default domain provably cannot see, so
// attaching with --appdomain (or appdomain.list/appdomain.start after an ordinary attach) has
// something concrete to demonstrate against - "does find-types now show PluginMarker" is a much
// stronger check than "did the attach return success".
using var process = Process.GetCurrentProcess();

var pluginAssemblyPath = Path.Combine(
    AppContext.BaseDirectory, "Scry.MultiDomainAttachTarget.Plugin.dll");

// CreateInstanceFrom, not CreateInstanceFromAndUnwrap: the handle is never unwrapped, so
// PluginMarker never needs to be MarshalByRefObject or [Serializable] - it only ever needs to
// exist, loaded, inside PluginDomain.
var pluginDomain = AppDomain.CreateDomain("PluginDomain");
pluginDomain.CreateInstanceFrom(
    pluginAssemblyPath, "Scry.MultiDomainAttachTarget.Plugin.PluginMarker");

Console.WriteLine(process.Id);
Console.Out.Flush();
await Task.Delay(Timeout.InfiniteTimeSpan);
