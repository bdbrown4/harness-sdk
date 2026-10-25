param(
    [Parameter(Mandatory)][string] $ResultsDirectory,
    [Parameter(Mandatory)][ValidateSet('X86', 'X64', 'Arm64')][string] $ExpectedArchitecture
)

$ErrorActionPreference = 'Stop'
$files = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File -Recurse)
if ($files.Count -eq 0) { throw 'Cedar verification produced no TRX files.' }
foreach ($file in $files) {
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $document.Load($file.FullName)
    $ns = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $counters = $document.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $ns)
    if (-not $counters) { throw 'Cedar results have no execution counters.' }
    $counts = @{}
    foreach ($name in @('total', 'executed', 'passed', 'failed', 'notExecuted')) {
        [long] $value = 0
        if (-not [long]::TryParse($counters.GetAttribute($name), [ref] $value) -or $value -lt 0) { throw "Invalid Cedar $name counter." }
        $counts[$name] = $value
    }
    if ($counts.total -le 0 -or $counts.total -ne $counts.executed -or $counts.passed -ne $counts.total -or $counts.failed -ne 0 -or $counts.notExecuted -ne 0) { throw 'Cedar verification requires nonempty passing tests with no skips.' }
    $entries = @($document.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns))
    if ($entries.Count -ne $counts.executed -or @($entries | Where-Object { $_.GetAttribute('outcome') -cne 'Passed' }).Count -ne 0) { throw 'Cedar result entries do not match the passing execution counters.' }
    $definition = @($document.SelectNodes("/t:TestRun/t:TestDefinitions/t:UnitTest[t:TestMethod[@className='Strands.Cedar.Tests.NativeBridgeTests' and @name='RealEngineVersionAndRequestedProcessArchitectureAreVerified']]", $ns))
    if ($definition.Count -ne 1) { throw 'Cedar architecture assertion definition is missing or duplicated.' }
    $id = $definition[0].GetAttribute('id')
    $result = @($document.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns) | Where-Object { $_.GetAttribute('testId') -ceq $id })
    if ($result.Count -ne 1 -or $result[0].GetAttribute('outcome') -cne 'Passed') { throw 'Cedar architecture assertion did not pass exactly once.' }
    $stdout = $result[0].SelectSingleNode('t:Output/t:StdOut', $ns)
    $pointerSize = if ($ExpectedArchitecture -eq 'X86') { 4 } else { 8 }
    if (-not $stdout -or -not $stdout.InnerText.Contains("Process architecture: $ExpectedArchitecture; pointer size: $pointerSize.")) { throw 'Cedar process architecture evidence does not match the requested host.' }
    Write-Output "Verified $($counts.passed) native Cedar cases in $ExpectedArchitecture."
}
