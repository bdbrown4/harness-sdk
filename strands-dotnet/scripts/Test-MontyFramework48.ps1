[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $TestAssembly,
    [Parameter(Mandatory = $true)][string] $VSTestConsole,
    [Parameter(Mandatory = $true)][string] $TestResultValidator,
    [Parameter(Mandatory = $true)][string] $ConsumerExecutable,
    [Parameter(Mandatory = $true)][string] $ResultsDirectory,
    [Parameter(Mandatory = $true)][ValidateSet('x86', 'x64')][string] $Platform
)

$ErrorActionPreference = 'Stop'
$framework = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'
if ($framework.Release -lt 528040 -or $framework.Release -ge 533320) {
    throw "Clean Framework 4.8 is required; installed Release is $($framework.Release)."
}
foreach ($path in @($TestAssembly, $VSTestConsole, $TestResultValidator, $ConsumerExecutable)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing Monty verification input: $path" }
}
if ((Test-Path -LiteralPath $ResultsDirectory) -and @(Get-ChildItem -LiteralPath $ResultsDirectory -Force).Count -gt 0) {
    throw 'Monty clean-runtime evidence requires a new or empty results directory.'
}
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
[pscustomobject]@{
    FrameworkRelease = $framework.Release
    FrameworkVersion = $framework.Version
    ClrVersion = [Environment]::Version.ToString()
    MscorlibVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo([object].Assembly.Location).FileVersion
    OperatingSystem = [Environment]::OSVersion.VersionString
    ControllerArchitecture = if ([Environment]::Is64BitProcess) { 'x64' } else { 'x86' }
    RequestedTestPlatform = $Platform
} | ConvertTo-Json | Tee-Object -FilePath (Join-Path $ResultsDirectory 'runtime.json')
$settings = Join-Path $ResultsDirectory 'monty.runsettings'
'<RunSettings><RunConfiguration><TreatNoTestsAsError>true</TreatNoTestsAsError></RunConfiguration></RunSettings>' | Set-Content -LiteralPath $settings -Encoding UTF8
$previousArchitecture = $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE
$env:STRANDS_MONTY_EXPECTED_ARCHITECTURE = $Platform.ToUpperInvariant()
try {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $VSTestConsole $TestAssembly "/Platform:$Platform" '/Framework:.NETFramework,Version=v4.8' "/Settings:$settings" '/Logger:trx;LogFileName=monty.trx' "/ResultsDirectory:$ResultsDirectory" 2>&1
        $testExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    $output | Tee-Object -FilePath (Join-Path $ResultsDirectory 'tests.log')
    if ($testExitCode -ne 0) { throw "Clean Framework 4.8 Monty $Platform tests failed: $testExitCode" }
    & $TestResultValidator -ResultsDirectory $ResultsDirectory -ExpectedArchitecture $Platform.ToUpperInvariant()
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $ConsumerExecutable $Platform.ToUpperInvariant() 2>&1
        $consumerExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    $output | Tee-Object -FilePath (Join-Path $ResultsDirectory 'package-consumer.log')
    $pointer = if ($Platform -eq 'x86') { 4 } else { 8 }
    $marker = "Monty:real-python,host-tool,print-only;Engine:0.0.23;ABI:1;ProcessArchitecture:$($Platform.ToUpperInvariant());HostPointerSize:$pointer;WorkerPointerBits:$($pointer * 8)"
    if ($consumerExitCode -ne 0 -or $output -cnotcontains $marker) { throw "Clean Framework 4.8 Monty $Platform package consumer failed." }
}
finally { $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE = $previousArchitecture }
Write-Output "Clean Framework 4.8 Monty tests and independent package consumer passed in $Platform."
