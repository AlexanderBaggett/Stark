param(
    [int]$Runs = $(if ($env:STARK_BENCH_RUNS) { [int]$env:STARK_BENCH_RUNS } else { 100 }),
    [string]$Filter = $env:STARK_BENCH_FILTER,
    [string]$Subset = $env:STARK_BENCH_SUBSET,
    [string]$Target = $env:STARK_TARGET,
    [string]$ExtraCompilerArgs = $env:STARK_COMPILER_ARGS,
    [string]$Languages = $(if ($env:STARK_BENCH_LANGUAGES) { $env:STARK_BENCH_LANGUAGES } else { "stark,c,rust" }),
    [string]$CaptureRss = $(if ($env:STARK_BENCH_CAPTURE_RSS) { $env:STARK_BENCH_CAPTURE_RSS } else { "0" }),
    [string]$RuntimeOnly = $(if ($env:STARK_BENCH_RUNTIME_ONLY) { $env:STARK_BENCH_RUNTIME_ONLY } else { "0" }),
    [string]$KeepBinaries = $(if ($env:STARK_BENCH_KEEP_BINARIES) { $env:STARK_BENCH_KEEP_BINARIES } else { "0" }),
    [string]$BinaryDir = $env:STARK_BENCH_BINARY_DIR,
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
            BenchmarkGroup = "benchmarks/$category/$stem"
        }
    }

    $collection = ""
    $scenario = $stem
    if ($stem.StartsWith("LinkedList", [StringComparison]::Ordinal)) {
        $collection = "LinkedList"
        $scenario = $stem.Substring("LinkedList".Length)
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
            Language = $Language
        }
    }

    return [PSCustomObject]@{
        BenchmarkGroup = $descriptor.BenchmarkGroup
        Language = $Language
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

function Get-BenchmarkSubsetFilters {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return @()
    }

    $normalized = $Name.Trim().ToLowerInvariant()
    switch ($normalized) {
        "allocator" { return @("benchmarks/allocator/") }
        "windows-allocator" { return @("benchmarks/allocator/") }
        "console" { return @("benchmarks/console/") }
        "windows-console" { return @("benchmarks/console/") }
        "directory" { return @("DirectoryEnumeration") }
        "windows-directory" { return @("DirectoryEnumeration") }
        "file" { return @("benchmarks/io/File") }
        "windows-file" { return @("benchmarks/io/File") }
        "socket" { return @("benchmarks/network/Tcp") }
        "network" { return @("benchmarks/network/Tcp") }
        "windows-socket" { return @("benchmarks/network/Tcp") }
        "windows-network" { return @("benchmarks/network/Tcp") }
        "windows-io" { return @("benchmarks/io/File", "DirectoryEnumeration") }
        "windows-core" { return @("benchmarks/allocator/", "benchmarks/console/", "benchmarks/io/File", "DirectoryEnumeration", "benchmarks/network/Tcp") }
        default {
            throw "Unknown benchmark subset '$Name'. Expected allocator, console, directory, file, socket, network, windows-io, or windows-core."
        }
    }
}

function Test-BenchmarkMatchesAnyFilter {
    param(
        [string]$RelativePath,
        [string]$BenchmarkId,
        [string]$BenchmarkGroup,
        [string[]]$Filters
    )

    if ($Filters.Count -eq 0) {
        return $true
    }

    foreach ($filterValue in $Filters) {
        if ([string]::IsNullOrWhiteSpace($filterValue)) {
            continue
        }

        $normalizedFilter = $filterValue -replace '\\', '/'
        if ($RelativePath -like "*$normalizedFilter*" -or
            $BenchmarkId -like "*$normalizedFilter*" -or
            $BenchmarkGroup -like "*$normalizedFilter*") {
            return $true
        }
    }

    return $false
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

function Join-ProcessArguments {
    param([string[]]$Arguments)

    $quoted = New-Object System.Collections.Generic.List[string]
    foreach ($argument in @($Arguments)) {
        if ($null -eq $argument) {
            $argument = ""
        }

        if ($argument.Length -gt 0 -and $argument -notmatch '[\s"]') {
            $quoted.Add($argument)
            continue
        }

        $builder = New-Object System.Text.StringBuilder
        [void]$builder.Append('"')
        $backslashes = 0
        foreach ($character in $argument.ToCharArray()) {
            if ($character -eq '\') {
                $backslashes += 1
                continue
            }

            if ($character -eq '"') {
                [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
                [void]$builder.Append('"')
                $backslashes = 0
                continue
            }

            if ($backslashes -gt 0) {
                [void]$builder.Append(('\' * $backslashes))
                $backslashes = 0
            }

            [void]$builder.Append($character)
        }

        if ($backslashes -gt 0) {
            [void]$builder.Append(('\' * ($backslashes * 2)))
        }

        [void]$builder.Append('"')
        $quoted.Add($builder.ToString())
    }

    return $quoted -join " "
}

function Invoke-Native {
    param(
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [switch]$SuppressOutput
    )

    if ($SuppressOutput) {
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $FilePath
        $argumentListProperty = [System.Diagnostics.ProcessStartInfo].GetProperty("ArgumentList")
        if ($null -ne $argumentListProperty) {
            foreach ($argument in @($Arguments)) {
                [void]$startInfo.ArgumentList.Add($argument)
            }
        }
        else {
            $startInfo.Arguments = Join-ProcessArguments $Arguments
        }

        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.CreateNoWindow = $true

        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $startInfo
        if (!$process.Start()) {
            throw "Unable to start command: $FilePath"
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdoutTask.Wait()
        $stderrTask.Wait()
        $exitCode = $process.ExitCode
        $process.Dispose()

        if ($exitCode -ne 0) {
            throw "Command failed with exit code $exitCode`: $FilePath $($Arguments -join ' ')"
        }

        return
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

function Get-MedianMicroseconds {
    param([long[]]$Values)

    if ($Values.Count -eq 0) {
        return 0
    }

    $sorted = @($Values | Sort-Object)
    $middle = [int]($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return [long]$sorted[$middle]
    }

    return [long](($sorted[$middle - 1] + $sorted[$middle]) / 2)
}

function Get-FileSizeBytes {
    param([string]$Path)

    if (!(Test-Path -LiteralPath $Path)) {
        return 0
    }

    return (Get-Item -LiteralPath $Path).Length
}

function Get-RuntimeSpreadPercent {
    param(
        [long]$MinMicroseconds,
        [long]$AverageMicroseconds,
        [long]$MaxMicroseconds
    )

    if ($AverageMicroseconds -le 0) {
        return "0.000000"
    }

    $spread = (($MaxMicroseconds - $MinMicroseconds) * 100.0) / $AverageMicroseconds
    return $spread.ToString("0.000000", [Globalization.CultureInfo]::InvariantCulture)
}

function Read-MetricValue {
    param(
        [string]$Path,
        [string]$Name
    )

    if (!(Test-Path -LiteralPath $Path)) {
        return 0
    }

    $prefix = "$Name="
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line.StartsWith($prefix, [StringComparison]::Ordinal)) {
            [long]$value = 0
            if ([long]::TryParse($line.Substring($prefix.Length), [ref]$value)) {
                return $value
            }

            return 0
        }
    }

    return 0
}

function Emit-Row {
    param([string]$Row)

    Write-Output $Row
    Add-Content -Path $script:ResultsFile -Value $Row
}

function Add-CRelativeRuntimeRatios {
    param([string]$Path)

    $lines = @(Get-Content -LiteralPath $Path)
    if ($lines.Count -eq 0) {
        return
    }

    $header = @($lines[0].Split(","))
    $benchmarkIndex = [Array]::IndexOf($header, "benchmark")
    $languageIndex = [Array]::IndexOf($header, "language")
    $medianIndex = [Array]::IndexOf($header, "median_us")
    $avgIndex = [Array]::IndexOf($header, "avg_us")
    if ($benchmarkIndex -lt 0 -or $languageIndex -lt 0 -or $medianIndex -lt 0 -or $avgIndex -lt 0) {
        throw "Benchmark CSV must contain benchmark, language, median_us, and avg_us columns."
    }

    $medianRatioIndex = [Array]::IndexOf($header, "c_median_ratio")
    $ratioIndex = [Array]::IndexOf($header, "c_avg_ratio")
    $keptIndexes = New-Object System.Collections.Generic.List[int]
    for ($index = 0; $index -lt $header.Count; $index++) {
        if ($index -ne $ratioIndex -and $index -ne $medianRatioIndex) {
            $keptIndexes.Add($index)
        }
    }

    $cMedians = @{}
    $cAverages = @{}
    $rows = New-Object System.Collections.Generic.List[object[]]
    for ($lineIndex = 1; $lineIndex -lt $lines.Count; $lineIndex++) {
        if ([string]::IsNullOrWhiteSpace($lines[$lineIndex])) {
            continue
        }

        $fields = @($lines[$lineIndex].Split(","))
        $rows.Add($fields)

        if ($fields.Count -le [Math]::Max($languageIndex, [Math]::Max($medianIndex, $avgIndex))) {
            continue
        }

        [double]$median = 0
        [double]$avg = 0
        if ($fields[$languageIndex] -eq "c") {
            if ([double]::TryParse($fields[$medianIndex], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$median) -and
                $median -gt 0) {
                $cMedians[$fields[$benchmarkIndex]] = $median
            }

            if ([double]::TryParse($fields[$avgIndex], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$avg) -and
                $avg -gt 0) {
                $cAverages[$fields[$benchmarkIndex]] = $avg
            }
        }
    }

    $output = New-Object System.Collections.Generic.List[string]
    $output.Add((($keptIndexes | ForEach-Object { $header[$_] }) + @("c_median_ratio", "c_avg_ratio")) -join ",")

    foreach ($fields in $rows) {
        $keptFields = $keptIndexes | ForEach-Object {
            if ($_ -lt $fields.Count) {
                $fields[$_]
            }
            else {
                ""
            }
        }

        $medianRatio = ""
        $avgRatio = ""
        [double]$rowMedian = 0
        [double]$rowAvg = 0
        [double]$baselineMedian = 0
        [double]$baselineAvg = 0
        if ($fields.Count -gt $benchmarkIndex) {
            if ($cMedians.ContainsKey($fields[$benchmarkIndex])) {
                $baselineMedian = [double]$cMedians[$fields[$benchmarkIndex]]
            }

            if ($cAverages.ContainsKey($fields[$benchmarkIndex])) {
                $baselineAvg = [double]$cAverages[$fields[$benchmarkIndex]]
            }
        }

        if ($fields.Count -gt [Math]::Max($benchmarkIndex, $medianIndex) -and
            $baselineMedian -gt 0 -and
            [double]::TryParse($fields[$medianIndex], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$rowMedian)) {
            $medianRatio = ($rowMedian / $baselineMedian).ToString("0.000000", [Globalization.CultureInfo]::InvariantCulture)
        }

        if ($fields.Count -gt [Math]::Max($benchmarkIndex, $avgIndex) -and
            $baselineAvg -gt 0 -and
            [double]::TryParse($fields[$avgIndex], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$rowAvg)) {
            $avgRatio = ($rowAvg / $baselineAvg).ToString("0.000000", [Globalization.CultureInfo]::InvariantCulture)
        }

        $output.Add(($keptFields + @($medianRatio, $avgRatio)) -join ",")
    }

    Set-Content -LiteralPath $Path -Value $output
}

function Complete-ResultsFile {
    param([string]$Reason)

    if (!(Test-Path -LiteralPath $ResultsFile)) {
        return
    }

    $firstLine = Get-Content -LiteralPath $ResultsFile -TotalCount 1
    if ([string]::IsNullOrWhiteSpace($firstLine) -or !$firstLine.StartsWith("benchmark,")) {
        return
    }

    try {
        Add-CRelativeRuntimeRatios $ResultsFile
        Write-Status "Added c_median_ratio and c_avg_ratio columns using same-benchmark C runtime baselines$Reason."
    }
    catch {
        Write-Status "Unable to add C runtime ratio columns$Reason`: $($_.Exception.Message)"
    }
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
        "benchmark_subset=$(if ([string]::IsNullOrWhiteSpace($Subset)) { '<none>' } else { $Subset })",
        "benchmark_languages=$Languages",
        "benchmark_capture_rss=$(if ($script:captureRss) { '1' } else { '0' })",
        "benchmark_runtime_only=$(if ($script:runtimeOnly -eq $true) { '1' } else { '0' })",
        "benchmark_keep_binaries=$(if ($script:keepBinaries -eq $true) { '1' } else { '0' })",
        "benchmark_binary_dir=$(if ([string]::IsNullOrWhiteSpace($BinaryDir)) { '<temp>' } else { $BinaryDir })",
        "benchmark_peak_rss_unit=KiB",
        "benchmark_peak_rss_source=Process.PeakWorkingSet64 captured after each benchmark process exits when STARK_BENCH_CAPTURE_RSS=1; 0 when disabled",
        "benchmark_median_column=median_us median of timed runs",
        "benchmark_ratio_column=c_median_ratio median_us divided by same-benchmark C median_us; c_avg_ratio is retained for outlier diagnostics",
        "benchmark_compile_columns=compile_us total benchmark build wall time; llvm_object_us/link_us/toolchain_us from Stark --toolchain-metrics when available",
        "stark_target=$(if ([string]::IsNullOrWhiteSpace($Target)) { 'host-default' } else { $Target })",
        "stark_flags=--emit-exe -O3",
        "stark_compiler_configuration=Release",
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
        [long]$LlvmObjectMicroseconds,
        [long]$LinkMicroseconds,
        [long]$ToolchainMicroseconds,
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
    $runMicroseconds = New-Object System.Collections.Generic.List[long]

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
        $runMicroseconds.Add($elapsedMicroseconds)

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
    $medianMicroseconds = Get-MedianMicroseconds $runMicroseconds.ToArray()
    $runtimeSpreadPercent = Get-RuntimeSpreadPercent $minMicroseconds $avgMicroseconds $maxMicroseconds
    $binaryBytes = Get-FileSizeBytes $OutputPath
    $label = Get-BenchmarkLabel $BenchmarkId $Language
    Emit-Row "$($label.BenchmarkGroup),$($label.Language),$Runs,$CompileMicroseconds,$LlvmObjectMicroseconds,$LinkMicroseconds,$ToolchainMicroseconds,$binaryBytes,$minMicroseconds,$medianMicroseconds,$avgMicroseconds,$maxMicroseconds,$runtimeSpreadPercent,$peakRssKiB"
}

function Assert-RuntimeOnlyExecutable {
    param(
        [string]$Path,
        [string]$BenchmarkId,
        [string]$Language
    )

    if (!(Test-Path -LiteralPath $Path)) {
        throw "Runtime-only benchmark mode expected an existing $Language executable for $BenchmarkId at $Path."
    }
}

function Compile-AndTimeStark {
    param(
        [string]$SourcePath,
        [string]$BenchmarkId,
        [string]$OutputPath
    )

    if ($script:runtimeOnly -eq $true) {
        Assert-RuntimeOnlyExecutable $OutputPath $BenchmarkId "stark"
        Time-Executable $BenchmarkId "stark" 0 0 0 0 $OutputPath
        return
    }

    $metricsPath = "$OutputPath.metrics"
    $arguments = @(
        "run",
        "-c",
        "Release",
        "--project",
        (Join-Path $repoRoot "src"),
        "--",
        $SourcePath,
        "--emit-exe",
        "-O3",
        "-I",
        $stdlibRoot,
        "-o",
        $OutputPath,
        "--toolchain-metrics",
        $metricsPath
    )
    $arguments += $script:compilerArgs

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-Native "dotnet" $arguments -SuppressOutput
    $stopwatch.Stop()

    Time-Executable `
        $BenchmarkId `
        "stark" `
        (Get-ElapsedMicroseconds $stopwatch) `
        (Read-MetricValue $metricsPath "llvm_object_us") `
        (Read-MetricValue $metricsPath "link_us") `
        (Read-MetricValue $metricsPath "toolchain_us") `
        $OutputPath
}

function Compile-AndTimeC {
    param(
        [string]$SourcePath,
        [string]$BenchmarkId,
        [string]$OutputPath
    )

    if ($script:runtimeOnly -eq $true) {
        Assert-RuntimeOnlyExecutable $OutputPath $BenchmarkId "c"
        Time-Executable $BenchmarkId "c" 0 0 0 0 $OutputPath
        return
    }

    $arguments = @($SourcePath) + $cFlags + @("-o", $OutputPath)
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-Native $CCompiler $arguments -SuppressOutput
    $stopwatch.Stop()

    Time-Executable $BenchmarkId "c" (Get-ElapsedMicroseconds $stopwatch) 0 0 0 $OutputPath
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

    if ($script:runtimeOnly -eq $true) {
        Assert-RuntimeOnlyExecutable $OutputPath $BenchmarkId $Language
        Time-Executable $BenchmarkId $Language 0 0 0 0 $OutputPath
        return
    }

    $arguments = @("--crate-name", (ConvertTo-RustCrateName $SourcePath), $SourcePath) + $rustFlags + @("-o", $OutputPath)
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-Native $RustCompiler $arguments -SuppressOutput
    $stopwatch.Stop()

    Time-Executable $BenchmarkId $Language (Get-ElapsedMicroseconds $stopwatch) 0 0 0 $OutputPath
}

$script:selectedLanguages = @()
$script:captureRss = [bool](ConvertTo-BenchmarkBoolean $CaptureRss "STARK_BENCH_CAPTURE_RSS")
$script:runtimeOnly = [bool](ConvertTo-BenchmarkBoolean $RuntimeOnly "STARK_BENCH_RUNTIME_ONLY")
$script:keepBinaries = [bool](ConvertTo-BenchmarkBoolean $KeepBinaries "STARK_BENCH_KEEP_BINARIES")
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

if ($script:runtimeOnly -eq $true -and [string]::IsNullOrWhiteSpace($BinaryDir)) {
    throw "STARK_BENCH_RUNTIME_ONLY requires STARK_BENCH_BINARY_DIR so the harness can reuse existing executables."
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

$tmpDir = if ([string]::IsNullOrWhiteSpace($BinaryDir)) {
    Join-Path ([IO.Path]::GetTempPath()) "stark-bench-$([Guid]::NewGuid().ToString('N'))"
}
else {
    [IO.Path]::GetFullPath($BinaryDir)
}
Ensure-Directory $tmpDir

$script:compilerArgs = @()
if (![string]::IsNullOrWhiteSpace($Target)) {
    $script:compilerArgs += @("--target", $Target)
}

$script:compilerArgs += Split-ArgumentString $ExtraCompilerArgs
$script:benchmarkRunnerExitCode = 0

try {
    Write-MachineMetadata $MachineFile
    Write-Status "Benchmark results: $ResultsFile"
    Write-Status "Machine metadata: $MachineFile"

    $benchmarks = Get-ChildItem -Path $benchRoot -Recurse -Filter "*.stark" -File |
        Sort-Object FullName

    $subsetFilters = @(Get-BenchmarkSubsetFilters $Subset)
    if (![string]::IsNullOrWhiteSpace($Filter) -or $subsetFilters.Count -gt 0) {
        $manualFilters = @()
        if (![string]::IsNullOrWhiteSpace($Filter)) {
            $manualFilters = @($Filter)
        }

        $benchmarks = $benchmarks | Where-Object {
            $relativePath = ConvertTo-DisplayPath (Get-RelativePath $repoRoot $_.FullName)
            $benchmarkId = $relativePath -replace '\.stark$', ''
            $benchmarkGroup = (Get-BenchmarkLabel $benchmarkId "stark").BenchmarkGroup
            (Test-BenchmarkMatchesAnyFilter $relativePath $benchmarkId $benchmarkGroup $manualFilters) -and
                (Test-BenchmarkMatchesAnyFilter $relativePath $benchmarkId $benchmarkGroup $subsetFilters)
        }
    }

    if ($null -eq $benchmarks -or @($benchmarks).Count -eq 0) {
        throw "No benchmark sources matched."
    }

    $benchmarkEntries = @($benchmarks | ForEach-Object {
        $sourcePath = $_.FullName
        $relativePath = ConvertTo-DisplayPath (Get-RelativePath $repoRoot $sourcePath)
        $benchmarkId = $relativePath -replace '\.stark$', ''
        $label = Get-BenchmarkLabel $benchmarkId "stark"
        $stem = [IO.Path]::GetFileNameWithoutExtension($sourcePath)

        [PSCustomObject]@{
            SourcePath = $sourcePath
            RelativePath = $relativePath
            BenchmarkId = $benchmarkId
            BenchmarkGroup = $label.BenchmarkGroup
            VariantOrder = 0
        }
    } | Sort-Object BenchmarkGroup, VariantOrder, RelativePath)

    $lastBenchmarkPathByGroup = @{}
    foreach ($entry in $benchmarkEntries) {
        $lastBenchmarkPathByGroup[$entry.BenchmarkGroup] = $entry.RelativePath
    }

    Emit-Row "benchmark,language,runs,compile_us,llvm_object_us,link_us,toolchain_us,binary_bytes,min_us,median_us,avg_us,max_us,runtime_spread_pct,peak_rss_kib"

    $timedNativeBenchmarks = @{}
    foreach ($entry in $benchmarkEntries) {
        $sourcePath = $entry.SourcePath
        $relativePath = $entry.RelativePath

        if (Test-BenchmarkDirective $sourcePath "compile-only") {
            Write-Status "Skipping compile-only benchmark $relativePath; compiler tests still validate it lowers successfully."
            continue
        }

        $safeName = ConvertTo-SafeName $relativePath
        $benchmarkId = $entry.BenchmarkId
        $benchmarkGroup = $entry.BenchmarkGroup
        $nativeSafeName = ConvertTo-SafeName $benchmarkGroup
        $runNativeForGroup = $lastBenchmarkPathByGroup[$benchmarkGroup] -eq $relativePath

        $starkOutputPath = Join-Path $tmpDir "$safeName-stark.exe"
        $cOutputPath = Join-Path $tmpDir "$nativeSafeName-c.exe"
        $rustOutputPath = Join-Path $tmpDir "$nativeSafeName-rust.exe"

        if (Test-LanguageEnabled "stark") {
            Compile-AndTimeStark $sourcePath $benchmarkId $starkOutputPath
        }

        $cBenchmarkKey = "c|$benchmarkGroup"
        if ($runNativeForGroup -and (Test-LanguageEnabled "c") -and !$timedNativeBenchmarks.ContainsKey($cBenchmarkKey)) {
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
        if ($runNativeForGroup -and (Test-LanguageEnabled "rust") -and !$timedNativeBenchmarks.ContainsKey($rustBenchmarkKey)) {
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

    Add-CRelativeRuntimeRatios $ResultsFile
    Write-Status "Added c_median_ratio and c_avg_ratio columns using same-benchmark C runtime baselines."
}
catch {
    Complete-ResultsFile " before exiting after failure"
    Write-Status $_.Exception.Message
    $script:benchmarkRunnerExitCode = 1
}
finally {
    if ($script:keepBinaries -ne $true -and $script:runtimeOnly -ne $true -and [string]::IsNullOrWhiteSpace($BinaryDir) -and (Test-Path -LiteralPath $tmpDir)) {
        Remove-Item -LiteralPath $tmpDir -Recurse -Force
    }
}

if ($script:benchmarkRunnerExitCode -ne 0) {
    exit $script:benchmarkRunnerExitCode
}
