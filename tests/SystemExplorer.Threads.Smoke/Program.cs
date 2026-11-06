using System.Diagnostics;
using System.Reflection;
using SystemExplorer.Windows;

if (args.Length == 1 && args[0] == "--exit259")
{
    Console.WriteLine("READY");
    try
    {
        await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12));
        Environment.Exit(259);
    }
    catch (TimeoutException)
    {
        Environment.Exit(2);
    }
    return;
}

Console.WriteLine($".NET SDK/runtime: {Environment.Version}; OS: {Environment.OSVersion.VersionString}");
await DeterministicThreadTests.RunAsync();
await NativeThreadSmoke.RunAsync();
await NativeThreadSmoke.CheckExit259Async();
Console.WriteLine("PASS: all focused thread diagnostics smoke scenarios.");
