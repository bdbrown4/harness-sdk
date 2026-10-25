param(
    [Parameter(Mandatory = $true)][string] $ProductionScript,
    [Parameter(Mandatory = $true)][string] $EvidenceDirectory,
    [string] $AssetsFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProductionScript = (Resolve-Path -LiteralPath $ProductionScript).Path
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Use a new contract evidence directory.' }
New-Item -ItemType Directory -Path $EvidenceDirectory | Out-Null
function Get-ContractHash([string] $Path) {
    $stream = [IO.File]::OpenRead($Path); $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
    finally { $hash.Dispose(); $stream.Dispose() }
}
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($ProductionScript, [ref] $tokens, [ref] $parseErrors)
if ($parseErrors.Count -ne 0) { throw 'The managed package validator must parse before its asset-selection contracts run.' }
$keys = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$key' }, $true))
if ($keys.Count -ne 1) { throw 'Expected one package-key assignment in the managed asset-selection block.' }
$statements = @($keys[0].Parent.Statements)
$start = [Array]::IndexOf($statements, $keys[0])
$ends = @(for ($i = $start + 1; $i -lt $statements.Count; $i++) {
    if ($statements[$i] -is [Management.Automation.Language.AssignmentStatementAst] -and $statements[$i].Left.Extent.Text -ceq '$zip') { $i }
})
if ($start -lt 0 -or $ends.Count -ne 1) { throw 'Cannot isolate the production managed asset-selection block.' }
$source = [IO.File]::ReadAllText($ProductionScript)
$selectionText = $source.Substring($statements[$start].Extent.StartOffset, $statements[$ends[0] - 1].Extent.EndOffset - $statements[$start].Extent.StartOffset)
$selection = [scriptblock]::Create($selectionText)
[IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'production-selection.ps1'), $selectionText)
$id = 'Strands.Agents'; $version = '0.1.0-alpha.1'
if ($AssetsFile) {
    $AssetsFile = (Resolve-Path -LiteralPath $AssetsFile).Path
    $original = Get-Content -Raw -LiteralPath $AssetsFile | ConvertFrom-Json
    $packages = @($original.libraries.PSObject.Properties | Where-Object { $_.Name.StartsWith($id + '/', [StringComparison]::Ordinal) })
    if ($packages.Count -ne 1) { throw 'The retained restore witness must contain exactly one Core package identity.' }
    $version = $packages[0].Name.Substring($id.Length + 1)
} else {
    $original = @'
{"targets":{"net48":{"Strands.Agents/0.1.0-alpha.1":{"compile":{"lib/netstandard2.0/Strands.Agents.dll":{}},"runtime":{"lib/netstandard2.0/Strands.Agents.dll":{}}}},"net48/win-x64":{"Strands.Agents/0.1.0-alpha.1":{"compile":{"lib/netstandard2.0/Strands.Agents.dll":{}},"runtime":{"lib/netstandard2.0/Strands.Agents.dll":{}}}}},"libraries":{"Strands.Agents/0.1.0-alpha.1":{"type":"package"}}}
'@ | ConvertFrom-Json
}
function Copy-Assets { $original | ConvertTo-Json -Depth 100 | ConvertFrom-Json }
function Invoke-Selection($Assets, [string] $Framework = 'net48') {
    $assets = $Assets; $framework = $Framework
    & $selection
}
function Assert-Rejected($Assets) {
    $rejected = $false
    try { Invoke-Selection $Assets }
    catch {
        if ($_.Exception.Message -cne "Consumer selected an unexpected normal managed asset: $id") { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'The production validator accepted an invalid normal asset selection.' }
}
function Get-Selections($Assets) {
    foreach ($target in $Assets.targets.PSObject.Properties) {
        $entry = $target.Value.PSObject.Properties["$id/$version"]
        if ($entry) { $entry.Value }
    }
}
$results = [Collections.Generic.List[object]]::new()
function Test-Case([string] $Name, [scriptblock] $Body) {
    try { & $Body; $results.Add([pscustomobject]@{ Name=$Name; Passed=$true }) }
    catch { $results.Add([pscustomobject]@{ Name=$Name; Passed=$false; Error=$_.Exception.Message }) }
}
Test-Case 'AcceptEveryNormalBaseAndRidTarget' {
    if (@(Get-Selections $original).Count -lt 2) { throw 'The restore witness must exercise multiple applicable targets.' }
    Invoke-Selection $original
}
Test-Case 'AcceptSingleNormalTarget' {
    $assets = Copy-Assets
    $first = $assets.targets.PSObject.Properties | Select-Object -First 1
    foreach ($target in @($assets.targets.PSObject.Properties)) {
        if ($target.Name -cne $first.Name) { $assets.targets.PSObject.Properties.Remove($target.Name) }
    }
    Invoke-Selection $assets
}
Test-Case 'RejectMissingPackageSelection' {
    $assets = Copy-Assets
    foreach ($target in $assets.targets.PSObject.Properties) { $target.Value.PSObject.Properties.Remove("$id/$version") }
    Assert-Rejected $assets
}
foreach ($kind in @('compile', 'runtime')) {
    Test-Case ('RejectDivergent' + $kind + 'AssetInLaterTarget') {
        $assets = Copy-Assets
        $entries = @(Get-Selections $assets)
        $entries[-1].$kind = [pscustomobject]@{ 'lib/net8.0/Strands.Agents.dll' = [pscustomobject]@{} }
        Assert-Rejected $assets
    }
    Test-Case ('RejectMissing' + $kind + 'AssetInLaterTarget') {
        $assets = Copy-Assets
        $entries = @(Get-Selections $assets)
        $entries[-1].$kind = [pscustomobject]@{ '_._' = [pscustomobject]@{} }
        Assert-Rejected $assets
    }
}
foreach ($framework in @('net8.0', 'net10.0')) {
    Test-Case ('AcceptNormalModernAsset-' + $framework) {
        $assets = Copy-Assets
        foreach ($entry in @(Get-Selections $assets)) {
            $asset = "lib/$framework/$id.dll"
            $entry.compile = [pscustomobject]@{ $asset = [pscustomobject]@{} }
            $entry.runtime = [pscustomobject]@{ $asset = [pscustomobject]@{} }
        }
        Invoke-Selection $assets $framework
    }
}
$report = [pscustomobject]@{
    Scope = 'Actual production asset-selection statements only; no SDK restore, publish, or consumer runtime execution'
    PowerShellVersion = $PSVersionTable.PSVersion.ToString()
    ProductionSHA256 = Get-ContractHash $ProductionScript
    AssetsFile = $AssetsFile
    AssetsSHA256 = $(if ($AssetsFile) { Get-ContractHash $AssetsFile } else { $null })
    Passed = @($results | Where-Object Passed).Count
    Failed = @($results | Where-Object { -not $_.Passed }).Count
    Results = $results
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'summary.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 6
if ($report.Failed -ne 0) { exit 1 }
exit 0
