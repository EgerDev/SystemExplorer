# Thread diagnostics smoke tests

This focused Windows x64 console suite has no package dependencies and exercises the
thread-diagnostics provider with deterministic fakes plus owned native threads and
a cooperative child-process fixture.

From the repository root with the official .NET SDK 10.0.403 on `PATH`:

```powershell
dotnet run --project tests/SystemExplorer.Threads.Smoke -c Release
dotnet build src/SystemExplorer.Windows -c Release -warnaserror
```

The provider's three-second capture budget is cooperative: it checks time between
synchronous native calls and cannot impose a hard deadline on those calls. WCT
timeouts discard results, but closing a native wait-chain session can wait for
callbacks to return; cancellation is not reported complete until that close has
finished. See Microsoft's documentation for
[WaitForSingleObject](https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject)
and [GetThreadWaitChain](https://learn.microsoft.com/windows/win32/api/wct/nf-wct-getthreadwaitchain).

Native self-process checks do not adjust privileges, elevate, or install a driver.
Evidence from this Windows Server host is not Windows 11 standard-user acceptance.
