param(
    [int]$Runs = $(if ($env:STARK_BENCH_RUNS) { [int]$env:STARK_BENCH_RUNS } else { 20 }),
    [string]$Filter = $env:STARK_BENCH_FILTER,
    [string]$Target = $env:STARK_TARGET,
    [string]$ExtraCompilerArgs = $env:STARK_COMPILER_ARGS,
    [string]$Languages = $(if ($env:STARK_BENCH_LANGUAGES) { $env:STARK_BENCH_LANGUAGES } else { "stark,c,rust" }),
    [string]$CaptureRss = $(if ($env:STARK_BENCH_CAPTURE_RSS) { $env:STARK_BENCH_CAPTURE_RSS } else { "0" }),
    [string]$CCompiler = $(if ($env:STARK_BENCH_C_COMPILER) { $env:STARK_BENCH_C_COMPILER } else { "clang" }),
    [string]$RustCompiler = $(if ($env:STARK_BENCH_RUST_COMPILER) { $env:STARK_BENCH_RUST_COMPILER } else { "rustc" }),
    [string]$OutputDir = $env:STARK_BENCH_OUTPUT_DIR,
    [string]$ResultsFile = $env:STARK_BENCH_RESULTS_FILE,
    [string]$MachineFile = $env:STARK_BENCH_MACHINE_FILE
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..")).Path
$benchRoot = Join-Path $repoRoot "benchmarks"
$stdlibRoot = Join-Path $repoRoot "stdlib\src"
$timestamp = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ")
$cFlags = @("-O3", "-DNDEBUG", "-std=c17")
$rustFlags = @("-C", "opt-level=3", "-C", "debug-assertions=no", "-C", "overflow-checks=no")

if ($Runs -lt 1) {
    throw "STARK_BENCH_RUNS must be a positive integer."
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $benchRoot "results"
}

function Ensure-Directory {
    param([string]$Path)

    if (![string]::IsNullOrWhiteSpace($Path) -and !(Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
}

function Ensure-TrailingSeparator {
    param([string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (!$fullPath.EndsWith([IO.Path]::DirectorySeparatorChar.ToString()) -and
        !$fullPath.EndsWith([IO.Path]::AltDirectorySeparatorChar.ToString())) {
        return $fullPath + [IO.Path]::DirectorySeparatorChar
    }

    return $fullPath
}

function Get-RelativePath {
    param(
        [string]$BasePath,
        [string]$Path
    )

    $baseUri = New-Object Uri (Ensure-TrailingSeparator $BasePath)
    $pathUri = New-Object Uri ([IO.Path]::GetFullPath($Path))
    $relative = $baseUri.MakeRelativeUri($pathUri).ToString()
    return [Uri]::UnescapeDataString($relative).Replace("/", [IO.Path]::DirectorySeparatorChar)
}

function ConvertTo-DisplayPath {
    param([string]$Path)

    return $Path -replace '\\', '/'
}

function ConvertTo-SafeName {
    param([string]$Name)

    return ($Name -replace '[\\/]', '_') -replace '\.stark$', ''
}

function Get-BenchmarkVariantDescriptor {
    param([string]$BenchmarkId)

    $parts = $BenchmarkId -split "/"
    if ($parts.Length -lt 3 -or $parts[0] -ne "benchmarks") {
        return $null
    }

    $category = $parts[1]
    $stem = [IO.Path]::GetFileName($BenchmarkId)
    if ([string]::IsNullOrWhiteSpace($stem)) {
        return $null
    }

    $prefix = ""
    if ($stem.StartsWith("Experimental", [StringComparison]::Ordinal)) {
        $prefix = "Experimental"
        $stem = $stem.Substring("Experimental".Length)
    }
    elseif ($stem.StartsWith("Dynamic", [StringComparison]::Ordinal)) {
        $prefix = "Dynamic"
        $stem = $stem.Substring("Dynamic".Length)
    }

    if ($category -ne "collections") {
        $subsystem = switch ($category) {
            "allocator" { "Memory" }
            "console" { "Console" }
            "io" { "IO" }
            "network" { "Network" }
            "runtime" { "Runtime" }
            "text" { "Text" }
            default { "" }
        }

        if ([string]::IsNullOrWhiteSpace($subsystem)) {
            return $null
        }

        return [PSCustomObject]@{
            Prefix = $prefix
            Collection = $subsystem
            Scenario = $stem
            BenchmarkGroup = "benchmarks/$category/$stem"
        }
    }

    $collection = ""
    $scenario = $stem
    if ($stem.StartsWith("LinkedList", [StringComparison]::Ordinal)) {
        $collection = "LinkedList"
        $scenario = $stem.Substring("LinkedList".Length)
    }
    elseif ($stem.StartsWith("RingQueue", [StringComparison]::Ordinal)) {
        $collection = "Queue"
        $scenario = $stem.Substring("RingQueue".Length)
    }
    elseif ($stem.StartsWith("Dictionary", [StringComparison]::Ordinal)) {
        $collection = "Dictionary"
        $scenario = $stem.Substring("Dictionary".Length)
    }
    elseif ($stem.StartsWith("Queue", [StringComparison]::Ordinal)) {
        $collection = "Queue"
        $scenario = $stem.Substring("Queue".Length)
    }
    elseif ($stem.StartsWith("Stack", [StringComparison]::Ordinal)) {
        $collection = "Stack"
        $scenario = $stem.Substring("Stack".Length)
    }
    elseif ($stem.StartsWith("List", [StringComparison]::Ordinal)) {
        $collection = "List"
        $scenario = $stem.Substring("List".Length)
    }

    if ([string]::IsNullOrWhiteSpace($collection)) {
        return $null
    }

    if ([string]::IsNullOrWhiteSpace($scenario)) {
        $scenario = "Default"
    }

    $canonicalStem = "$collection$scenario"
    return [PSCustomObject]@{
        Prefix = $prefix
        Collection = $collection
        Scenario = $scenario
        BenchmarkGroup = "benchmarks/collections/$canonicalStem"
    }
}

function Get-BenchmarkLabel {
    param(
        [string]$BenchmarkId,
        [string]$Language
    )

    $descriptor = Get-BenchmarkVariantDescriptor $BenchmarkId
    if ($null -eq $descriptor) {
        return [PSCustomObject]@{
            BenchmarkGroup = $BenchmarkId
            Implementation = $Language
            Collection = ""
            Scenario = [IO.Path]::GetFileName($BenchmarkId)
        }
    }

    $implementation = $Language
    if ($Language -eq "stark") {
        if ($descriptor.Prefix -eq "Experimental") {
            if ([IO.Path]::GetFileName($BenchmarkId).StartsWith("ExperimentalRingQueue", [StringComparison]::Ordinal)) {
                $implementation = "experimental-ring-stark"
            }
            else {
                $implementation = "experimental-stark"
            }
        }
        elseif ($descriptor.Prefix -eq "Dynamic") {
            $implementation = "dynamic-stark"
        }
        else {
            $implementation = "stable-stark"
        }
    }

    return [PSCustomObject]@{
        BenchmarkGroup = $descriptor.BenchmarkGroup
        Implementation = $implementation
        Collection = $descriptor.Collection
        Scenario = $descriptor.Scenario
    }
}

function Get-BenchmarkSourcePath {
    param(
        [string]$BenchmarkId,
        [string]$Extension
    )

    $relativePath = ($BenchmarkId -replace '/', [IO.Path]::DirectorySeparatorChar) + $Extension
    return Join-Path $repoRoot $relativePath
}

function Split-ArgumentString {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return @()
    }

    $arguments = New-Object System.Collections.Generic.List[string]
    $current = New-Object System.Text.StringBuilder
    $inSingleQuote = $false
    $inDoubleQuote = $false
    $escaping = $false

    foreach ($character in $Text.ToCharArray()) {
        if ($escaping) {
            [void]$current.Append($character)
            $escaping = $false
            continue
        }

        if ($character -eq '`') {
            $escaping = $true
            continue
        }

        if ($character -eq "'" -and !$inDoubleQuote) {
            $inSingleQuote = !$inSingleQuote
            continue
        }

        if ($character -eq '"' -and !$inSingleQuote) {
            $inDoubleQuote = !$inDoubleQuote
            continue
        }

        if ([char]::IsWhiteSpace($character) -and !$inSingleQuote -and !$inDoubleQuote) {
            if ($current.Length -gt 0) {
                $arguments.Add($current.ToString())
                [void]$current.Clear()
            }

            continue
        }

        [void]$current.Append($character)
    }

    if ($escaping) {
        [void]$current.Append('`')
    }

    if ($inSingleQuote -or $inDoubleQuote) {
        throw "Unterminated quote in STARK_COMPILER_ARGS."
    }

    if ($current.Length -gt 0) {
        $arguments.Add($current.ToString())
    }

    return $arguments.ToArray()
}

function Test-LanguageEnabled {
    param([string]$Language)

    return $script:selectedLanguages -contains $Language
}

function ConvertTo-BenchmarkBoolean {
    param(
        [string]$Value,
        [string]$Name
    )

    if ($Value -eq "0") {
        return $false
    }

    if ($Value -eq "1") {
        return $true
    }

    throw "$Name must be 0 or 1."
}

function Test-WindowsHost {
    $isWindowsVariable = Get-Variable -Name IsWindows -ErrorAction SilentlyContinue
    if ($null -ne $isWindowsVariable) {
        return [bool]$isWindowsVariable.Value
    }

    return [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
}

function Test-BenchmarkDirective {
    param(
        [string]$Path,
        [string]$Directive
    )

    if (!(Test-Path -LiteralPath $Path)) {
        return $false
    }

    $escapedDirective = [regex]::Escape($Directive)
    return [bool](Select-String -LiteralPath $Path -Pattern "^\s*//\s*stark-bench:\s*$escapedDirective(?:\s|$)" -Quiet)
}

function Assert-CommandExists {
    param(
        [string]$Name,
        [string]$Label
    )

    if (!(Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Label '$Name' was not found."
    }
}

function Get-FirstCommandLine {
    param(
        [string]$Name,
        [string[]]$Arguments
    )

    if (!(Get-Command $Name -ErrorAction SilentlyContinue)) {
        return "not-found"
    }

    try {
        $lines = & $Name @Arguments 2>$null
        if ($lines -is [array] -and $lines.Length -gt 0) {
            return [string]$lines[0]
        }

        if ($null -ne $lines) {
            return [string]$lines
        }
    }
    catch {
    }

    return "unknown"
}

function Invoke-Native {
    param(
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [switch]$SuppressOutput
    )

    if ($SuppressOutput) {
        & $FilePath @Arguments *> $null
    }
    else {
        & $FilePath @Arguments
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
    }
}

function Invoke-BenchmarkExecutable {
    param([string]$FilePath)

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo

    if (!$process.Start()) {
        throw "Unable to start benchmark executable: $FilePath"
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdoutTask.Wait()
    $stderrTask.Wait()

    $exitCode = $process.ExitCode
    $peakRssKiB = [long][Math]::Ceiling([double]$process.PeakWorkingSet64 / 1024.0)
    $process.Dispose()

    if ($exitCode -ne 0) {
        $stderr = $stderrTask.Result
        if (![string]::IsNullOrWhiteSpace($stderr)) {
            throw "Benchmark exited with status $exitCode`: $FilePath`n$stderr"
        }

        throw "Benchmark exited with status $exitCode`: $FilePath"
    }

    return $peakRssKiB
}

function Convert-NanosecondsToMicroseconds {
    param([long]$Nanoseconds)

    return [long](($Nanoseconds + 500) / 1000)
}

function Get-ElapsedMicroseconds {
    param([System.Diagnostics.Stopwatch]$Stopwatch)

    $nanoseconds = [long](($Stopwatch.ElapsedTicks * 1000000000.0) / [System.Diagnostics.Stopwatch]::Frequency)
    return Convert-NanosecondsToMicroseconds $nanoseconds
}

function Emit-Row {
    param([string]$Row)

    Write-Output $Row
    Add-Content -Path $script:ResultsFile -Value $Row
}

function Add-CRelativeAverageRatios {
    param([string]$Path)

    $lines = @(Get-Content -LiteralPath $Path)
    if ($lines.Count -eq 0) {
        return
    }

    $header = @($lines[0].Split(","))
    $benchmarkIndex = [Array]::IndexOf($header, "benchmark")
    $benchmarkGroupIndex = [Array]::IndexOf($header, "benchmark_group")
    $languageIndex = [Array]::IndexOf($header, "language")
    $avgIndex = [Array]::IndexOf($header, "avg_us")
    if ($benchmarkIndex -lt 0 -or $languageIndex -lt 0 -or $avgIndex -lt 0) {
        throw "Benchmark CSV must contain benchmark, language, and avg_us columns."
    }

    $ratioIndex = [Array]::IndexOf($header, "c_avg_ratio")
    $keptIndexes = New-Object System.Collections.Generic.List[int]
    for ($index = 0; $index -lt $header.Count; $index++) {
        if ($index -ne $ratioIndex) {
            $keptIndexes.Add($index)
        }
    }

    $cAverages = @{}
    $cGroupAverages = @{}
    $rows = New-Object System.Collections.Generic.List[object[]]
    for ($lineIndex = 1; $lineIndex -lt $lines.Count; $lineIndex++) {
        if ([string]::IsNullOrWhiteSpace($lines[$lineIndex])) {
            continue
        }

        $fields = @($lines[$lineIndex].Split(","))
        $rows.Add($fields)

        if ($fields.Count -le [Math]::Max($languageIndex, $avgIndex)) {
            continue
        }

        [double]$avg = 0
        if ($fields[$languageIndex] -eq "c" -and
            [double]::TryParse($fields[$avgIndex], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$avg) -and
            $avg -gt 0) {
            $cAverages[$fields[$benchmarkIndex]] = $avg
            if ($benchmarkGroupIndex -ge 0 -and $fields.Count -gt $benchmarkGroupIndex) {
                $group = $fields[$benchmarkGroupIndex]
                if (![string]::IsNullOrWhiteSpace($group) -and
                    ($fields[$benchmarkIndex] -eq $group -or !$cGroupAverages.ContainsKey($group))) {
                    $cGroupAverages[$group] = $avg
                }
            }
        }
    }

    $output = New-Object System.Collections.Generic.List[string]
    $output.Add((($keptIndexes | ForEach-Object { $header[$_] }) + @("c_avg_ratio")) -join ",")

    foreach ($fields in $rows) {
        $keptFields = $keptIndexes | ForEach-Object {
            if ($_ -lt $fields.Count) {
                $fields[$_]
            }
            else {
                ""
            }
        }

        $ratio = ""
        [double]$rowAvg = 0
        [double]$baselineAvg = 0
        if ($fields.Count -gt $benchmarkIndex -and $cAverages.ContainsKey($fields[$benchmarkIndex])) {
            $baselineAvg = [double]$cAverages[$fields[$benchmarkIndex]]
        }
        elseif ($benchmarkGroupIndex -ge 0 -and $fields.Count -gt $benchmarkGroupIndex -and $cGroupAverages.ContainsKey($fields[$benchmarkGroupIndex])) {
            $baselineAvg = [double]$cGroupAverages[$fields[$benchmarkGroupIndex]]
        }

        if ($fields.Count -gt [Math]::Max($benchmarkIndex, $avgIndex) -and
            $baselineAvg -gt 0 -and
            [double]::TryParse($fields[$avgIndex], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$rowAvg)) {
            $ratio = ($rowAvg / $baselineAvg).ToString("0.000000", [Globalization.CultureInfo]::InvariantCulture)
        }

        $output.Add(($keptFields + @($ratio)) -join ",")
    }

    Set-Content -LiteralPath $Path -Value $output
}

function Write-Status {
    param([string]$Message)

    [Console]::Error.WriteLine($Message)
}

function Get-GitValue {
    param([string[]]$Arguments)

    if (!(Get-Command git -ErrorAction SilentlyContinue)) {
        return "unknown"
    }

    try {
        $value = & git -C $repoRoot @Arguments 2>$null
        if ($LASTEXITCODE -eq 0 -and $null -ne $value) {
            if ($value -is [array]) {
                return ($value -join "`n")
            }

            return [string]$value
        }
    }
    catch {
    }

    return "unknown"
}

function Get-GitDirtyEntryCount {
    if (!(Get-Command git -ErrorAction SilentlyContinue)) {
        return "unknown"
    }

    try {
        $status = & git -C $repoRoot status --short 2>$null
        if ($LASTEXITCODE -ne 0 -or $null -eq $status) {
            return "unknown"
        }

        if ($status -is [array]) {
            return $status.Length.ToString()
        }

        if ([string]::IsNullOrWhiteSpace([string]$status)) {
            return "0"
        }

        return "1"
    }
    catch {
        return "unknown"
    }
}

function Get-WindowsCpuModel {
    try {
        $cpu = Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1
        if ($null -ne $cpu -and ![string]::IsNullOrWhiteSpace($cpu.Name)) {
            return $cpu.Name.Trim()
        }
    }
    catch {
    }

    return "unknown"
}

function Get-WindowsMemory {
    try {
        $system = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop | Select-Object -First 1
        if ($null -ne $system -and $null -ne $system.TotalPhysicalMemory) {
            return "$([math]::Round([double]$system.TotalPhysicalMemory / 1GB, 2)) GiB"
        }
    }
    catch {
    }

    return "unknown"
}

function Write-MachineMetadata {
    param([string]$Path)

    $metadata = @(
        "timestamp_utc=$timestamp",
        "results_file=$ResultsFile",
        "machine_file=$Path",
        "repository_root=$repoRoot",
        "git_commit=$(Get-GitValue @('rev-parse', 'HEAD'))",
        "git_dirty_entries=$(Get-GitDirtyEntryCount)",
        "os=$([Environment]::OSVersion.VersionString)",
        "powershell=$($PSVersionTable.PSVersion.ToString())",
        "cpu_model=$(Get-WindowsCpuModel)",
        "cpu_count=$([Environment]::ProcessorCount)",
        "memory=$(Get-WindowsMemory)",
        "dotnet=$(Get-FirstCommandLine 'dotnet' @('--version'))",
        "clang=$(Get-FirstCommandLine 'clang' @('--version'))",
        "cc=$(Get-FirstCommandLine 'cc' @('--version'))",
        "rustc=$(Get-FirstCommandLine 'rustc' @('--version'))",
        "stark_runs=$Runs",
        "timing_unit=microseconds",
        "stark_filter=$(if ([string]::IsNullOrWhiteSpace($Filter)) { '<none>' } else { $Filter })",
        "benchmark_languages=$Languages",
        "benchmark_capture_rss=$(if ($script:captureRss) { '1' } else { '0' })",
        "benchmark_peak_rss_unit=KiB",
        "benchmark_peak_rss_source=Process.PeakWorkingSet64 captured after each benchmark process exits when STARK_BENCH_CAPTURE_RSS=1; 0 when disabled",
        "benchmark_label_columns=benchmark_group,implementation,collection,scenario",
        "benchmark_ratio_column=c_avg_ratio avg_us divided by same-benchmark C avg_us, falling back to benchmark_group C avg_us",
        "stark_target=$(if ([string]::IsNullOrWhiteSpace($Target)) { 'host-default' } else { $Target })",
        "stark_flags=--emit-exe -O3",
        "stark_compiler_args=$(if ([string]::IsNullOrWhiteSpace($ExtraCompilerArgs)) { '<none>' } else { $ExtraCompilerArgs })",
        "c_compiler=$CCompiler",
        "c_flags=$($cFlags -join ' ')",
        "rust_compiler=$RustCompiler",
        "rust_flags=$($rustFlags -join ' ')",
        "fairness_rules=benchmarks/Fairness.md"
    )

    Set-Content -Path $Path -Value $metadata
}

function Time-Executable {
    param(
        [string]$BenchmarkId,
        [string]$Language,
        [long]$CompileMicroseconds,
        [string]$OutputPath
    )

    [long]$peakRssKiB = 0
    if ($script:captureRss) {
        $peakRssKiB = Invoke-BenchmarkExecutable $OutputPath
    }
    else {
        Invoke-Native $OutputPath @() -SuppressOutput
    }

    [long]$totalMicroseconds = 0
    [long]$minMicroseconds = 0
    [long]$maxMicroseconds = 0

    for ($run = 1; $run -le $Runs; $run++) {
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        [long]$runPeakRssKiB = 0
        if ($script:captureRss) {
            $runPeakRssKiB = Invoke-BenchmarkExecutable $OutputPath
        }
        else {
            Invoke-Native $OutputPath @() -SuppressOutput
        }
        $stopwatch.Stop()
        $elapsedMicroseconds = Get-ElapsedMicroseconds $stopwatch
        $totalMicroseconds += $elapsedMicroseconds

        if ($runPeakRssKiB -gt $peakRssKiB) {
            $peakRssKiB = $runPeakRssKiB
        }

        if ($minMicroseconds -eq 0 -or $elapsedMicroseconds -lt $minMicroseconds) {
            $minMicroseconds = $elapsedMicroseconds
        }

        if ($elapsedMicroseconds -gt $maxMicroseconds) {
            $maxMicroseconds = $elapsedMicroseconds
        }
    }

    $avgMicroseconds = [long]($totalMicroseconds / $Runs)
    $label = Get-BenchmarkLabel $BenchmarkId $Language
    Emit-Row "$BenchmarkId,$($label.BenchmarkGroup),$($label.Implementation),$($label.Collection),$($label.Scenario),$Language,$Runs,$CompileMicroseconds,$minMicroseconds,$avgMicroseconds,$maxMicroseconds,$peakRssKiB"
}

function Compile-AndTimeStark {
    param(
        [string]$SourcePath,
        [string]$BenchmarkId,
        [string]$OutputPath
    )

    $arguments = @(
        "run",
        "--project",
        (Join-Path $repoRoot "src"),
        "--",
        $SourcePath,
        "--emit-exe",
        "-O3",
        "-I",
        $stdlibRoot,
        "-o",
        $OutputPath
    )
    $arguments += $script:compilerArgs

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-Native "dotnet" $arguments -SuppressOutput
    $stopwatch.Stop()

    Time-Executable $BenchmarkId "stark" (Get-ElapsedMicroseconds $stopwatch) $OutputPath
}

function Compile-AndTimeC {
    param(
        [string]$SourcePath,
        [string]$BenchmarkId,
        [string]$OutputPath
    )

    $arguments = @($SourcePath) + $cFlags + @("-o", $OutputPath)
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-Native $CCompiler $arguments -SuppressOutput
    $stopwatch.Stop()

    Time-Executable $BenchmarkId "c" (Get-ElapsedMicroseconds $stopwatch) $OutputPath
}

function ConvertTo-RustCrateName {
    param([string]$SourcePath)

    $crateName = [IO.Path]::GetFileNameWithoutExtension($SourcePath) -replace '[^A-Za-z0-9_]', '_'
    if ($crateName -match '^[0-9]') {
        return "bench_$crateName"
    }

    return $crateName
}

function Compile-AndTimeRust {
    param(
        [string]$SourcePath,
        [string]$BenchmarkId,
        [string]$OutputPath,
        [string]$Language = "rust"
    )

    $arguments = @("--crate-name", (ConvertTo-RustCrateName $SourcePath), $SourcePath) + $rustFlags + @("-o", $OutputPath)
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-Native $RustCompiler $arguments -SuppressOutput
    $stopwatch.Stop()

    Time-Executable $BenchmarkId $Language (Get-ElapsedMicroseconds $stopwatch) $OutputPath
}

$script:selectedLanguages = @()
$script:captureRss = ConvertTo-BenchmarkBoolean $CaptureRss "STARK_BENCH_CAPTURE_RSS"
foreach ($language in $Languages.Split(",")) {
    $normalized = $language.Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        continue
    }

    if ($normalized -ne "stark" -and $normalized -ne "c" -and $normalized -ne "rust") {
        throw "Unsupported STARK_BENCH_LANGUAGES entry '$language'. Expected stark, c, and/or rust."
    }

    $script:selectedLanguages += $normalized
}

if ($script:selectedLanguages.Length -eq 0) {
    throw "At least one benchmark language must be selected."
}

if (Test-LanguageEnabled "c") {
    Assert-CommandExists $CCompiler "C benchmark compiler"
}

if (Test-LanguageEnabled "rust") {
    Assert-CommandExists $RustCompiler "Rust benchmark compiler"
}

Ensure-Directory $OutputDir
if ([string]::IsNullOrWhiteSpace($ResultsFile)) {
    $ResultsFile = Join-Path $OutputDir "results-$timestamp-$([Guid]::NewGuid().ToString('N').Substring(0, 6)).csv"
}

if ([string]::IsNullOrWhiteSpace($MachineFile)) {
    $MachineFile = Join-Path $OutputDir "machine-$timestamp-$([Guid]::NewGuid().ToString('N').Substring(0, 6)).txt"
}

Ensure-Directory (Split-Path -Parent $ResultsFile)
Ensure-Directory (Split-Path -Parent $MachineFile)
[IO.File]::WriteAllText($ResultsFile, "")

$tmpDir = Join-Path ([IO.Path]::GetTempPath()) "stark-bench-$([Guid]::NewGuid().ToString('N'))"
Ensure-Directory $tmpDir

$script:compilerArgs = @()
if (![string]::IsNullOrWhiteSpace($Target)) {
    $script:compilerArgs += @("--target", $Target)
}

$script:compilerArgs += Split-ArgumentString $ExtraCompilerArgs

try {
    Write-MachineMetadata $MachineFile
    Write-Status "Benchmark results: $ResultsFile"
    Write-Status "Machine metadata: $MachineFile"

    $benchmarks = Get-ChildItem -Path $benchRoot -Recurse -Filter "*.stark" -File |
        Sort-Object FullName

    if (![string]::IsNullOrWhiteSpace($Filter)) {
        $benchmarks = $benchmarks | Where-Object { $_.FullName -like "*$Filter*" }
    }

    if ($null -eq $benchmarks -or @($benchmarks).Count -eq 0) {
        throw "No benchmark sources matched."
    }

    Emit-Row "benchmark,benchmark_group,implementation,collection,scenario,language,runs,compile_us,min_us,avg_us,max_us,peak_rss_kib"

    $timedNativeBenchmarks = @{}
    foreach ($benchmark in @($benchmarks)) {
        $sourcePath = $benchmark.FullName
        $relativePath = ConvertTo-DisplayPath (Get-RelativePath $repoRoot $sourcePath)

        if (Test-BenchmarkDirective $sourcePath "compile-only") {
            Write-Status "Skipping compile-only benchmark $relativePath; compiler tests still validate it lowers successfully."
            continue
        }

        $safeName = ConvertTo-SafeName $relativePath
        $benchmarkId = $relativePath -replace '\.stark$', ''
        $benchmarkGroup = (Get-BenchmarkLabel $benchmarkId "stark").BenchmarkGroup
        $nativeSafeName = ConvertTo-SafeName $benchmarkGroup

        $starkOutputPath = Join-Path $tmpDir "$safeName-stark.exe"
        $cOutputPath = Join-Path $tmpDir "$nativeSafeName-c.exe"
        $rustOutputPath = Join-Path $tmpDir "$nativeSafeName-rust.exe"

        if (Test-LanguageEnabled "stark") {
            Compile-AndTimeStark $sourcePath $benchmarkId $starkOutputPath
        }

        $cBenchmarkKey = "c|$benchmarkGroup"
        if ((Test-LanguageEnabled "c") -and !$timedNativeBenchmarks.ContainsKey($cBenchmarkKey)) {
            $timedNativeBenchmarks[$cBenchmarkKey] = $true
            $cSourcePath = Get-BenchmarkSourcePath $benchmarkGroup ".c"
            if (!(Test-Path -LiteralPath $cSourcePath)) {
                throw "Missing C benchmark counterpart for group $benchmarkGroup`: $(ConvertTo-DisplayPath (Get-RelativePath $repoRoot $cSourcePath))"
            }

            if ((Test-WindowsHost) -and (Test-BenchmarkDirective $cSourcePath "skip-c-windows")) {
                $cRelativePath = ConvertTo-DisplayPath (Get-RelativePath $repoRoot $cSourcePath)
                Write-Status "Skipping C benchmark group $benchmarkGroup on Windows; $cRelativePath is marked // stark-bench: skip-c-windows."
            }
            else {
                Compile-AndTimeC $cSourcePath $benchmarkGroup $cOutputPath
            }
        }

        $rustBenchmarkKey = "rust|$benchmarkGroup"
        if ((Test-LanguageEnabled "rust") -and !$timedNativeBenchmarks.ContainsKey($rustBenchmarkKey)) {
            $timedNativeBenchmarks[$rustBenchmarkKey] = $true
            $rustSourcePath = Get-BenchmarkSourcePath $benchmarkGroup ".rs"
            if (!(Test-Path -LiteralPath $rustSourcePath)) {
                throw "Missing Rust benchmark counterpart for group $benchmarkGroup`: $(ConvertTo-DisplayPath (Get-RelativePath $repoRoot $rustSourcePath))"
            }

            Compile-AndTimeRust $rustSourcePath $benchmarkGroup $rustOutputPath

            $rustSourceStem = $rustSourcePath.Substring(0, $rustSourcePath.Length - ".rs".Length)
            $rustVariantPaths = Get-ChildItem -Path "$rustSourceStem.rust-*.rs" -File -ErrorAction SilentlyContinue |
                Sort-Object FullName
            foreach ($rustVariantPath in @($rustVariantPaths)) {
                $rustVariantName = [IO.Path]::GetFileName($rustVariantPath.FullName)
                $rustVariantName = $rustVariantName.Substring(([IO.Path]::GetFileName($rustSourceStem) + ".rust-").Length)
                $rustVariantName = $rustVariantName.Substring(0, $rustVariantName.Length - ".rs".Length)
                if ($rustVariantName.Contains(",")) {
                    throw "Rust benchmark variant names must not contain commas: $(ConvertTo-DisplayPath (Get-RelativePath $repoRoot $rustVariantPath.FullName))"
                }

                $rustVariantSafeName = $rustVariantName -replace '[^A-Za-z0-9_]', '_'
                $rustVariantOutputPath = Join-Path $tmpDir "$nativeSafeName-rust-$rustVariantSafeName.exe"
                Compile-AndTimeRust $rustVariantPath.FullName $benchmarkGroup $rustVariantOutputPath "rust-$rustVariantName"
            }
        }
    }

    Add-CRelativeAverageRatios $ResultsFile
    Write-Status "Added c_avg_ratio column using same-benchmark or same-group C avg_us baselines."
}
finally {
    if (Test-Path -LiteralPath $tmpDir) {
        Remove-Item -LiteralPath $tmpDir -Recurse -Force
    }
}
