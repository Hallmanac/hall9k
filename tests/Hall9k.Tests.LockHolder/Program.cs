// A standalone process whose only job is to hold the exact exclusive-file-lock primitive
// CrossProcessContainerGate uses (FileShare.None), so a test can prove a permit is reclaimed
// when the process holding it dies without ever running a using/finally block — something no
// in-process simulation can prove, since disposing a FileStream inside the same process always
// runs .NET's own release path, never the OS's own let-go-on-process-exit path. Referenced only
// by Hall9k.Tests, invoked with `dotnet <this assembly's path> <lock file path>`.
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: Hall9k.Tests.LockHolder <path-to-lock-file>");
    return 2;
}

using FileStream lockStream = new(args[0], FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

// The one signal the parent test waits on before it kills this process — printed only once the
// lock is actually held, so the test can never race a kill against an acquire still in flight.
Console.WriteLine("LOCKED");
Console.Out.Flush();

await Task.Delay(Timeout.Infinite);
return 0;
