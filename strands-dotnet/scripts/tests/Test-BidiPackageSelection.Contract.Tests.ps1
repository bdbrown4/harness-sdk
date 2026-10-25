param([Parameter(Mandatory = $true)][string] $ProductionScript, [Parameter(Mandatory = $true)][string] $EvidenceDirectory)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Use a new fixture directory.' }
New-Item -ItemType Directory -Path $EvidenceDirectory | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $ProductionScript).Path, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Package script does not parse.' }
$function = @($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq 'Get-ExactBidiPackage' })
if ($function.Count -ne 1) { throw 'The exact package selection function is missing.' }
. ([scriptblock]::Create($function[0].Extent.Text))
function New-FixturePackage([string]$Path, [string]$Id, [string]$Version) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('fixture.nuspec'); $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write("<package><metadata><id>$Id</id><version>$Version</version></metadata></package>") } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
}
$feed = Join-Path $EvidenceDirectory 'feed'; New-Item -ItemType Directory -Path $feed | Out-Null
New-FixturePackage (Join-Path $feed 'Strands.Agents.Bidi.1.0.0.nupkg') 'Strands.Agents.Bidi' '1.0.0'
New-FixturePackage (Join-Path $feed 'Strands.Agents.Bidi.Audio.1.0.0.nupkg') 'Strands.Agents.Bidi.Audio' '1.0.0'
if ((Get-ExactBidiPackage $feed).Name -cne 'Strands.Agents.Bidi.1.0.0.nupkg') { throw 'Audio must not shadow the exact Bidi package.' }
New-FixturePackage (Join-Path $feed 'Strands.Agents.Bidi.2.0.0.nupkg') 'Strands.Agents.Bidi' '2.0.0'
$duplicateRejected = $false
try { Get-ExactBidiPackage $feed | Out-Null } catch { if ($_.Exception.Message -notlike '*exactly one bidirectional package by nuspec identity*') { throw }; $duplicateRejected = $true }
if (-not $duplicateRejected) { throw 'Multiple exact Bidi identities must be rejected.' }
$other = Join-Path $EvidenceDirectory 'wrong-id'; New-Item -ItemType Directory -Path $other | Out-Null
New-FixturePackage (Join-Path $other 'Strands.Agents.Bidi.1.0.0.nupkg') 'Strands.Agents.Bidi.Audio' '1.0.0'
$wrongRejected = $false
try { Get-ExactBidiPackage $other | Out-Null } catch { if ($_.Exception.Message -notlike '*exactly one bidirectional package by nuspec identity*') { throw }; $wrongRejected = $true }
if (-not $wrongRejected) { throw 'A filename cannot substitute for the package identity.' }
[ordered]@{ Scope = 'Synthetic package selection fixtures only; no SDK/runtime claim'; Passed = 3; Failed = 0 } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'summary.json') -Encoding UTF8
