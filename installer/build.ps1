<#
  DAAMS release build script
  Location:  <repo root>\installer\build.ps1      (repo root = folder containing monitoring_engine\)
  Run:       powershell -ExecutionPolicy Bypass -File .\installer\build.ps1

  Produces (consumed by DAAMS.iss):
    installer\build\engine\          DAAMSEngine.exe, DAAMSProtect.exe, DAAMSResetLocal.exe, _internal\
    installer\build\controlcenter\   DAAMS.ControlCenter.exe + .NET runtime + appsettings.json

  The Control Center looks for ..\Engine\DAAMSResetLocal.exe (DataMaintenanceService.cs), so the
  installed layout is  <install dir>\Engine  and  <install dir>\ControlCenter  side by side.

  Prerequisites: monitoring_engine\venv with requirements.txt installed; .NET 10 SDK (global.json).
#>

$ErrorActionPreference = "Stop"

$root      = Split-Path -Parent $PSScriptRoot
$engineDir = Join-Path $root "monitoring_engine"
$ccProj    = Join-Path $root "DAAMS.ControlCenter\DAAMS.ControlCenter\DAAMS.ControlCenter.csproj"
$out       = Join-Path $PSScriptRoot "build"
$tmp       = Join-Path $PSScriptRoot "_tmp"
$py        = Join-Path $engineDir "venv\Scripts\python.exe"
$icon      = Join-Path $PSScriptRoot "daams.ico"

if (-not (Test-Path $py)) { throw "Engine venv not found at $py" }
if (-not (Test-Path $icon)) { throw "Icon not found at $icon" }

foreach ($d in @($out, $tmp)) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
New-Item -ItemType Directory -Path "$out\engine", "$out\controlcenter", "$tmp\dist", "$tmp\work", "$tmp\spec" | Out-Null

# --------------------------------------------------------------- Engine -----
& $py -m pip install --upgrade pyinstaller
if ($LASTEXITCODE -ne 0) { throw "pip install pyinstaller failed" }

# If a built exe reports ModuleNotFoundError: X, add "--collect-all", "X" (or "--hidden-import", "X") here.
$common = @(
    "--noconfirm", "--clean", "--onedir",
    "--distpath", "$tmp\dist", "--workpath", "$tmp\work", "--specpath", "$tmp\spec",
    "--paths", $engineDir,
    "--icon", $icon,
    "--collect-all", "firebase_admin",
    "--collect-all", "google.cloud.firestore",
    "--hidden-import", "win32crypt"
)

function New-VersionFile([string]$name, [string]$desc) {
    $path = Join-Path "$tmp\spec" "$name.version.txt"
    $txt = @"
VSVersionInfo(
  ffi=FixedFileInfo(filevers=(1,0,0,0), prodvers=(1,0,0,0), mask=0x3f, flags=0x0, OS=0x40004, fileType=0x1, subtype=0x0, date=(0,0)),
  kids=[
    StringFileInfo([StringTable('040904B0', [
      StringStruct('CompanyName', 'DAAMS Capstone Team'),
      StringStruct('FileDescription', '$desc'),
      StringStruct('FileVersion', '1.0.0.0'),
      StringStruct('InternalName', '$name'),
      StringStruct('OriginalFilename', '$name.exe'),
      StringStruct('ProductName', '$desc'),
      StringStruct('ProductVersion', '1.0.0.0')])]),
    VarFileInfo([VarStruct('Translation', [1033, 1200])])
  ]
)
"@
    Set-Content -Path $path -Value $txt -Encoding ASCII
    return $path
}

# $desc is the name Windows shows (Task Manager, file properties). The .exe file names stay the same
# because the Control Center, the right-click menu and the uninstaller refer to them.
function Build-Exe([string]$name, [string]$script, [string]$mode, [string]$desc) {
    Write-Host "`n=== PyInstaller: $name ($desc) ===" -ForegroundColor Cyan
    $ver = New-VersionFile $name $desc
    Push-Location $engineDir
    try {
        & $py -m PyInstaller @common $mode --version-file $ver --name $name $script
        if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed for $name" }
    }
    finally { Pop-Location }
}

# Engine runs hidden (no console window). Its prints/errors go to %LOCALAPPDATA%\DAAMS\logs\engine_console.log
# (see the sys.stdout guard at the top of main.py). To debug with a visible console, temporarily use "--console".
Build-Exe "DAAMSEngine"     "main.py"                   "--noconsole" "DAAMS Monitoring Engine"
# Right-click helper: used to run under pythonw.exe (no console) -> --windowed.
Build-Exe "DAAMSProtect"    "cli\protect_asset_cli.py"  "--windowed"  "DAAMS Protect Asset"
# "Clean Local Data" tool. Reads RESET from stdin, so it needs --console.
Build-Exe "DAAMSResetLocal" "tools\reset_test_data.py"  "--console"   "DAAMS Clean Local Data"

# One shared _internal folder (all three use the same Python runtime).
foreach ($n in "DAAMSEngine", "DAAMSProtect", "DAAMSResetLocal") {
    Copy-Item "$tmp\dist\$n\*" "$out\engine" -Recurse -Force
}

# -------------------------------------------------------- Control Center ----
Write-Host "`n=== dotnet publish ===" -ForegroundColor Cyan
dotnet publish $ccProj -c Release -r win-x64 --self-contained true -o "$out\controlcenter"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# ------------------------------------------------------------- Checks -------
$expected = @(
    "$out\engine\DAAMSEngine.exe", "$out\engine\DAAMSProtect.exe", "$out\engine\DAAMSResetLocal.exe",
    "$out\controlcenter\DAAMS.ControlCenter.exe", "$out\controlcenter\appsettings.json"
)
foreach ($f in $expected) { if (-not (Test-Path $f)) { throw "Missing build output: $f" } }

# Never ship credentials or local data
Get-ChildItem $out -Recurse -Include "serviceAccountKey*.json", "protected_assets.json", "security_settings.json",
    "admin_settings.json", "smtp_settings.json", "*.db", "*.db-wal", "*.db-shm", "*.jsonl" |
    ForEach-Object { Write-Warning "Removing from build: $($_.FullName)"; Remove-Item $_.FullName -Force }

Write-Host "`nBuild finished. Open installer\DAAMS.iss in Inno Setup and press Compile (F9)." -ForegroundColor Green
