<#
.SYNOPSIS
    Enables Windows Security Audit Log monitoring (Event ID 4663) for DAAMS Open detection.

.DESCRIPTION
    Configures the Windows File System audit subcategory for success auditing,
    creates a FileSystemAuditRule for Everyone (ReadData, Success only), and
    applies it to the specified path with Set-Acl.
    Must be executed with Administrator privileges.

.PARAMETER Path
    Mandatory path to the file or directory to audit.
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Path
)

$ErrorActionPreference = "Stop"

# Ensure script is running elevated
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "enable_open_audit.ps1 must be run as Administrator."
    exit 1
}

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "Target path does not exist: $Path"
    exit 1
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).ProviderPath

# 1. Enable Windows File System subcategory success auditing
Write-Host "Enabling Windows File System audit policy..."
& auditpol.exe /set /subcategory:"File System" /success:enable
if ($LASTEXITCODE -ne 0) {
    Write-Warning "auditpol returned non-zero exit code: $LASTEXITCODE"
}

# 2. Determine inheritance flags
$isFolder = (Get-Item -LiteralPath $resolvedPath) -is [System.IO.DirectoryInfo]
if ($isFolder) {
    $inheritanceFlags = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
} else {
    $inheritanceFlags = [System.Security.AccessControl.InheritanceFlags]::None
}

$identity = [System.Security.Principal.NTAccount]"Everyone"
$rights = [System.Security.AccessControl.FileSystemRights]::ReadData
$propagationFlags = [System.Security.AccessControl.PropagationFlags]::None
$auditFlags = [System.Security.AccessControl.AuditFlags]::Success

$auditRule = New-Object System.Security.AccessControl.FileSystemAuditRule(
    $identity,
    $rights,
    $inheritanceFlags,
    $propagationFlags,
    $auditFlags
)

# 3. Apply audit rule using Set-Acl
Write-Host "Configuring audit ACL on: $resolvedPath"
$acl = Get-Acl -LiteralPath $resolvedPath -Audit
$acl.AddAuditRule($auditRule)
Set-Acl -LiteralPath $resolvedPath -AclObject $acl

Write-Host "Successfully enabled ReadData success auditing for Everyone on: $resolvedPath (Inheritance: $inheritanceFlags)"
