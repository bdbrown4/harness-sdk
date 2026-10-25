param(
    [Parameter(Mandatory)][string] $TrxPath,
    [Parameter(Mandatory)][ValidateSet('X86', 'X64', 'Arm64')][string] $ExpectedArchitecture
)

$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('cedar-trx-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
foreach ($scenario in @('empty', 'zero', 'failed-entry', 'missing-architecture', 'wrong-architecture')) {
    $directory = Join-Path $scratch $scenario
    New-Item -ItemType Directory -Path $directory | Out-Null
    if ($scenario -ne 'empty') {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load((Resolve-Path -LiteralPath $TrxPath).Path)
        $ns = [Xml.XmlNamespaceManager]::new($document.NameTable)
        $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
        if ($scenario -eq 'zero') {
            $counters = $document.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $ns)
            foreach ($attribute in $counters.Attributes) { $attribute.Value = '0' }
        }
        if ($scenario -eq 'failed-entry') {
            $result = @($document.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns) | Where-Object { $_.GetAttribute('testName') -notlike '*RealEngineVersionAndRequestedProcessArchitectureAreVerified*' })[0]
            if (-not $result) { throw 'Guard fixture requires a non-architecture case.' }
            $result.SetAttribute('outcome', 'Failed')
        }
        if ($scenario -eq 'missing-architecture') {
            $definition = $document.SelectSingleNode("/t:TestRun/t:TestDefinitions/t:UnitTest[t:TestMethod[@className='Strands.Cedar.Tests.NativeBridgeTests' and @name='RealEngineVersionAndRequestedProcessArchitectureAreVerified']]", $ns)
            $definition.ParentNode.RemoveChild($definition) | Out-Null
        }
        $document.Save((Join-Path $directory 'fixture.trx'))
    }
    $architecture = if ($scenario -eq 'wrong-architecture') { if ($ExpectedArchitecture -eq 'X86') { 'X64' } else { 'X86' } } else { $ExpectedArchitecture }
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Assert-TestResults.ps1') -ResultsDirectory $directory -ExpectedArchitecture $architecture }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Cedar result guard accepted invalid fixture: $scenario" }
}
Write-Output "Five invalid Cedar result fixtures rejected; diagnostics retained at $scratch."
