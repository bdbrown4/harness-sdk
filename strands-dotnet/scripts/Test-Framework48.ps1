[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $TestAssembly,
    [Parameter(Mandatory = $true)][string] $VSTestConsole,
    [Parameter(Mandatory = $true)][string] $ConsumerExecutable,
    [Parameter(Mandatory = $true)][string] $ExampleExecutable,
    [Parameter(Mandatory = $true)][string] $ResultsDirectory,
    [string] $SageMakerConsumerExecutable,
    [string] $BidiTestAssembly,
    [string] $BidiConsumerExecutable,
    [string] $AudioTestAssembly,
    [string] $AudioConsumerExecutable,
    [string] $OpenTelemetryTestAssembly,
    [string] $OpenTelemetryConsumerExecutable,
    [string] $MontyTestAssembly,
    [string] $MontyTestResultValidator,
    [string] $MontyConsumerExecutable,
    [string] $CedarTestAssembly,
    [string] $CedarTestResultValidator,
    [string] $CedarConsumerExecutable,
    [string] $BedrockConsumerExecutable,
    [ValidateSet('x86', 'x64')][string] $Platform = 'x64',
    [string] $S3TestAssembly,
    [string] $S3ConsumerExecutable,
    [string] $SqliteTestAssembly,
    [string] $SqliteConsumerExecutable,
    [string] $BedrockKnowledgeBaseTestAssembly,
    [string] $BedrockKnowledgeBaseConsumerExecutable,
    [string] $GoogleAuthTestAssembly,
    [string] $BedrockNovaConsumerExecutable,
    [string] $CoreConsumerExecutable,
    [string] $GoogleAuthConsumerExecutable
)

function Assert-Framework48TestResults {
    param(
        [Parameter(Mandatory = $true)][string] $TrxPath,
        [Parameter(Mandatory = $true)][ValidateSet('x86', 'x64')][string] $ExpectedPlatform
    )

    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) { throw "Framework test results are missing: $TrxPath" }
    $results = New-Object System.Xml.XmlDocument
    $results.XmlResolver = $null
    $results.Load($TrxPath)
    $namespaces = New-Object System.Xml.XmlNamespaceManager($results.NameTable)
    $namespaces.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $counters = $results.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $namespaces)
    if ($null -eq $counters) { throw 'Framework test results have no execution counters.' }
    $counts = @{}
    foreach ($name in @('total', 'executed', 'passed', 'failed', 'notExecuted')) {
        [long] $value = 0
        if (-not [long]::TryParse($counters.GetAttribute($name), [ref] $value) -or $value -lt 0) {
            throw "Framework test results have an invalid '$name' counter."
        }
        $counts[$name] = $value
    }
    if ($counts.executed -le 0 -or $counts.total -ne $counts.executed -or $counts.passed -ne $counts.executed -or $counts.failed -ne 0 -or $counts.notExecuted -ne 0) {
        throw 'Framework verification requires a nonempty, fully executed, passing test run.'
    }
    $testResults = @($results.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $namespaces))
    if ($testResults.Count -ne $counts.executed -or @($testResults | Where-Object { $_.GetAttribute('outcome') -cne 'Passed' }).Count -ne 0) {
        throw 'Framework result entries do not match the passing execution counters.'
    }
    $architectureDefinitions = @($results.SelectNodes("/t:TestRun/t:TestDefinitions/t:UnitTest[t:TestMethod[@className='Strands.Compatibility.Tests.RuntimeArchitectureCompatibilityTests' and @name='TestHostUsesTheRequestedProcessArchitecture']]", $namespaces))
    if ($architectureDefinitions.Count -ne 1) { throw 'Framework results must contain exactly one architecture assertion definition.' }
    $architectureId = $architectureDefinitions[0].GetAttribute('id')
    $architectureResults = @($testResults | Where-Object { $_.GetAttribute('testId') -ceq $architectureId })
    if ($architectureResults.Count -ne 1) { throw 'The Framework architecture assertion did not execute exactly once.' }
    $architectureOutput = $architectureResults[0].SelectSingleNode('t:Output/t:StdOut', $namespaces)
    if ($null -eq $architectureOutput) { throw 'The Framework architecture assertion produced no runtime evidence.' }
    $architecture = $architectureOutput.InnerText | ConvertFrom-Json
    $expectedPointerSize = if ($ExpectedPlatform -eq 'x86') { 4 } else { 8 }
    if ($architecture.ExpectedArchitecture -cne $ExpectedPlatform -or $architecture.PointerSize -ne $expectedPointerSize -or ([string] $architecture.ProcessArchitecture).ToLowerInvariant() -cne $ExpectedPlatform) {
        throw "Framework architecture evidence does not match the requested $ExpectedPlatform test host."
    }
    Write-Output "Verified $($counts.executed) passing tests and $ExpectedPlatform architecture assertion."
}

function Assert-SqlitePackageConsumer {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]] $Lines,
        [Parameter(Mandatory = $true)][string] $Mode,
        [Parameter(Mandatory = $true)][string] $ExpectedPlatform,
        [Parameter(Mandatory = $true)][string] $Executable
    )

    $prefix = 'STRANDS_SQLITE_CONSUMER_JSON:'
    $markers = @($Lines | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })
    if ($markers.Count -ne 1) { throw 'SQLite package consumer must return exactly one result marker.' }
    try { $record = $markers[0].Substring($prefix.Length) | ConvertFrom-Json }
    catch { throw [InvalidOperationException]::new('SQLite package consumer returned malformed JSON.', $_.Exception) }
    foreach ($name in @('Mode', 'PointerSize', 'Architecture', 'Runtime', 'SQLiteVersion', 'Fts5', 'Persistence', 'NamespacedMemory', 'OpaqueBytes', 'BorrowedStorage', 'HigherRankScore', 'LowerRankScore', 'NativePath', 'NativeSHA256')) {
        if ($null -eq $record -or $null -eq $record.PSObject.Properties[$name]) { throw "SQLite package consumer is missing evidence: $name" }
    }
    $pointer = if ($ExpectedPlatform -eq 'x86') { 4 } else { 8 }
    if ($record.Mode -cne $Mode -or $record.Architecture -cne $ExpectedPlatform.ToUpperInvariant() -or
        ($record.PointerSize -isnot [int] -and $record.PointerSize -isnot [long]) -or $record.PointerSize -ne $pointer) {
        throw 'SQLite package consumer architecture or mode evidence does not match the requested process.'
    }
    foreach ($name in @('Fts5', 'Persistence', 'NamespacedMemory', 'OpaqueBytes', 'BorrowedStorage')) {
        if ($record.$name -isnot [bool] -or -not $record.$name) { throw "SQLite package consumer did not prove $name." }
    }
    if ([string]::IsNullOrWhiteSpace([string] $record.Runtime) -or [string]::IsNullOrWhiteSpace([string] $record.SQLiteVersion)) {
        throw 'SQLite package consumer is missing native runtime/version evidence.'
    }
    foreach ($name in @('HigherRankScore', 'LowerRankScore')) {
        if ($record.$name -isnot [double] -and $record.$name -isnot [decimal] -and $record.$name -isnot [int] -and $record.$name -isnot [long]) {
            throw 'SQLite package consumer returned invalid ranking evidence.'
        }
    }
    if ($record.HigherRankScore -le $record.LowerRankScore -or $record.LowerRankScore -le 0 -or $record.HigherRankScore -gt 1) {
        throw 'SQLite package consumer returned invalid ranking evidence.'
    }
    $nativePath = [IO.Path]::GetFullPath([string] $record.NativePath)
    $output = [IO.Path]::GetFullPath((Split-Path -Parent $Executable)).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $nativePath.StartsWith($output, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $nativePath -PathType Leaf)) {
        throw 'SQLite package consumer native evidence is outside its output directory or missing.'
    }
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($hash.ComputeHash([IO.File]::ReadAllBytes($nativePath))).Replace('-', '') }
    finally { $hash.Dispose() }
    if ($record.NativeSHA256 -cne $digest) { throw 'SQLite package consumer native evidence hash does not match the loaded module.' }
}

function Get-Framework48RegistryViews {
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $baseKey = $null
        $key = $null
        try {
            $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
            $key = $baseKey.OpenSubKey('SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full')
            if ($null -eq $key) { throw "Framework Full registry key is missing in $view." }
            [pscustomobject]@{ View = $view.ToString(); Release = $key.GetValue('Release'); Version = $key.GetValue('Version') }
        } finally {
            if ($null -ne $key) { $key.Dispose() }
            if ($null -ne $baseKey) { $baseKey.Dispose() }
        }
    }
}

function Assert-Framework48RegistryViews {
    param([Parameter(Mandatory = $true)][object[]] $Views)
    if ($Views.Count -ne 2 -or @($Views | Where-Object { $_.View -ceq 'Registry64' }).Count -ne 1 -or @($Views | Where-Object { $_.View -ceq 'Registry32' }).Count -ne 1) {
        throw 'Exactly one Framework registry record from each Registry64 and Registry32 view is required.'
    }
    foreach ($entry in $Views) {
        [long] $release = 0
        if (-not [long]::TryParse([string] $entry.Release, [ref] $release) -or $release -lt 528040 -or $release -ge 533320) {
            throw "An isolated Framework 4.8 runtime is required in both registry views; $($entry.View) Release is '$($entry.Release)'."
        }
    }
}

$ErrorActionPreference = 'Stop'
if ((Test-Path -LiteralPath $ResultsDirectory) -and @(Get-ChildItem -LiteralPath $ResultsDirectory -Force).Count -ne 0) {
    throw 'Framework runtime evidence requires a new or empty results directory.'
}
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$frameworkViews = @(Get-Framework48RegistryViews)
Assert-Framework48RegistryViews -Views $frameworkViews
$runtime = [pscustomobject]@{
    FrameworkRelease = $frameworkViews[0].Release
    FrameworkVersion = $frameworkViews[0].Version
    FrameworkRegistryViews = $frameworkViews
    ClrVersion = [Environment]::Version.ToString()
    MscorlibVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo([object].Assembly.Location).FileVersion
    OperatingSystem = [Environment]::OSVersion.VersionString
    ProcessArchitecture = if ([Environment]::Is64BitProcess) { 'x64' } else { 'x86' }
    RequestedTestPlatform = $Platform
}
$runtime | ConvertTo-Json -Depth 4 | Tee-Object -FilePath (Join-Path $ResultsDirectory 'runtime.json')
foreach ($path in @($TestAssembly, $VSTestConsole, $ConsumerExecutable, $ExampleExecutable)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required executable/assembly is missing: $path" }
}
if ($SageMakerConsumerExecutable -and -not (Test-Path -LiteralPath $SageMakerConsumerExecutable -PathType Leaf)) { throw "The SageMaker package consumer is missing: $SageMakerConsumerExecutable" }
foreach ($path in @($BidiTestAssembly, $BidiConsumerExecutable)) {
    if ($path -and -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "A bidirectional verification artifact is missing: $path" }
}
if ($BedrockNovaConsumerExecutable -and -not (Test-Path -LiteralPath $BedrockNovaConsumerExecutable -PathType Leaf)) { throw 'A Nova verification artifact is missing.' }
foreach ($managed in @(@{ Name = 'Core'; Path = $CoreConsumerExecutable }, @{ Name = 'GoogleAuth'; Path = $GoogleAuthConsumerExecutable })) {
    if ($managed.Path -and -not (Test-Path -LiteralPath $managed.Path -PathType Leaf)) { throw "A $($managed.Name) managed package consumer is missing." }
}
foreach ($path in @($AudioTestAssembly, $AudioConsumerExecutable)) {
    if ($path -and -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "An Audio verification artifact is missing: $path" }
}
foreach ($path in @($OpenTelemetryTestAssembly, $OpenTelemetryConsumerExecutable)) {
    if ($path -and -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "An OpenTelemetry verification artifact is missing: $path" }
}
foreach ($path in @($S3TestAssembly, $S3ConsumerExecutable)) {
    if ($path -and -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "An S3 verification artifact is missing: $path" }
}
foreach ($path in @($SqliteTestAssembly, $SqliteConsumerExecutable)) {
    if ($path -and -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "A SQLite verification artifact is missing: $path" }
}
foreach ($path in @($BedrockKnowledgeBaseTestAssembly, $BedrockKnowledgeBaseConsumerExecutable)) {
    if ($path -and -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "A Knowledge Base verification artifact is missing: $path" }
}
if ($GoogleAuthTestAssembly -and -not (Test-Path -LiteralPath $GoogleAuthTestAssembly -PathType Leaf)) { throw "A Google Auth verification artifact is missing: $GoogleAuthTestAssembly" }
$nativeGroups = @(
    @{ Name = 'monty'; Assembly = $MontyTestAssembly; Validator = $MontyTestResultValidator; Consumer = $MontyConsumerExecutable },
    @{ Name = 'cedar'; Assembly = $CedarTestAssembly; Validator = $CedarTestResultValidator; Consumer = $CedarConsumerExecutable }
)
foreach ($group in $nativeGroups) {
    $provided = @(@($group.Assembly, $group.Validator, $group.Consumer) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($provided.Count -ne 0 -and $provided.Count -ne 3) { throw "Provide all three $($group.Name) assembly/validator/consumer inputs together." }
    foreach ($path in $provided) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "A $($group.Name) verification artifact is missing: $path" }
    }
}
if ($BedrockConsumerExecutable -and -not (Test-Path -LiteralPath $BedrockConsumerExecutable -PathType Leaf)) { throw "The Bedrock package consumer is missing: $BedrockConsumerExecutable" }
$stdioFixture = Join-Path (Split-Path -Parent $TestAssembly) 'McpStdioFixture\Strands.Mcp.StdioServer.exe'
if (-not (Test-Path -LiteralPath $stdioFixture -PathType Leaf)) { throw "The native Framework 4.8 stdio fixture is missing: $stdioFixture" }

$previousInterpreterPath = $env:PATH
$previousArchitecture = $env:STRANDS_TEST_PROCESS_ARCHITECTURE
$previousMontyArchitecture = $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE
$previousCedarArchitecture = $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE
$settingsPath = Join-Path $ResultsDirectory "framework48-$Platform.runsettings"
'<RunSettings><RunConfiguration><TreatNoTestsAsError>true</TreatNoTestsAsError></RunConfiguration></RunSettings>' | Set-Content -LiteralPath $settingsPath -Encoding UTF8
$env:STRANDS_TEST_PROCESS_ARCHITECTURE = $Platform
$env:STRANDS_MONTY_EXPECTED_ARCHITECTURE = $Platform.ToUpperInvariant()
$env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE = $Platform.ToUpperInvariant()
try {
    foreach ($name in @('STRANDS_BASH_TEST_EXECUTABLE', 'STRANDS_PYTHON_EXECUTABLE')) {
        $interpreter = [Environment]::GetEnvironmentVariable($name)
        if ($interpreter) {
            if (-not [IO.Path]::IsPathRooted($interpreter) -or -not (Test-Path -LiteralPath $interpreter -PathType Leaf)) { throw "Declared test interpreter is missing: $name" }
            $env:PATH = (Split-Path -Parent $interpreter) + [IO.Path]::PathSeparator + $env:PATH
        }
    }
    $suites = @(@{ Assembly = $TestAssembly; Name = 'framework48'; Log = 'tests.log' })
    if ($BidiTestAssembly) { $suites += @{ Assembly = $BidiTestAssembly; Name = 'bidi-framework48'; Log = 'bidi-tests.log' } }
    if ($AudioTestAssembly) { $suites += @{ Assembly = $AudioTestAssembly; Name = 'audio-framework48'; Log = 'audio-tests.log' } }
    if ($OpenTelemetryTestAssembly) { $suites += @{ Assembly = $OpenTelemetryTestAssembly; Name = 'opentelemetry-framework48'; Log = 'opentelemetry-tests.log' } }
    if ($S3TestAssembly) { $suites += @{ Assembly = $S3TestAssembly; Name = 's3-framework48'; Log = 's3-tests.log' } }
    if ($SqliteTestAssembly) { $suites += @{ Assembly = $SqliteTestAssembly; Name = 'sqlite-framework48'; Log = 'sqlite-tests.log' } }
    if ($BedrockKnowledgeBaseTestAssembly) { $suites += @{ Assembly = $BedrockKnowledgeBaseTestAssembly; Name = 'knowledge-base-framework48'; Log = 'knowledge-base-tests.log' } }
    if ($GoogleAuthTestAssembly) { $suites += @{ Assembly = $GoogleAuthTestAssembly; Name = 'google-auth-framework48'; Log = 'google-auth-tests.log' } }
    foreach ($group in $nativeGroups) {
        if ($group.Assembly) { $suites += @{ Assembly = $group.Assembly; Name = ($group.Name + '-framework48'); Log = ($group.Name + '-tests.log'); NativeValidator = $group.Validator } }
    }
    foreach ($suite in $suites) {
        $suiteDirectory = if ($suite.ContainsKey('NativeValidator')) { Join-Path $ResultsDirectory $suite.Name } else { $ResultsDirectory }
        New-Item -ItemType Directory -Path $suiteDirectory -Force | Out-Null
        $trxName = "$($suite.Name)-$Platform.trx"
        $trxPath = Join-Path $suiteDirectory $trxName
        $previousNativePreference = $ErrorActionPreference
        try {
            # Windows PowerShell wraps native stderr in error records. Capture all output before checking the exit code.
            $ErrorActionPreference = 'Continue'
            & $VSTestConsole $suite.Assembly "/Platform:$Platform" '/Framework:.NETFramework,Version=v4.8' "/Settings:$settingsPath" "/Logger:trx;LogFileName=$trxName" "/ResultsDirectory:$suiteDirectory" 2>&1 |
                Tee-Object -FilePath (Join-Path $ResultsDirectory $suite.Log) -ErrorAction Stop
            $testExitCode = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $previousNativePreference
        }
        if ($testExitCode -ne 0) { throw "Framework 4.8 $Platform $($suite.Name) tests failed with exit code $testExitCode." }
        if ($suite.ContainsKey('NativeValidator')) {
            & $suite.NativeValidator -ResultsDirectory $suiteDirectory -ExpectedArchitecture $Platform.ToUpperInvariant()
        } else {
            Assert-Framework48TestResults -TrxPath $trxPath -ExpectedPlatform $Platform
        }
    }
    foreach ($group in $nativeGroups) {
        if (-not $group.Consumer) { continue }
        $previousNativePreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $lines = @(& $group.Consumer $Platform.ToUpperInvariant() 2>&1)
            $consumerExit = $LASTEXITCODE
        } finally { $ErrorActionPreference = $previousNativePreference }
        $lines | Tee-Object -FilePath (Join-Path $ResultsDirectory ($group.Name + '-package-consumer.log'))
        $pointer = if ($Platform -eq 'x86') { 4 } else { 8 }
        $marker = if ($group.Name -eq 'monty') { "Monty:real-python,host-tool,print-only;Engine:0.0.23;ABI:1;ProcessArchitecture:$($Platform.ToUpperInvariant());HostPointerSize:$pointer;WorkerPointerBits:$($pointer * 8)" }
            else { "Cedar:allow,deny,schema-rejected;Architecture:$($Platform.ToUpperInvariant());PointerSize:$pointer" }
        if ($consumerExit -ne 0 -or $lines -cnotcontains $marker) { throw "Framework 4.8 $Platform $($group.Name) package consumer failed." }
    }
    foreach ($managed in @(
        @{ Name = 'Core'; Path = $CoreConsumerExecutable; Marker = 'PASS core-package-tool-cycle' },
        @{ Name = 'GoogleAuth'; Path = $GoogleAuthConsumerExecutable; Marker = 'PASS googleauth-package-provider-request' }
    )) {
        if (-not $managed.Path) { continue }
        $pointer = if ($Platform -eq 'x86') { 4 } else { 8 }
        $previousNativePreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $lines = @(& $managed.Path $pointer 2>&1)
            $consumerExit = $LASTEXITCODE
        } finally { $ErrorActionPreference = $previousNativePreference }
        $consumerLog = Join-Path $ResultsDirectory ($managed.Name.ToLowerInvariant() + '-package-consumer.log')
        [IO.File]::WriteAllText($consumerLog, '')
        $lines | Tee-Object -FilePath $consumerLog
        $marker = $managed.Marker + " pointer=$pointer"
        if ($consumerExit -ne 0 -or @($lines | Where-Object { $_ -ceq $marker }).Count -ne 1) { throw "The $($managed.Name) managed package consumer failed." }
    }
    if ($BedrockNovaConsumerExecutable) {
        $pointer = if ($Platform -eq 'x86') { 4 } else { 8 }
        $previousNativePreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $lines = @(& $BedrockNovaConsumerExecutable $pointer 2>&1)
            $consumerExit = $LASTEXITCODE
        } finally { $ErrorActionPreference = $previousNativePreference }
        $lines | Tee-Object -FilePath (Join-Path $ResultsDirectory 'nova-native-package-consumer.log')
        $marker = "Nova package: pointer=$pointer; asset=netstandard2.0; abi=2; network=false"
        if ($consumerExit -ne 0 -or @($lines | Where-Object { $_ -ceq $marker }).Count -ne 1 -or
            @($lines | Where-Object { $_ -ceq 'PASS framework48-nova-native-abi-smoke' }).Count -ne 1) { throw 'The Nova native package consumer failed.' }
    }
    if ($AudioConsumerExecutable) {
        $pointer = if ($Platform -eq 'x86') { 4 } else { 8 }
        $previousNativePreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $lines = @(& $AudioConsumerExecutable $pointer 2>&1)
            $consumerExit = $LASTEXITCODE
        } finally { $ErrorActionPreference = $previousNativePreference }
        $lines | Tee-Object -FilePath (Join-Path $ResultsDirectory 'audio-package-consumer.log')
        $marker = "Audio package: native=ok; borrowed=true; pointer=$pointer; asset=netstandard2.0; hardware=false"
        if ($consumerExit -ne 0 -or @($lines | Where-Object { $_ -ceq $marker }).Count -ne 1 -or
            @($lines | Where-Object { ([string] $_).StartsWith('Audio native version: PortAudio', [StringComparison]::Ordinal) }).Count -ne 1) {
            throw "Framework 4.8 $Platform Audio package consumer failed."
        }
    }
    if ($S3ConsumerExecutable) {
        $pointer = if ($Platform -eq 'x86') { 4 } else { 8 }
        $previousNativePreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $lines = @(& $S3ConsumerExecutable $pointer 2>&1)
            $consumerExit = $LASTEXITCODE
        } finally { $ErrorActionPreference = $previousNativePreference }
        $lines | Tee-Object -FilePath (Join-Path $ResultsDirectory 's3-package-consumer.log')
        if ($consumerExit -ne 0 -or $lines -cnotcontains "S3 package: fourOps=ok; namespace=ok; search=ok; borrowed=true; pointer=$pointer") { throw "Framework 4.8 $Platform S3 package consumer failed." }
    }
    if ($SqliteConsumerExecutable) {
        $pointer = if ($Platform -eq 'x86') { 4 } else { 8 }
        $storage = Join-Path $ResultsDirectory 'sqlite-storage'
        if (Test-Path -LiteralPath $storage) { throw 'SQLite package consumer storage must be new.' }
        foreach ($mode in @('write', 'read')) {
            $previousNativePreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $lines = @(& $SqliteConsumerExecutable $mode $storage $pointer 2>&1)
                $consumerExit = $LASTEXITCODE
            } finally { $ErrorActionPreference = $previousNativePreference }
            $lines | Tee-Object -FilePath (Join-Path $ResultsDirectory "sqlite-$mode-package-consumer.log")
            if ($consumerExit -ne 0) { throw "Framework 4.8 $Platform SQLite package $mode consumer failed." }
            Assert-SqlitePackageConsumer -Lines $lines -Mode $mode -ExpectedPlatform $Platform -Executable $SqliteConsumerExecutable
        }
    }
    if ($BedrockKnowledgeBaseConsumerExecutable) {
        $pointer = if ($Platform -eq 'x86') { 4 } else { 8 }
        $receipt = Join-Path $ResultsDirectory 'knowledge-base-package-receipt.json'
        $previousNativePreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $lines = @(& $BedrockKnowledgeBaseConsumerExecutable $pointer $Platform.ToUpperInvariant() $receipt 2>&1)
            $consumerExit = $LASTEXITCODE
        } finally { $ErrorActionPreference = $previousNativePreference }
        $consumerLog = Join-Path $ResultsDirectory 'knowledge-base-package-consumer.log'
        [IO.File]::WriteAllText($consumerLog, '')
        $lines | Tee-Object -FilePath $consumerLog
        $marker = "KB Consumer: retrieval=true; borrowed=true; requests=11; custom=true; s3=true; pointer=$pointer"
        if ($consumerExit -ne 0 -or @($lines | Where-Object { $_ -ceq $marker }).Count -ne 1) { throw "Framework 4.8 $Platform Knowledge Base package consumer failed." }
    }
    if ($BedrockConsumerExecutable) {
        $pointer = if ($Platform -eq 'x86') { 4 } else { 8 }
        $lines = @(& $BedrockConsumerExecutable $pointer 2>&1)
        $consumerExit = $LASTEXITCODE
        $lines | Tee-Object -FilePath (Join-Path $ResultsDirectory 'bedrock-package-consumer.log')
        if ($consumerExit -ne 0 -or $lines -cnotcontains "Bedrock package: agent=complete; tool=item:7; calls=2; borrowed=true; cancellation=ok; pointer=$pointer") { throw "Framework 4.8 $Platform Bedrock package consumer failed." }
    }
    foreach ($consumer in @(
        @{ Name='SageMaker'; Path=$SageMakerConsumerExecutable; Marker='SageMaker package: agent=ok; structured=7; calls=2; borrowed=true' },
        @{ Name='Bidi'; Path=$BidiConsumerExecutable; Marker='Bidi package: websocket=ok; tool=7; continuation=one; audio=ok; agent=7' },
        @{ Name='OpenTelemetry'; Path=$OpenTelemetryConsumerExecutable; Marker='OpenTelemetry package: spans=2; invocations=1; resource=ok; ownership=ok' }
    )) {
        if (-not $consumer.Path) { continue }
        $pointer = if ($Platform -eq 'x86') { 4 } else { 8 }
        $previousNativePreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $lines = @(& $consumer.Path $pointer 2>&1)
            $consumerExit = $LASTEXITCODE
        } finally { $ErrorActionPreference = $previousNativePreference }
        $log = Join-Path $ResultsDirectory ($consumer.Name.ToLowerInvariant() + '-package-consumer.log')
        [IO.File]::WriteAllText($log, '')
        $lines | Tee-Object -FilePath $log
        $marker = "$($consumer.Name) package architecture: pointer=$pointer; asset=netstandard2.0"
        if ($consumerExit -ne 0 -or $lines -cnotcontains $consumer.Marker -or
            @($lines | Where-Object { $_ -ceq $marker }).Count -ne 1) { throw "The $($consumer.Name) package consumer failed." }
    }
} finally {
    $env:PATH = $previousInterpreterPath
    $env:STRANDS_TEST_PROCESS_ARCHITECTURE = $previousArchitecture
    $env:STRANDS_MONTY_EXPECTED_ARCHITECTURE = $previousMontyArchitecture
    $env:STRANDS_CEDAR_EXPECTED_ARCHITECTURE = $previousCedarArchitecture
}
Write-Output "Isolated Framework 4.8 $Platform tests passed."

# The full Harness NuGet consumer is built explicitly for each requested architecture.
$pointer = if ($Platform -eq 'x86') { 4 } else { 8 }
$previousNativePreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    $lines = @(& $ConsumerExecutable $pointer 2>&1)
    $consumerExit = $LASTEXITCODE
} finally { $ErrorActionPreference = $previousNativePreference }
$lines | Tee-Object -FilePath (Join-Path $ResultsDirectory 'package-consumer.log')
$marker = "Harness package: agent=complete; tool=5; turns=2; pointer=$pointer; asset=netstandard2.0; framework=4.8"
if ($consumerExit -ne 0 -or @($lines | Where-Object { $_ -ceq $marker }).Count -ne 1 -or
    $lines -cnotcontains '2 + 3 = 5.' -or $lines -cnotcontains 'Stop: end_turn; model turns: 2') {
    throw "Framework 4.8 $Platform full Harness package consumer failed."
}
Write-Output "Isolated Framework 4.8 $Platform full Harness packaged consumer passed."

# The project example retains its x64 execution profile.
if ($Platform -ne 'x64') { return }

foreach ($sample in @(@{ Name = 'project-example'; Path = $ExampleExecutable })) {
    $previousNativePreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $lines = @(& $sample.Path --demo 2>&1)
        $exitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousNativePreference }
    $lines | Tee-Object -FilePath (Join-Path $ResultsDirectory ($sample.Name + '.log'))
    if ($exitCode -ne 0 -or $lines -notcontains '2 + 3 = 5.' -or $lines -notcontains 'Stop: end_turn; model turns: 2') {
        throw "Framework 4.8 $($sample.Name) did not complete the expected model/tool cycle."
    }
}
Write-Output 'Isolated Framework 4.8 packaged consumer and project example passed.'
