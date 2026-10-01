# Reporting transport regression check

This dependency-free .NET 8 console check inspects the compiled plugin without loading Unity or the game. It rejects reporting, ban, and kick RPC references, the old reporting protocol marker, and reporting patch targets stored in IL or metadata strings.

Run from the repository root:

```powershell
dotnet run --project tests/ReportTransportCheck -- path/to/current/OnTogetherSchoolScreen.dll
```

Supply the old 0.1.10 DLL as a positive control to confirm the scanner detects the removed transport:

```powershell
dotnet run --project tests/ReportTransportCheck -- path/to/current/OnTogetherSchoolScreen.dll path/to/0.1.10/OnTogetherSchoolScreen.dll
```

Exit code 0 means the current DLL passed and, if supplied, the old DLL was rejected. Exit code 1 means a regression or an ineffective positive control. Exit code 2 indicates invalid arguments or an unreadable assembly. This is a static transport check; it does not claim that live multiplayer was tested.
