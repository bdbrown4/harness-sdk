param(
    [Parameter(Mandatory = $true)][string] $ProductionScript,
    [Parameter(Mandatory = $true)][string] $FixtureDirectory,
    [Parameter(Mandatory = $true)][string] $EvidenceDirectory,
    [string] $NativeValidatorRoot
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Contract evidence directory must be new.' }
New-Item -ItemType Directory -Path $EvidenceDirectory | Out-Null
$global:ContractFixtures = (Resolve-Path -LiteralPath $FixtureDirectory).Path
$global:ContractObservedSuites = [Collections.Generic.List[string]]::new()
$global:ContractObservedS3Pointers = [Collections.Generic.List[string]]::new()
$global:ContractS3Mode = 'Pass'
$global:ContractObservedSqliteCalls = [Collections.Generic.List[string]]::new()
$global:ContractSqliteMode = 'Pass'
$global:ContractRegistryViews = @(
    [pscustomobject]@{ View = 'Registry64'; Release = 528049; Version = '4.8-fixture' },
    [pscustomobject]@{ View = 'Registry32'; Release = 528049; Version = '4.8-fixture' }
)
$console = Join-Path $EvidenceDirectory 'vstest-fixture.ps1'
@'
$family = [IO.Path]::GetFileNameWithoutExtension($args[0])
$platform = ($args | Where-Object { $_ -like '/Platform:*' }).Substring(10)
$logger = ($args | Where-Object { $_ -like '/Logger:*' })
$name = $logger.Substring($logger.IndexOf('LogFileName=') + 12)
$directory = ($args | Where-Object { $_ -like '/ResultsDirectory:*' }).Substring(18)
New-Item -ItemType Directory -Path $directory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $global:ContractFixtures "$family-$platform.trx") -Destination (Join-Path $directory $name)
$global:ContractObservedSuites.Add($family)
$global:LASTEXITCODE = 0
'@ | Set-Content -LiteralPath $console -Encoding UTF8
$sample = Join-Path $EvidenceDirectory 'sample.ps1'
"'2 + 3 = 5.'; 'Stop: end_turn; model turns: 2'; `$global:LASTEXITCODE = 0" | Set-Content -LiteralPath $sample -Encoding UTF8
$global:ContractObservedHarnessPointers = [Collections.Generic.List[string]]::new()
$global:ContractHarnessMode = 'Pass'
$harnessSample = Join-Path $EvidenceDirectory 'harness-sample.ps1'
@'
$global:ContractObservedHarnessPointers.Add([string] $args[0])
$pointer = if ($global:ContractHarnessMode -eq 'WrongPointer') { 99 } else { $args[0] }
$asset = if ($global:ContractHarnessMode -eq 'WrongAsset') { 'net8.0' } else { 'netstandard2.0' }
if ($global:ContractHarnessMode -ne 'MissingCycle') { '2 + 3 = 5.' }
if ($global:ContractHarnessMode -ne 'MissingStop') { 'Stop: end_turn; model turns: 2' }
if ($global:ContractHarnessMode -ne 'MissingMarker') {
    $marker = "Harness package: agent=complete; tool=5; turns=2; pointer=$pointer; asset=$asset; framework=4.8"
    $marker
    if ($global:ContractHarnessMode -eq 'DuplicateMarker') { $marker }
}
$global:LASTEXITCODE = if ($global:ContractHarnessMode -eq 'FailedExit') { 7 } else { 0 }
'@ | Set-Content -LiteralPath $harnessSample -Encoding UTF8
$global:ContractNovaPointers = [Collections.Generic.List[string]]::new()
$global:ContractNovaMode = 'Pass'
$novaSample = Join-Path $EvidenceDirectory 'nova-sample.ps1'
@'
$global:ContractNovaPointers.Add([string] $args[0])
$pointer = if ($global:ContractNovaMode -eq 'WrongPointer') { 99 } else { $args[0] }
$asset = if ($global:ContractNovaMode -eq 'WrongAsset') { 'net8.0' } else { 'netstandard2.0' }
$abi = if ($global:ContractNovaMode -eq 'WrongAbi') { 99 } else { 2 }
"Nova package: pointer=$pointer; asset=$asset; abi=$abi; network=false"
if ($global:ContractNovaMode -ne 'MissingMarker') {
    'PASS framework48-nova-native-abi-smoke'
    if ($global:ContractNovaMode -eq 'DuplicateMarker') { 'PASS framework48-nova-native-abi-smoke' }
}
$global:LASTEXITCODE = if ($global:ContractNovaMode -eq 'FailedExit') { 7 } else { 0 }
'@ | Set-Content -LiteralPath $novaSample -Encoding UTF8
$global:ContractManagedModes = @{ Core = 'Pass'; GoogleAuth = 'Pass' }
$global:ContractManagedCalls = [Collections.Generic.List[string]]::new()
$managedSamples = @{}
foreach ($family in @('Core', 'GoogleAuth')) {
    $managedSample = Join-Path $EvidenceDirectory ($family.ToLowerInvariant() + '-sample.ps1')
    @'
$family = '__FAMILY__'
$global:ContractManagedCalls.Add("$family|$($args[0])")
$mode = $global:ContractManagedModes[$family]
$pointer = if ($mode -eq 'WrongPointer') { 99 } else { $args[0] }
$prefix = if ($family -eq 'Core') { 'PASS core-package-tool-cycle' } else { 'PASS googleauth-package-provider-request' }
if ($mode -ne 'MissingMarker') { "$prefix pointer=$pointer" }
$global:LASTEXITCODE = 0
'@.Replace('__FAMILY__', $family) | Set-Content -LiteralPath $managedSample -Encoding UTF8
    $managedSamples[$family] = $managedSample
}
$global:ContractArchitectureModes = @{ SageMaker='Pass'; Bidi='Pass'; OpenTelemetry='Pass' }
$global:ContractArchitectureCalls = [Collections.Generic.List[string]]::new()
$architectureSamples = @{}
foreach ($family in @('SageMaker', 'Bidi', 'OpenTelemetry')) {
    $samplePath = Join-Path $EvidenceDirectory ($family.ToLowerInvariant() + '-architecture-sample.ps1')
    @'
$family = '__FAMILY__'
$mode = $global:ContractArchitectureModes[$family]
$global:ContractArchitectureCalls.Add("$family|$($args[0])")
$pointer = if ($mode -eq 'WrongPointer') { 99 } else { $args[0] }
$asset = if ($mode -eq 'WrongAsset') { 'net8.0' } else { 'netstandard2.0' }
$functional = switch ($family) {
    'SageMaker' { 'SageMaker package: agent=ok; structured=7; calls=2; borrowed=true' }
    'Bidi' { 'Bidi package: websocket=ok; tool=7; continuation=one; audio=ok; agent=7' }
    'OpenTelemetry' { 'OpenTelemetry package: spans=2; invocations=1; resource=ok; ownership=ok' }
}
$functional
if ($mode -ne 'MissingMarker') {
    $marker = "$family package architecture: pointer=$pointer; asset=$asset"
    $marker
    if ($mode -eq 'DuplicateMarker') { $marker }
}
$global:LASTEXITCODE = if ($mode -eq 'FailedExit') { 7 } else { 0 }
'@.Replace('__FAMILY__', $family) | Set-Content -LiteralPath $samplePath -Encoding UTF8
    $architectureSamples[$family] = $samplePath
}

$nativeSample = Join-Path $EvidenceDirectory 'native-sample.ps1'
@'
$architecture = $args[0]
$pointer = if ($architecture -eq 'X86') { 4 } else { 8 }
"Monty:real-python,host-tool,print-only;Engine:0.0.23;ABI:1;ProcessArchitecture:$architecture;HostPointerSize:$pointer;WorkerPointerBits:$($pointer * 8)"
"Cedar:allow,deny,schema-rejected;Architecture:$architecture;PointerSize:$pointer"
$global:LASTEXITCODE = 0
'@ | Set-Content -LiteralPath $nativeSample -Encoding UTF8
$bedrockSample = Join-Path $EvidenceDirectory 'bedrock-sample.ps1'
'"Bedrock package: agent=complete; tool=item:7; calls=2; borrowed=true; cancellation=ok; pointer=$($args[0])"; $global:LASTEXITCODE = 0' | Set-Content -LiteralPath $bedrockSample -Encoding UTF8
$s3Sample = Join-Path $EvidenceDirectory 's3-sample.ps1'
@'
$global:ContractObservedS3Pointers.Add([string] $args[0])
$pointer = if ($global:ContractS3Mode -eq 'WrongPointer') { 99 } else { $args[0] }
if ($global:ContractS3Mode -ne 'MissingMarker') { "S3 package: fourOps=ok; namespace=ok; search=ok; borrowed=true; pointer=$pointer" }
$global:LASTEXITCODE = if ($global:ContractS3Mode -eq 'FailedExit') { 7 } else { 0 }
'@ | Set-Content -LiteralPath $s3Sample -Encoding UTF8
$sqliteSample = Join-Path $EvidenceDirectory 'sqlite-sample.ps1'
$sqliteNative = Join-Path $EvidenceDirectory 'sqlite-native/e_sqlite3.dll'
New-Item -ItemType Directory -Path (Split-Path -Parent $sqliteNative) | Out-Null
[IO.File]::WriteAllBytes($sqliteNative, [byte[]] @(83, 81, 76, 105, 116, 101))
@'
$mode = [string] $args[0]
$storage = [string] $args[1]
$pointer = [int] $args[2]
$global:ContractObservedSqliteCalls.Add("$mode|$storage|$pointer")
$native = Join-Path $PSScriptRoot 'sqlite-native/e_sqlite3.dll'
$hash = [Security.Cryptography.SHA256]::Create()
try { $digest = [BitConverter]::ToString($hash.ComputeHash([IO.File]::ReadAllBytes($native))).Replace('-', '') } finally { $hash.Dispose() }
$record = [ordered] @{
    Mode = $mode; PointerSize = $pointer; Architecture = if ($pointer -eq 8) { 'X64' } else { 'X86' }
    Runtime = '.NET Framework fixture'; SQLiteVersion = 'fixture-not-native-proof'; Fts5 = $true
    Persistence = $true; NamespacedMemory = $true; OpaqueBytes = $true; BorrowedStorage = $true
    HigherRankScore = 0.5; LowerRankScore = 0.2; NativePath = $native; NativeSHA256 = $digest
}
switch ($global:ContractSqliteMode) {
    'WrongPointer' { $record.PointerSize = 99 }
    'WrongArchitecture' { $record.Architecture = if ($pointer -eq 8) { 'X86' } else { 'X64' } }
    'WrongMode' { $record.Mode = 'other' }
    'FailedFts' { $record.Fts5 = $false }
    'NativeHashMismatch' { $record.NativeSHA256 = '0' * 64 }
    'WrongRank' { $record.HigherRankScore = 0.1 }
    'WrongNativePath' { $record.NativePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../outside-sqlite.dll')) }
}
$skip = ($mode -eq 'write' -and $global:ContractSqliteMode -eq 'MissingWriterMarker') -or ($mode -eq 'read' -and $global:ContractSqliteMode -eq 'MissingReaderMarker')
if (-not $skip) {
    $line = if ($global:ContractSqliteMode -eq 'MalformedMarker') { 'STRANDS_SQLITE_CONSUMER_JSON:{invalid' } else { 'STRANDS_SQLITE_CONSUMER_JSON:' + ($record | ConvertTo-Json -Compress) }
    $line
    if ($global:ContractSqliteMode -eq 'DuplicateMarker') { $line }
}
$global:LASTEXITCODE = if (($mode -eq 'write' -and $global:ContractSqliteMode -eq 'WriterExit') -or ($mode -eq 'read' -and $global:ContractSqliteMode -eq 'ReaderExit')) { 7 } else { 0 }
'@ | Set-Content -LiteralPath $sqliteSample -Encoding UTF8
$global:ContractKbPointers = [Collections.Generic.List[string]]::new()
$global:ContractKbMode = 'Pass'
$kbSample = Join-Path $EvidenceDirectory 'kb-sample.ps1'
@'
$global:ContractKbPointers.Add([string] $args[0])
$pointer = if ($global:ContractKbMode -eq 'WrongPointer') { 99 } else { $args[0] }
if ($global:ContractKbMode -ne 'MissingMarker') {
    $marker = "KB Consumer: retrieval=true; borrowed=true; requests=11; custom=true; s3=true; pointer=$pointer"
    $marker
    if ($global:ContractKbMode -eq 'DuplicateMarker') { $marker }
}
$global:LASTEXITCODE = if ($global:ContractKbMode -eq 'FailedExit') { 7 } else { 0 }
'@ | Set-Content -LiteralPath $kbSample -Encoding UTF8
$global:ContractAudioPointers = [Collections.Generic.List[string]]::new()
$global:ContractAudioMode = 'Pass'
$audioSample = Join-Path $EvidenceDirectory 'audio-sample.ps1'
@'
$global:ContractAudioPointers.Add([string] $args[0])
$pointer = if ($global:ContractAudioMode -eq 'WrongPointer') { 99 } else { $args[0] }
$asset = if ($global:ContractAudioMode -eq 'WrongAsset') { 'net8.0' } else { 'netstandard2.0' }
if ($global:ContractAudioMode -ne 'MissingMarker') {
    $marker = "Audio package: native=ok; borrowed=true; pointer=$pointer; asset=$asset; hardware=false"
    $marker
    if ($global:ContractAudioMode -eq 'DuplicateMarker') { $marker }
}
'Audio native version: PortAudio fixture; not native runtime evidence'
$global:LASTEXITCODE = if ($global:ContractAudioMode -eq 'FailedExit') { 7 } else { 0 }
'@ | Set-Content -LiteralPath $audioSample -Encoding UTF8
$assemblyRoot = Join-Path $EvidenceDirectory 'assemblies'
New-Item -ItemType Directory -Path (Join-Path $assemblyRoot 'McpStdioFixture') -Force | Out-Null
foreach ($name in @('compat.dll', 'bidi.dll', 'audio.dll', 'otel.dll', 'monty.dll', 'cedar.dll', 's3.dll', 'sqlite.dll', 'kb.dll', 'McpStdioFixture/Strands.Mcp.StdioServer.exe')) {
    New-Item -ItemType File -Path (Join-Path $assemblyRoot $name) | Out-Null
}
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $ProductionScript).Path, [ref] $tokens, [ref] $parseErrors)
if ($parseErrors.Count -ne 0) { throw "Production script parse failed: $parseErrors" }
$parameterNames = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
$functions = @($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] })
$body = $ast.ParamBlock.Extent.Text + "`n" + (($ast.EndBlock.Statements | Where-Object { $_ -isnot [Management.Automation.Language.FunctionDefinitionAst] } | ForEach-Object { $_.Extent.Text }) -join "`n")

function Invoke-RunnerFixture([string] $Name, [string] $Platform = 'x86', [hashtable] $Overrides = @{}, [bool] $LegacyPositional = $false) {
    foreach ($definition in $functions) { . ([scriptblock]::Create($definition.Extent.Text)) }
    # Fixtures override only the OS-read boundary; the actual production body,
    # suite dispatch, environment scopes and result validators execute below.
    function Get-ItemProperty { [pscustomobject]@{ Release = $global:ContractRegistryViews[0].Release; Version = '4.8-fixture' } }
    function Get-Framework48RegistryViews { $global:ContractRegistryViews }
    $arguments = @{
        TestAssembly = (Join-Path $assemblyRoot 'compat.dll'); VSTestConsole = $console
        ConsumerExecutable = $harnessSample; ExampleExecutable = $sample
        ResultsDirectory = (Join-Path $EvidenceDirectory $Name); Platform = $Platform
        BidiTestAssembly = (Join-Path $assemblyRoot 'bidi.dll'); OpenTelemetryTestAssembly = (Join-Path $assemblyRoot 'otel.dll')
    }
    foreach ($family in @('Monty', 'Cedar')) {
        if ($parameterNames -contains ($family + 'TestAssembly')) {
            $arguments[$family + 'TestAssembly'] = Join-Path $assemblyRoot ($family.ToLowerInvariant() + '.dll')
            $arguments[$family + 'TestResultValidator'] = Join-Path $NativeValidatorRoot ("Strands.Agents.$family/native/Assert-TestResults.ps1")
            $arguments[$family + 'ConsumerExecutable'] = $nativeSample
        }
    }
    if ($parameterNames -contains 'BedrockConsumerExecutable') { $arguments.BedrockConsumerExecutable = $bedrockSample }
    foreach ($key in $Overrides.Keys) { $arguments[$key] = $Overrides[$key] }
    if ($LegacyPositional) {
        # These are the established positional slots through Platform, before Nova was added.
        $legacyNames = @('TestAssembly', 'VSTestConsole', 'ConsumerExecutable', 'ExampleExecutable', 'ResultsDirectory',
            'SageMakerConsumerExecutable', 'BidiTestAssembly', 'BidiConsumerExecutable', 'AudioTestAssembly', 'AudioConsumerExecutable',
            'OpenTelemetryTestAssembly', 'OpenTelemetryConsumerExecutable', 'MontyTestAssembly', 'MontyTestResultValidator', 'MontyConsumerExecutable',
            'CedarTestAssembly', 'CedarTestResultValidator', 'CedarConsumerExecutable', 'BedrockConsumerExecutable', 'Platform')
        $legacyArguments = [object[]]::new($legacyNames.Count)
        for ($index = 0; $index -lt $legacyNames.Count; $index++) { $legacyArguments[$index] = $arguments[$legacyNames[$index]] }
        & ([scriptblock]::Create($body)) @legacyArguments
    } else { & ([scriptblock]::Create($body)) @arguments }
}

$results = @()
try {
    Invoke-RunnerFixture 'five-suite-dispatch' *> (Join-Path $EvidenceDirectory 'five-suite-dispatch.log')
    $actual = @($global:ContractObservedSuites | Sort-Object)
    if (($actual -join ',') -cne 'bidi,cedar,compat,monty,otel') { throw "Expected all five families; observed $($actual -join ',')." }
    $results += @{ Name = 'AllFiveSuiteFamiliesAreActuallyDispatched'; Passed = $true }
} catch { $results += @{ Name = 'AllFiveSuiteFamiliesAreActuallyDispatched'; Passed = $false; Error = $_.Exception.Message } }
$global:ContractObservedSuites.Clear()
$global:ContractRegistryViews[1].Release = 533320
try {
    $rejected = $false
    try { Invoke-RunnerFixture 'mixed-registry-views' *> (Join-Path $EvidenceDirectory 'mixed-registry-views.log') }
    catch { if ($_.Exception.Message -match '(?i)4\.8|registry|release') { $rejected = $true } else { throw } }
    if (-not $rejected -or $global:ContractObservedSuites.Count -ne 0) { throw 'A 4.8.1 second registry view must reject before any suite.' }
    $results += @{ Name = 'EitherRegistryViewWith481RejectsBeforeExecution'; Passed = $true }
} catch { $results += @{ Name = 'EitherRegistryViewWith481RejectsBeforeExecution'; Passed = $false; Error = $_.Exception.Message } }
$global:ContractRegistryViews[1].Release = 528049
foreach ($definition in $functions) { . ([scriptblock]::Create($definition.Extent.Text)) }
if (Get-Command Assert-Framework48RegistryViews -ErrorAction SilentlyContinue) {
    foreach ($view in @('Registry64', 'Registry32')) {
        foreach ($release in @($null, 'not-an-integer', 528039, 533320)) {
            $name = "RejectInvalid-$view-$release"
            try {
                $rows = @([pscustomobject]@{ View = 'Registry64'; Release = 528040 }, [pscustomobject]@{ View = 'Registry32'; Release = 533319 })
                @($rows | Where-Object View -CEQ $view)[0].Release = $release
                $rejected = $false
                try { Assert-Framework48RegistryViews -Views $rows } catch { $rejected = $true }
                if (-not $rejected) { throw 'Invalid registry release was accepted.' }
                $results += @{ Name = $name; Passed = $true }
            } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
        }
    }
    foreach ($name in @('ValidBoundaryReleases', 'MissingRegistryView', 'DuplicateRegistryView')) {
        try {
            $rows = @([pscustomobject]@{ View = 'Registry64'; Release = 528040 }, [pscustomobject]@{ View = 'Registry32'; Release = 533319 })
            if ($name -eq 'MissingRegistryView') { $rows = @($rows[0]) }
            if ($name -eq 'DuplicateRegistryView') { $rows[1].View = 'Registry64' }
            $rejected = $false
            try { Assert-Framework48RegistryViews -Views $rows } catch { $rejected = $true }
            if ($rejected -ne ($name -ne 'ValidBoundaryReleases')) { throw 'Registry cardinality/boundary policy differs.' }
            $results += @{ Name = $name; Passed = $true }
        } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
    }
    foreach ($family in @('Monty', 'Cedar')) {
        $name = "IncompleteNativeGroup-$family"
        try {
            $global:ContractObservedSuites.Clear()
            $overrides = @{}; $overrides[$family + 'TestResultValidator'] = $null
            $rejected = $false
            try { Invoke-RunnerFixture $name 'x86' $overrides *> (Join-Path $EvidenceDirectory "$name.log") } catch { $rejected = $_.Exception.Message -match 'all three' }
            if (-not $rejected -or $global:ContractObservedSuites.Count -ne 0) { throw 'Incomplete native inputs must reject before suites.' }
            $results += @{ Name = $name; Passed = $true }
        } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
    }
    $name = 'X64AllFiveAndEnvironmentRestored'
    $priorValues = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
    try {
        $env:STRANDS_TEST_PROCESS_ARCHITECTURE = 'prior-common'; $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE = 'prior-monty'; $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE = 'prior-cedar'
        $global:ContractObservedSuites.Clear()
        Invoke-RunnerFixture $name 'x64' @{} *> (Join-Path $EvidenceDirectory "$name.log")
        if (($global:ContractObservedSuites | Sort-Object) -join ',' -cne 'bidi,cedar,compat,monty,otel') { throw 'x64 suite coverage differs.' }
        if ($env:STRANDS_TEST_PROCESS_ARCHITECTURE -cne 'prior-common' -or $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE -cne 'prior-monty' -or $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE -cne 'prior-cedar') { throw 'Expected architecture environment was not restored.' }
        $results += @{ Name = $name; Passed = $true }
    } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
    finally {
        $env:STRANDS_TEST_PROCESS_ARCHITECTURE = $priorValues[0]; $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE = $priorValues[1]; $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE = $priorValues[2]
    }
    $name = 'FailedNativeConsumerRejectsAndRestoresEnvironment'
    $badConsumer = Join-Path $EvidenceDirectory 'bad-native-consumer.ps1'
    "'wrong native pointer marker'; `$global:LASTEXITCODE = 7" | Set-Content -LiteralPath $badConsumer -Encoding UTF8
    $before = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
    try {
        $rejected = $false
        try { Invoke-RunnerFixture $name 'x86' @{ MontyConsumerExecutable = $badConsumer } *> (Join-Path $EvidenceDirectory "$name.log") }
        catch { $rejected = $_.Exception.Message -match 'monty package consumer failed' }
        $after = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
        if (-not $rejected) { throw 'Native consumer failure was not rejected.' }
        for ($index = 0; $index -lt 3; $index++) {
            if ($before[$index] -cne $after[$index]) { throw 'Native consumer failure did not restore its architecture environment.' }
        }
        $results += @{ Name = $name; Passed = $true }
    } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
    $name = 'InheritedStrictModeStillDispatchesCommonAndNativeSuites'
    try {
        Set-StrictMode -Version Latest
        Invoke-RunnerFixture $name 'x86' @{} *> (Join-Path $EvidenceDirectory "$name.log")
        $results += @{ Name = $name; Passed = $true }
    } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
    finally { Set-StrictMode -Off }
}
foreach ($family in @('compat', 'bidi', 'otel', 'monty', 'cedar', 's3', 'sqlite')) {
    foreach ($platform in @('x64', 'x86')) {
        foreach ($variant in @('OriginalPass', 'WrongArchitecture', 'MissingEvidence', 'Skipped', 'Empty', 'Failed')) {
            $name = "$family-$platform-$variant"
            try {
                $directory = Join-Path $EvidenceDirectory $name
                New-Item -ItemType Directory -Path $directory | Out-Null
                $destination = Join-Path $directory 'fixture.trx'
                [xml] $document = Get-Content -Raw -LiteralPath (Join-Path $global:ContractFixtures "$family-$platform.trx")
                if ($variant -eq 'MissingEvidence') { $document.TestRun.Results.UnitTestResult.Output.StdOut = '' }
                if ($variant -eq 'Skipped') { $document.TestRun.ResultSummary.Counters.passed = '0'; $document.TestRun.ResultSummary.Counters.notExecuted = '1'; $document.TestRun.Results.UnitTestResult.outcome = 'NotExecuted' }
                if ($variant -eq 'Failed') { $document.TestRun.ResultSummary.Counters.passed = '0'; $document.TestRun.ResultSummary.Counters.failed = '1'; $document.TestRun.Results.UnitTestResult.outcome = 'Failed' }
                if ($variant -eq 'Empty') { foreach ($counter in @('total', 'executed', 'passed')) { $document.TestRun.ResultSummary.Counters.SetAttribute($counter, '0') }; $document.TestRun.Results.RemoveAll() }
                $document.Save($destination)
                $expected = if ($variant -eq 'WrongArchitecture') { if ($platform -eq 'x64') { 'x86' } else { 'x64' } } else { $platform }
                $rejected = $false
                try {
                    if ($family -in @('monty', 'cedar')) {
                        $validator = Join-Path $NativeValidatorRoot ("Strands.Agents." + (Get-Culture).TextInfo.ToTitleCase($family) + '/native/Assert-TestResults.ps1')
                        & $validator -ResultsDirectory $directory -ExpectedArchitecture $expected.ToUpperInvariant() *> (Join-Path $directory 'validator.log')
                    } else { Assert-Framework48TestResults -TrxPath $destination -ExpectedPlatform $expected *> (Join-Path $directory 'validator.log') }
                } catch { $rejected = $true }
                if ($rejected -ne ($variant -ne 'OriginalPass')) { throw 'Validator outcome differs from the deliberate fixture condition.' }
                $results += @{ Name = $name; Passed = $true }
            } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
        }
    }
}

# Execute the production body with optional S3 inputs. The script fixtures above
# remain S3-free to preserve the existing optional/default suite contract.
foreach ($platform in @('x64', 'x86')) {
    foreach ($variant in @('Pass', 'FailedExit', 'MissingMarker', 'WrongPointer', 'MissingAssembly', 'MissingConsumer', 'CorruptSuite')) {
        $name = "S3-$platform-$variant"
        $before = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
        $priorFixtures = $global:ContractFixtures
        try {
            $global:ContractObservedSuites.Clear()
            $global:ContractObservedS3Pointers.Clear()
            $global:ContractS3Mode = $variant
            $overrides = @{ S3TestAssembly = (Join-Path $assemblyRoot 's3.dll'); S3ConsumerExecutable = $s3Sample }
            if ($variant -eq 'MissingAssembly') { $overrides.S3TestAssembly = Join-Path $assemblyRoot 'missing-s3.dll' }
            if ($variant -eq 'MissingConsumer') { $overrides.S3ConsumerExecutable = Join-Path $assemblyRoot 'missing-s3.exe' }
            if ($variant -eq 'CorruptSuite') {
                $corrupt = Join-Path $EvidenceDirectory "$name-fixtures"
                New-Item -ItemType Directory -Path $corrupt | Out-Null
                Copy-Item -Path (Join-Path $priorFixtures '*.trx') -Destination $corrupt
                [xml] $document = Get-Content -Raw -LiteralPath (Join-Path $corrupt "s3-$platform.trx")
                $document.TestRun.Results.UnitTestResult.Output.StdOut = ''
                $document.Save((Join-Path $corrupt "s3-$platform.trx"))
                $global:ContractFixtures = $corrupt
            }
            $rejected = $false
            try { Invoke-RunnerFixture $name $platform $overrides *> (Join-Path $EvidenceDirectory "$name.log") }
            catch {
                if ($variant -eq 'Pass') { throw }
                $expected = switch ($variant) {
                    'MissingAssembly' { 'S3 verification artifact is missing' }
                    'MissingConsumer' { 'S3 verification artifact is missing' }
                    'CorruptSuite' { 'runtime evidence|ConvertFrom-Json|architecture evidence|JSON' }
                    default { 'S3 package consumer failed' }
                }
                if ($_.Exception.Message -notmatch $expected) { throw }
                $rejected = $true
            }
            if ($rejected -ne ($variant -ne 'Pass')) { throw "S3 fixture $variant did not produce its required outcome." }
            if ($variant -in @('MissingAssembly', 'MissingConsumer')) {
                if ($global:ContractObservedSuites.Count -ne 0) { throw 'Missing S3 input must reject before any suite.' }
            } elseif ($variant -eq 'Pass') {
                if (($global:ContractObservedSuites | Sort-Object) -join ',' -cne 'bidi,cedar,compat,monty,otel,s3') { throw 'The six suite families were not dispatched.' }
                $pointer = if ($platform -eq 'x86') { '4' } else { '8' }
                if ($global:ContractObservedS3Pointers.Count -ne 1 -or $global:ContractObservedS3Pointers[0] -cne $pointer) { throw 'S3 consumer did not execute once with the required pointer.' }
                if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $EvidenceDirectory $name) 's3-package-consumer.log'))) { throw 'S3 consumer evidence was not retained.' }
            }
            $after = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
            for ($index = 0; $index -lt 3; $index++) { if ($before[$index] -cne $after[$index]) { throw 'S3 dispatch did not restore its architecture environment.' } }
            $results += @{ Name = $name; Passed = $true }
        } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
        finally {
            $global:ContractS3Mode = 'Pass'
            $global:ContractFixtures = $priorFixtures
            $env:STRANDS_TEST_PROCESS_ARCHITECTURE = $before[0]; $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE = $before[1]; $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE = $before[2]
        }
    }
}

# SQLite is independently optional and composes with the existing S3 branch.
# These are script fixtures; the native DLL above is synthetic and is never loaded.
foreach ($platform in @('x64', 'x86')) {
    foreach ($variant in @('Pass', 'WriterExit', 'ReaderExit', 'MissingWriterMarker', 'MissingReaderMarker', 'DuplicateMarker', 'MalformedMarker', 'WrongPointer', 'WrongArchitecture', 'WrongMode', 'MissingAssembly', 'MissingConsumer', 'CorruptSuite', 'FailedFts', 'NativeHashMismatch', 'WrongRank', 'WrongNativePath')) {
        $name = "SQLiteConsumer-$platform-$variant"
        $before = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
        $priorFixtures = $global:ContractFixtures
        try {
            $global:ContractObservedSuites.Clear()
            $global:ContractObservedSqliteCalls.Clear()
            $global:ContractSqliteMode = $variant
            $overrides = @{ SqliteTestAssembly = (Join-Path $assemblyRoot 'sqlite.dll'); SqliteConsumerExecutable = $sqliteSample; S3TestAssembly = (Join-Path $assemblyRoot 's3.dll'); S3ConsumerExecutable = $s3Sample }
            if ($variant -eq 'MissingAssembly') { $overrides.SqliteTestAssembly = Join-Path $assemblyRoot 'missing-sqlite.dll' }
            if ($variant -eq 'MissingConsumer') { $overrides.SqliteConsumerExecutable = Join-Path $assemblyRoot 'missing-sqlite.exe' }
            if ($variant -eq 'CorruptSuite') {
                $corrupt = Join-Path $EvidenceDirectory "$name-fixtures"
                New-Item -ItemType Directory -Path $corrupt | Out-Null
                Copy-Item -Path (Join-Path $priorFixtures '*.trx') -Destination $corrupt
                [xml] $document = Get-Content -Raw -LiteralPath (Join-Path $corrupt "sqlite-$platform.trx")
                $document.TestRun.Results.UnitTestResult.Output.StdOut = ''
                $document.Save((Join-Path $corrupt "sqlite-$platform.trx"))
                $global:ContractFixtures = $corrupt
            }
            $rejected = $false
            try { Invoke-RunnerFixture $name $platform $overrides *> (Join-Path $EvidenceDirectory "$name.log") }
            catch {
                if ($variant -eq 'Pass') { throw }
                $expected = switch ($variant) {
                    'MissingAssembly' { 'SQLite verification artifact is missing' }
                    'MissingConsumer' { 'SQLite verification artifact is missing' }
                    'CorruptSuite' { 'runtime evidence|ConvertFrom-Json|architecture evidence|JSON' }
                    default { 'SQLite.*consumer|SQLite.*evidence' }
                }
                if ($_.Exception.Message -notmatch $expected) { throw }
                $rejected = $true
            }
            if ($rejected -ne ($variant -ne 'Pass')) { throw "SQLite fixture $variant did not produce its required outcome." }
            if ($variant -in @('MissingAssembly', 'MissingConsumer')) {
                if ($global:ContractObservedSuites.Count -ne 0) { throw 'Missing SQLite input must reject before any suite.' }
            } elseif ($variant -eq 'Pass') {
                if (($global:ContractObservedSuites | Sort-Object) -join ',' -cne 'bidi,cedar,compat,monty,otel,s3,sqlite') { throw 'The seven suite families were not dispatched.' }
                $pointer = if ($platform -eq 'x86') { '4' } else { '8' }
                $storage = Join-Path (Join-Path $EvidenceDirectory $name) 'sqlite-storage'
                if ($global:ContractObservedSqliteCalls.Count -ne 2 -or $global:ContractObservedSqliteCalls[0] -cne "write|$storage|$pointer" -or $global:ContractObservedSqliteCalls[1] -cne "read|$storage|$pointer") { throw 'SQLite writer and independent reader did not execute in order with the writable storage path and requested pointer.' }
                foreach ($mode in @('write', 'read')) {
                    if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $EvidenceDirectory $name) "sqlite-$mode-package-consumer.log"))) { throw 'SQLite consumer evidence was not retained.' }
                }
            } elseif ($variant -eq 'WriterExit' -and $global:ContractObservedSqliteCalls.Count -ne 1) { throw 'A failed writer must not run the reader.' }
            $after = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
            for ($index = 0; $index -lt 3; $index++) { if ($before[$index] -cne $after[$index]) { throw 'SQLite dispatch did not restore its architecture environment.' } }
            $results += @{ Name = $name; Passed = $true }
        } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
        finally {
            $global:ContractSqliteMode = 'Pass'
            $global:ContractFixtures = $priorFixtures
            $env:STRANDS_TEST_PROCESS_ARCHITECTURE = $before[0]; $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE = $before[1]; $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE = $before[2]
        }
    }
}

# Exercise the optional Knowledge Base branch together with S3 and SQLite.
foreach ($platform in @('x64', 'x86')) {
    foreach ($variant in @('Pass', 'MissingAssembly', 'MissingConsumer', 'BadTrx', 'FailedExit', 'MissingMarker', 'WrongPointer', 'DuplicateMarker')) {
        $name = "KnowledgeBase-$platform-$variant"
        $before = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
        $priorFixtures = $global:ContractFixtures
        try {
            $global:ContractObservedSuites.Clear(); $global:ContractKbPointers.Clear()
            $global:ContractKbMode = $variant
            $overrides = @{ BedrockKnowledgeBaseTestAssembly = (Join-Path $assemblyRoot 'kb.dll'); BedrockKnowledgeBaseConsumerExecutable = $kbSample; S3TestAssembly = (Join-Path $assemblyRoot 's3.dll'); S3ConsumerExecutable = $s3Sample; SqliteTestAssembly = (Join-Path $assemblyRoot 'sqlite.dll'); SqliteConsumerExecutable = $sqliteSample }
            if ($variant -eq 'MissingAssembly') { $overrides.BedrockKnowledgeBaseTestAssembly = Join-Path $assemblyRoot 'missing-kb.dll' }
            if ($variant -eq 'MissingConsumer') { $overrides.BedrockKnowledgeBaseConsumerExecutable = Join-Path $assemblyRoot 'missing-kb.exe' }
            if ($variant -eq 'BadTrx') {
                $bad = Join-Path $EvidenceDirectory "$name-fixtures"
                New-Item -ItemType Directory -Path $bad | Out-Null
                Get-ChildItem -LiteralPath $priorFixtures -Filter '*.trx' | Copy-Item -Destination $bad
                $path = Join-Path $bad "kb-$platform.trx"
                [xml] $doc = Get-Content -Raw -LiteralPath $path
                $doc.TestRun.ResultSummary.Counters.SetAttribute('failed', '1')
                $doc.Save($path)
                $global:ContractFixtures = $bad
            }
            $rejected = $false
            try { Invoke-RunnerFixture $name $platform $overrides *> (Join-Path $EvidenceDirectory "$name.log") }
            catch {
                $message = $_.Exception.Message
                $expected = switch ($variant) {
                    'MissingAssembly' { 'Knowledge Base verification artifact is missing' }
                    'MissingConsumer' { 'Knowledge Base verification artifact is missing' }
                    'BadTrx' { 'fully executed, passing test run' }
                    default { 'Knowledge Base package consumer failed' }
                }
                if ($message -notlike "*$expected*") { throw }
                $rejected = $true
            }
            if ($rejected -ne ($variant -ne 'Pass')) { throw "KB fixture $variant did not produce its required outcome." }
            if ($variant -in @('MissingAssembly', 'MissingConsumer')) {
                if ($global:ContractObservedSuites.Count -ne 0) { throw 'Missing KB input must reject before any suite.' }
            } else {
                if (@($global:ContractObservedSuites | Where-Object { $_ -ceq 'kb' }).Count -ne 1) { throw 'KB test suite did not execute exactly once.' }
                if ($variant -eq 'BadTrx') {
                    if ($global:ContractKbPointers.Count -ne 0) { throw 'Failed KB tests must not run a package consumer.' }
                } else {
                    $pointer = if ($platform -eq 'x86') { '4' } else { '8' }
                    if ($global:ContractKbPointers.Count -ne 1 -or $global:ContractKbPointers[0] -cne $pointer) { throw 'KB package consumer did not use the requested architecture.' }
                    if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $EvidenceDirectory $name) 'knowledge-base-package-consumer.log'))) { throw 'KB consumer diagnostics were not retained.' }
                }
            }
            $after = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
            for ($index = 0; $index -lt 3; $index++) { if ($before[$index] -cne $after[$index]) { throw 'KB dispatch did not restore its architecture environment.' } }
            $results += @{ Name = $name; Passed = $true }
        } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
        finally {
            $global:ContractKbMode = 'Pass'; $global:ContractFixtures = $priorFixtures
            $env:STRANDS_TEST_PROCESS_ARCHITECTURE = $before[0]; $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE = $before[1]; $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE = $before[2]
        }
    }
}

# Exercise the optional Google Auth suite dispatch and rejection boundaries on both architectures.
New-Item -ItemType File -Path (Join-Path $assemblyRoot 'googleauth.dll') | Out-Null
foreach ($platform in @('x64', 'x86')) {
    foreach ($variant in @('Pass', 'MissingAssembly', 'CorruptSuite', 'WrongPointer')) {
        $name = "GoogleAuth-$platform-$variant"
        $before = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
        $priorFixtures = $global:ContractFixtures
        try {
            $global:ContractObservedSuites.Clear()
            $overrides = @{ GoogleAuthTestAssembly = (Join-Path $assemblyRoot 'googleauth.dll') }
            if ($variant -eq 'MissingAssembly') { $overrides.GoogleAuthTestAssembly = Join-Path $assemblyRoot 'missing-googleauth.dll' }
            if ($variant -in @('CorruptSuite', 'WrongPointer')) {
                $bad = Join-Path $EvidenceDirectory "$name-fixtures"
                New-Item -ItemType Directory -Path $bad | Out-Null
                Get-ChildItem -LiteralPath $priorFixtures -Filter '*.trx' | Copy-Item -Destination $bad
                $path = Join-Path $bad "googleauth-$platform.trx"
                [xml] $document = Get-Content -Raw -LiteralPath $path
                if ($variant -eq 'CorruptSuite') { $document.TestRun.ResultSummary.Counters.SetAttribute('failed', '1') }
                else {
                    $record = $document.TestRun.Results.UnitTestResult.Output.StdOut | ConvertFrom-Json
                    $record.PointerSize = 99
                    $document.TestRun.Results.UnitTestResult.Output.StdOut = [string]($record | ConvertTo-Json -Compress)
                }
                $document.Save($path)
                $global:ContractFixtures = $bad
            }
            $rejected = $false
            try { Invoke-RunnerFixture $name $platform $overrides *> (Join-Path $EvidenceDirectory "$name.log") }
            catch {
                $expected = switch ($variant) {
                    'MissingAssembly' { 'Google Auth verification artifact is missing' }
                    'CorruptSuite' { 'fully executed, passing test run' }
                    'WrongPointer' { 'Framework architecture evidence does not match' }
                    default { throw }
                }
                if ($_.Exception.Message -notlike "*$expected*") { throw }
                $rejected = $true
            }
            if ($rejected -ne ($variant -ne 'Pass')) { throw "Google Auth fixture $variant did not produce its required outcome." }
            $dispatched = @($global:ContractObservedSuites | Where-Object { $_ -ceq 'googleauth' }).Count
            if ($variant -eq 'MissingAssembly') {
                if ($global:ContractObservedSuites.Count -ne 0) { throw 'Missing Google Auth input must reject before any suite.' }
            } elseif ($dispatched -ne 1) { throw 'Google Auth suite must execute exactly once.' }
            $after = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
            for ($index = 0; $index -lt 3; $index++) { if ($before[$index] -cne $after[$index]) { throw 'Google Auth dispatch did not restore its architecture environment.' } }
            $results += @{ Name = $name; Passed = $true }
        } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
        finally {
            $global:ContractFixtures = $priorFixtures
            $env:STRANDS_TEST_PROCESS_ARCHITECTURE = $before[0]; $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE = $before[1]; $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE = $before[2]
        }
    }
}


# Execute the full Harness package consumer on both architectures before the AnyCPU-only branch.
foreach ($platform in @('x64', 'x86')) {
    foreach ($variant in @('Pass', 'MissingConsumer', 'FailedExit', 'MissingMarker', 'WrongPointer', 'WrongAsset', 'DuplicateMarker', 'MissingCycle', 'MissingStop')) {
        $name = "FullHarness-$platform-$variant"
        $before = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
        try {
            $global:ContractObservedSuites.Clear(); $global:ContractObservedHarnessPointers.Clear()
            $global:ContractHarnessMode = $variant
            $overrides = @{}
            if ($variant -eq 'MissingConsumer') { $overrides.ConsumerExecutable = Join-Path $assemblyRoot 'missing-harness.exe' }
            $rejected = $false
            try { Invoke-RunnerFixture $name $platform $overrides *> (Join-Path $EvidenceDirectory "$name.log") }
            catch {
                $expected = if ($variant -eq 'MissingConsumer') { 'Required executable/assembly is missing' } else { 'full Harness package consumer failed' }
                if ($_.Exception.Message -notlike "*$expected*") { throw }
                $rejected = $true
            }
            if ($rejected -ne ($variant -ne 'Pass')) { throw "Full Harness fixture $variant did not produce its required outcome." }
            if ($variant -eq 'MissingConsumer') {
                if ($global:ContractObservedSuites.Count -ne 0 -or $global:ContractObservedHarnessPointers.Count -ne 0) { throw 'Missing full Harness input must reject before any suite or consumer.' }
            } else {
                $pointer = if ($platform -eq 'x86') { '4' } else { '8' }
                if ($global:ContractObservedHarnessPointers.Count -ne 1 -or $global:ContractObservedHarnessPointers[0] -cne $pointer) { throw 'The full Harness package consumer did not execute once with the requested architecture.' }
                if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $EvidenceDirectory $name) 'package-consumer.log'))) { throw 'Full Harness consumer diagnostics were not retained.' }
            }
            $after = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
            for ($index = 0; $index -lt 3; $index++) { if ($before[$index] -cne $after[$index]) { throw 'Full Harness dispatch did not restore its architecture environment.' } }
            $results += @{ Name = $name; Passed = $true }
        } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
        finally {
            $global:ContractHarnessMode = 'Pass'
            $env:STRANDS_TEST_PROCESS_ARCHITECTURE = $before[0]; $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE = $before[1]; $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE = $before[2]
        }
    }
}

# Audio is optional; suite and native-load consumer run before the x86 return.
foreach ($platform in @('x64', 'x86')) {
    foreach ($variant in @('Pass', 'MissingAssembly', 'MissingConsumer', 'WrongPointer', 'WrongAsset', 'MissingMarker', 'DuplicateMarker', 'FailedExit')) {
        $name = "Audio-$platform-$variant"
        $before = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
        try {
            $global:ContractObservedSuites.Clear(); $global:ContractAudioPointers.Clear()
            $global:ContractAudioMode = $variant
            $overrides = @{ AudioTestAssembly = (Join-Path $assemblyRoot 'audio.dll'); AudioConsumerExecutable = $audioSample }
            if ($variant -eq 'MissingAssembly') { $overrides.AudioTestAssembly = Join-Path $assemblyRoot 'missing-audio.dll' }
            if ($variant -eq 'MissingConsumer') { $overrides.AudioConsumerExecutable = Join-Path $assemblyRoot 'missing-audio.exe' }
            $rejected = $false
            try { Invoke-RunnerFixture $name $platform $overrides *> (Join-Path $EvidenceDirectory "$name.log") }
            catch {
                $expected = if ($variant -in @('MissingAssembly', 'MissingConsumer')) { 'Audio verification artifact is missing' } else { 'Audio package consumer failed' }
                if ($_.Exception.Message -notlike "*$expected*") { throw }
                $rejected = $true
            }
            if ($rejected -ne ($variant -ne 'Pass')) { throw "Audio fixture $variant did not produce its required outcome." }
            if ($variant -in @('MissingAssembly', 'MissingConsumer')) {
                if ($global:ContractObservedSuites.Count -ne 0 -or $global:ContractAudioPointers.Count -ne 0) { throw 'Missing Audio artifacts must reject before dispatch.' }
            } else {
                if (@($global:ContractObservedSuites | Where-Object { $_ -ceq 'audio' }).Count -ne 1) { throw 'The Audio suite must execute once.' }
                $pointer = if ($platform -eq 'x86') { '4' } else { '8' }
                if ($global:ContractAudioPointers.Count -ne 1 -or $global:ContractAudioPointers[0] -cne $pointer) { throw 'Audio consumer must execute once with its requested architecture.' }
                if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $EvidenceDirectory $name) 'audio-package-consumer.log'))) { throw 'Audio diagnostics were not retained.' }
            }
            $after = @($env:STRANDS_TEST_PROCESS_ARCHITECTURE, $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE, $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE)
            for ($index = 0; $index -lt 3; $index++) { if ($before[$index] -cne $after[$index]) { throw 'Audio dispatch did not restore its environment.' } }
            $results += @{ Name = $name; Passed = $true }
        } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
        finally {
            $global:ContractAudioMode = 'Pass'
            $env:STRANDS_TEST_PROCESS_ARCHITECTURE = $before[0]; $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE = $before[1]; $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE = $before[2]
        }
    }
}

# The optional native ABI consumer preserves the existing Bedrock chat consumer and both architecture branches.
foreach ($platform in @('x64', 'x86')) {
    foreach ($variant in @('Pass', 'MissingConsumer', 'FailedExit', 'MissingMarker', 'DuplicateMarker', 'WrongPointer', 'WrongAsset', 'WrongAbi')) {
        $name = "Nova-$platform-$variant"
        $beforePath = $env:PATH
        try {
            $global:ContractObservedSuites.Clear(); $global:ContractNovaPointers.Clear()
            $global:ContractNovaMode = $variant
            $overrides = @{ BedrockNovaConsumerExecutable = $novaSample }
            if ($variant -eq 'MissingConsumer') { $overrides.BedrockNovaConsumerExecutable = Join-Path $assemblyRoot 'missing-nova.exe' }
            $rejected = $false
            try { Invoke-RunnerFixture $name $platform $overrides *> (Join-Path $EvidenceDirectory "$name.log") }
            catch {
                $expected = if ($variant -eq 'MissingConsumer') { 'Nova verification artifact is missing' } else { 'Nova native package consumer failed' }
                if ($_.Exception.Message -notlike "*$expected*") { throw }
                $rejected = $true
            }
            if ($rejected -ne ($variant -ne 'Pass')) { throw "Nova fixture $variant did not produce the required outcome." }
            if ($variant -eq 'MissingConsumer') {
                if ($global:ContractObservedSuites.Count -ne 0 -or $global:ContractNovaPointers.Count -ne 0) { throw 'Missing Nova consumer must reject before dispatch.' }
            } else {
                $pointer = if ($platform -eq 'x86') { '4' } else { '8' }
                if ($global:ContractNovaPointers.Count -ne 1 -or $global:ContractNovaPointers[0] -cne $pointer) { throw 'The Nova consumer must execute once with the requested architecture.' }
                if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $EvidenceDirectory $name) 'nova-native-package-consumer.log'))) { throw 'Nova consumer diagnostics must be retained.' }
            }
            if ($env:PATH -cne $beforePath) { throw 'Framework runner must restore interpreter PATH.' }
            $results += @{ Name=$name; Passed=$true }
        } catch { $results += @{ Name=$name; Passed=$false; Error=$_.Exception.Message } }
        finally { $global:ContractNovaMode = 'Pass'; $env:PATH = $beforePath }
    }
}

foreach ($family in @('Core', 'GoogleAuth')) {
    foreach ($platform in @('x86', 'x64')) {
        foreach ($variant in @('Pass', 'MissingConsumer', 'MissingMarker', 'WrongPointer')) {
            $name = "Managed-$family-$platform-$variant"
            try {
                $global:ContractObservedSuites.Clear(); $global:ContractManagedCalls.Clear()
                $global:ContractManagedModes[$family] = $variant
                $path = if ($variant -eq 'MissingConsumer') { Join-Path $assemblyRoot 'missing-managed.exe' } else { $managedSamples[$family] }
                $overrides = @{}; $overrides[$family + 'ConsumerExecutable'] = $path
                $rejected = $false
                try { Invoke-RunnerFixture $name $platform $overrides *> (Join-Path $EvidenceDirectory "$name.log") }
                catch {
                    $expected = if ($variant -eq 'MissingConsumer') { "$family managed package consumer is missing" } else { "$family managed package consumer failed" }
                    if ($_.Exception.Message -notlike "*$expected*") { throw }
                    $rejected = $true
                }
                if ($rejected -ne ($variant -ne 'Pass')) { throw 'Managed consumer dispatch did not produce the required outcome.' }
                if ($variant -eq 'MissingConsumer') {
                    if ($global:ContractObservedSuites.Count -ne 0 -or $global:ContractManagedCalls.Count -ne 0) { throw 'Missing consumer must reject before suite dispatch.' }
                } else {
                    $pointer = if ($platform -eq 'x86') { '4' } else { '8' }
                    if ($global:ContractManagedCalls.Count -ne 1 -or $global:ContractManagedCalls[0] -cne "$family|$pointer") { throw 'Managed consumer must execute once before the x86 return with the requested pointer.' }
                    if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $EvidenceDirectory $name) ($family.ToLowerInvariant() + '-package-consumer.log')))) { throw 'Managed consumer diagnostics must be retained.' }
                }
                $results += @{ Name = $name; Passed = $true }
            } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
            finally { $global:ContractManagedModes[$family] = 'Pass' }
        }
    }
}

$name = 'EstablishedPositionalPlatformSlotStillExecutesX86'
try {
    $global:ContractObservedSuites.Clear(); $global:ContractObservedHarnessPointers.Clear(); $global:ContractNovaPointers.Clear(); $global:ContractManagedCalls.Clear()
    Invoke-RunnerFixture $name 'x86' @{} $true *> (Join-Path $EvidenceDirectory "$name.log")
    if ($global:ContractObservedHarnessPointers.Count -ne 1 -or $global:ContractObservedHarnessPointers[0] -cne '4' -or
        $global:ContractNovaPointers.Count -ne 0 -or $global:ContractManagedCalls.Count -ne 0 -or $global:ContractObservedSuites.Count -ne 5) { throw 'The established positional Platform argument must retain x86 execution without opting into Nova.' }
    $results += @{ Name=$name; Passed=$true }
} catch { $results += @{ Name=$name; Passed=$false; Error=$_.Exception.Message } }

foreach ($family in @('SageMaker', 'Bidi', 'OpenTelemetry')) {
    foreach ($platform in @('x86', 'x64')) {
        foreach ($variant in @('Pass', 'MissingConsumer', 'MissingMarker', 'WrongPointer', 'WrongAsset', 'FailedExit', 'DuplicateMarker')) {
            $name = "Architecture-$family-$platform-$variant"
            try {
                $global:ContractObservedSuites.Clear(); $global:ContractArchitectureCalls.Clear()
                $global:ContractArchitectureModes[$family] = $variant
                $path = if ($variant -eq 'MissingConsumer') { Join-Path $assemblyRoot 'missing-architecture.exe' } else { $architectureSamples[$family] }
                $overrides = @{}; $overrides[$family + 'ConsumerExecutable'] = $path
                $rejected = $false
                try { Invoke-RunnerFixture $name $platform $overrides *> (Join-Path $EvidenceDirectory "$name.log") }
                catch {
                    if ($_.Exception.Message -notmatch '(?i)(consumer|verification artifact).*(missing|failed|complete)') { throw }
                    $rejected = $true
                }
                if ($rejected -ne ($variant -ne 'Pass')) { throw 'Architecture-specific consumer did not produce the required outcome.' }
                if ($variant -eq 'MissingConsumer') {
                    if ($global:ContractObservedSuites.Count -ne 0 -or $global:ContractArchitectureCalls.Count -ne 0) { throw 'A missing architecture consumer must reject before suite execution.' }
                } else {
                    $pointer = if ($platform -eq 'x86') { '4' } else { '8' }
                    if ($global:ContractArchitectureCalls.Count -ne 1 -or $global:ContractArchitectureCalls[0] -cne "$family|$pointer") { throw 'The selected consumer must execute once with the requested pointer before either architecture return.' }
                    if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $EvidenceDirectory $name) ($family.ToLowerInvariant() + '-package-consumer.log')))) { throw 'Architecture consumer diagnostics are missing.' }
                }
                $results += @{ Name=$name; Passed=$true }
            } catch { $results += @{ Name=$name; Passed=$false; Error=$_.Exception.Message } }
            finally { $global:ContractArchitectureModes[$family] = 'Pass' }
        }
    }
}

# A real Windows native child must not turn an ordinary stderr warning into a
# terminating PowerShell error before the actual exit code can be checked.
foreach ($nativeExit in @(0, 7)) {
    $name = "ProjectExampleNativeStderr-Exit$nativeExit"
    $nativeExample = Join-Path $EvidenceDirectory ("demo-warning-$nativeExit.cmd")
    [IO.File]::WriteAllLines($nativeExample, @(
        '@echo off',
        'echo 2 + 3 = 5.',
        'echo Stop: end_turn; model turns: 2',
        'echo Model context window is unknown; defaulting to 200000 tokens. 1>&2',
        "exit /b $nativeExit"
    ), [Text.Encoding]::ASCII)
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Stop'
        $rejected = $false
        try { Invoke-RunnerFixture $name 'x64' @{ ExampleExecutable = $nativeExample } *> (Join-Path $EvidenceDirectory "$name.log") }
        catch {
            if ($_.Exception.Message -notlike '*Framework 4.8 project-example did not complete the expected model/tool cycle*') { throw }
            $rejected = $true
        }
        if ($rejected -ne ($nativeExit -ne 0)) { throw 'The example must accept exit0 warnings and reject the actual nonzero native exit.' }
        $diagnostics = Get-Content -LiteralPath (Join-Path (Join-Path $EvidenceDirectory $name) 'project-example.log') -Raw
        foreach ($expected in @('2 + 3 = 5.', 'Stop: end_turn; model turns: 2', 'Model context window is unknown; defaulting to 200000 tokens.')) {
            if (-not $diagnostics.Contains($expected)) { throw "Project example output was not retained: $expected" }
        }
        if ($ErrorActionPreference -cne 'Stop') { throw 'The example changed the caller error preference.' }
        $results += @{ Name = $name; Passed = $true }
    } catch { $results += @{ Name = $name; Passed = $false; Error = $_.Exception.Message } }
    finally { $ErrorActionPreference = $previousPreference }
}

$report = [pscustomobject]@{ Scope = 'Script fixtures only; no clean-runtime/native execution claim'; ControllerPointerSize = [IntPtr]::Size; PowerShellVersion = $PSVersionTable.PSVersion.ToString(); Passed = @($results | Where-Object { $_.Passed }).Count; Failed = @($results | Where-Object { -not $_.Passed }).Count; Results = $results }
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'summary.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 8 | Write-Output
if ($report.Failed -ne 0) { exit 1 }
# Expected failing native fixtures must not leave their exit code as this script's result.
exit 0
