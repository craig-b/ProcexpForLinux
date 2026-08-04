using Procexp.Helper;
using Procexp.Privileged;

// The privileged helper daemon.
//
// Far smaller than the macOS XPC helper it replaces: on Linux most of /proc is
// world-readable, so this exists only for the three per-process files the kernel
// restricts to the owning uid — io, smaps_rollup and environ — plus signalling
// processes belonging to other users.
//
// Runs under systemd as root. See docs/HELPER.md for the trust model.

if (!Environment.IsPrivilegedProcess)
{
    Console.Error.WriteLine("procexp-helper must run as root.");
    return 1;
}

void Log(string message)
{
    // systemd captures stdout into the journal, so plain writes are the whole
    // logging story. Timestamps come from the journal.
    Console.WriteLine(message);
    Console.Out.Flush();
}

using var lifetime = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    lifetime.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) => lifetime.Cancel();

Log($"procexp-helper starting, protocol version {HelperConstants.ProtocolVersion}");

var service = new HelperService(Log);

try
{
    await service.RunAsync(lifetime.Token);
}
catch (OperationCanceledException)
{
    // Normal shutdown.
}
finally
{
    try
    {
        if (File.Exists(HelperConstants.SocketPath))
        {
            File.Delete(HelperConstants.SocketPath);
        }
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        // Best effort; systemd's RuntimeDirectory cleanup covers the rest.
    }
}

Log("procexp-helper stopped");
return 0;
