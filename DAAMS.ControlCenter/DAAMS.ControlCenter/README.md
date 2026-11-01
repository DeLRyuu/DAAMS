# DAAMS Control Center

The Windows desktop administration console for DAAMS, built with C#, WPF, and
MVVM. It is separate from the Python `monitoring_engine/`.

The console provides a dashboard and views for protected assets, activity,
devices, security alerts/cases, and settings. It reads Firestore data and uses
the shared local SQLite database for offline data and sync status. Sample data
is available as an explicit fallback when remote data cannot be loaded.

## Requirements

- Windows 10/11
- .NET 10 SDK with the Windows Desktop workload
- Visual Studio 2022 or the `dotnet` CLI

## Run

From the repository root:

```powershell
cd DAAMS.ControlCenter\DAAMS.ControlCenter
dotnet restore
dotnet run
```

The project targets `net10.0-windows`; the repository's `global.json` selects
the .NET SDK version.