using System.Text.Json;
using Scry.Contracts;
using Scry.Injector;
using Scry.Client;

return await InjectorCli.RunAsync(args);

internal static class InjectorCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        AttachArguments parsed;
        try
        {
            parsed = AttachCommandLine.Parse(args);
        }
        catch (AttachUsageException exception)
        {
            Write(new
            {
                success = false,
                error = new
                {
                    code = "usage_error",
                    message = exception.Message +
                        " Usage: scry-injector " + AttachCommandLine.Usage + "."
                }
            });
            return 2;
        }

        try
        {
            var result = await AttachService.AttachAsync(
                parsed.Target,
                parsed.Alias,
                parsed.Adapters).ConfigureAwait(false);
            if (!result.Success)
            {
                Write(result);
                return 3;
            }

            await using var client = await ScryClient.ConnectAsync(
                result.Descriptor!,
                clientName: "scry-injector",
                ephemeralSession: true).ConfigureAwait(false);
            Write(new
            {
                success = true,
                target = result.Target,
                descriptorPath = result.DescriptorPath,
                handshake = client.Handshake
            });
            return 0;
        }
        catch (InjectionException exception)
        {
            Write(AttachResult.Failed(exception.ToError()));
            return 3;
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or
            OperationCanceledException or ProtocolException or ScryRemoteException)
        {
            Write(AttachResult.Failed(
                new InjectionError(
                    InjectionErrorCode.HandshakeFailed,
                    exception.Message)));
            return 4;
        }
    }

    private static void Write<T>(T value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, ScryJson.Options));
}
