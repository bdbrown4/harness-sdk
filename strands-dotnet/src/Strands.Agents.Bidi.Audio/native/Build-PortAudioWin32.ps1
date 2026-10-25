# Backwards-compatible entry point for the original Win32 build command.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$WorkingDirectory,
    [string]$VisualStudioDirectory,
    [string]$WindowsSdkVersion
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Build-PortAudioWindows.ps1') @PSBoundParameters -Architecture x86
