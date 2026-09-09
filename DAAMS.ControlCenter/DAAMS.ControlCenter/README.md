# DAAMS Control Center

Manager/supervisor-side GUI for DAAMS (Digital Asset Access Monitoring and Risk
Assessment System). C# / WPF / .NET 8 / MVVM.

This folder is the `control_center/` component described in the DAAMS repo layout.
It does not include, modify, or duplicate the Python `monitoring_engine/`.

## Status: Phase 1 — Foundation

Implemented:
- Project shell, MVVM structure, navigation, global theme
- Dashboard with mock/sample data (clearly labeled)
- Placeholder screens for Protected Assets, Activity Logs, Security Alerts,
  Incident Reports, Settings (real implementations land in Phases 3–7)

Not implemented yet (by design — see project reference doc):
- Firestore integration
- Risk calculation
- Real monitoring data
- Alerts/incident logic
- Authentication
- Installer

## Requirements

- Windows 10/11
- .NET 8 SDK (with the Windows Desktop workload)
- Visual Studio 2022 (recommended) or `dotnet` CLI

## Run

```
cd control_center
dotnet restore
dotnet run
```

Or open `ControlCenter.csproj` in Visual Studio and press F5.

## Project layout

```
control_center/
├── ControlCenter.csproj
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs
├── Models/          DAAMS data contracts (ActivityLog, ProtectedAsset, SecurityAlert, IncidentReport, enums)
├── ViewModels/       MVVM state + navigation (MainViewModel, DashboardViewModel, ...)
├── Views/            XAML screens
├── Converters/        XAML value converters (risk color, enum-to-bool nav, etc.)
├── Data/              MockDataProvider — sample data only, swapped out in Phase 8
├── Services/          (reserved for future Firestore/navigation services)
└── Resources/         Colors.xaml, Typography.xaml, Styles.xaml
```
