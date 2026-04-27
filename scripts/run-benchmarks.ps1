param(
    [int]$Runs = $(if ($env:STARK_BENCH_RUNS) { [int]$env:STARK_BENCH_RUNS } else { 50 }),
    [string]$Filter = $env:STARK_BENCH_FILTER,
    [string]$Target = $env:STARK_TARGET,
    [string]$ExtraCompilerArgs = $env:STARK_COMPILER_ARGS,
    [string]$Languages = $(if ($env:STARK_BENCH_LANGUAGES) { $env:STARK_BENCH_LANGUAGES } else { "stark,c,rust" }),
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

    Invoke-Native $OutputPath @() -SuppressOutput

    [long]$totalMicroseconds = 0
    [long]$minMicroseconds = 0
    [long]$maxMicroseconds = 0

    for ($run = 1; $run -le $Runs; $run++) {
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-Native $OutputPath @() -SuppressOutput
        $stopwatch.Stop()
        $elapsedMicroseconds = Get-ElapsedMicroseconds $stopwatch
        $totalMicroseconds += $elapsedMicroseconds

        if ($minMicroseconds -eq 0 -or $elapsedMicroseconds -lt $minMicroseconds) {
            $minMicroseconds = $elapsedMicroseconds
        }

        if ($elapsedMicroseconds -gt $maxMicroseconds) {
            $maxMicroseconds = $elapsedMicroseconds
        }
    }

    $avgMicroseconds = [long]($totalMicroseconds / $Runs)
    Emit-Row "$BenchmarkId,$Language,$Runs,$CompileMicroseconds,$minMicroseconds,$avgMicroseconds,$maxMicroseconds"
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

    Emit-Row "benchmark,language,runs,compile_us,min_us,avg_us,max_us"

    foreach ($benchmark in @($benchmarks)) {
        $sourcePath = $benchmark.FullName
        $relativePath = ConvertTo-DisplayPath (Get-RelativePath $repoRoot $sourcePath)

        if (Test-BenchmarkDirective $sourcePath "compile-only") {
            Write-Status "Skipping compile-only benchmark $relativePath; compiler tests still validate it lowers successfully."
            continue
        }

        $safeName = ConvertTo-SafeName $relativePath
        $benchmarkId = $relativePath -replace '\.stark$', ''
        $sourceStem = $sourcePath.Substring(0, $sourcePath.Length - ".stark".Length)

        $starkOutputPath = Join-Path $tmpDir "$safeName-stark.exe"
        $cOutputPath = Join-Path $tmpDir "$safeName-c.exe"
        $rustOutputPath = Join-Path $tmpDir "$safeName-rust.exe"

        if (Test-LanguageEnabled "stark") {
            Compile-AndTimeStark $sourcePath $benchmarkId $starkOutputPath
        }

        if (Test-LanguageEnabled "c") {
            $cSourcePath = "$sourceStem.c"
            if (!(Test-Path -LiteralPath $cSourcePath)) {
                throw "Missing C benchmark counterpart for $relativePath`: $(ConvertTo-DisplayPath (Get-RelativePath $repoRoot $cSourcePath))"
            }

            if ((Test-WindowsHost) -and (Test-BenchmarkDirective $cSourcePath "skip-c-windows")) {
                $cRelativePath = ConvertTo-DisplayPath (Get-RelativePath $repoRoot $cSourcePath)
                Write-Status "Skipping C benchmark $benchmarkId on Windows; $cRelativePath is marked // stark-bench: skip-c-windows."
            }
            else {
                Compile-AndTimeC $cSourcePath $benchmarkId $cOutputPath
            }
        }

        if (Test-LanguageEnabled "rust") {
            $rustSourcePath = "$sourceStem.rs"
            if (!(Test-Path -LiteralPath $rustSourcePath)) {
                throw "Missing Rust benchmark counterpart for $relativePath`: $(ConvertTo-DisplayPath (Get-RelativePath $repoRoot $rustSourcePath))"
            }

            Compile-AndTimeRust $rustSourcePath $benchmarkId $rustOutputPath

            $rustVariantPaths = Get-ChildItem -Path "$sourceStem.rust-*.rs" -File -ErrorAction SilentlyContinue |
                Sort-Object FullName
            foreach ($rustVariantPath in @($rustVariantPaths)) {
                $rustVariantName = [IO.Path]::GetFileName($rustVariantPath.FullName)
                $rustVariantName = $rustVariantName.Substring(([IO.Path]::GetFileName($sourceStem) + ".rust-").Length)
                $rustVariantName = $rustVariantName.Substring(0, $rustVariantName.Length - ".rs".Length)
                if ($rustVariantName.Contains(",")) {
                    throw "Rust benchmark variant names must not contain commas: $(ConvertTo-DisplayPath (Get-RelativePath $repoRoot $rustVariantPath.FullName))"
                }

                $rustVariantSafeName = $rustVariantName -replace '[^A-Za-z0-9_]', '_'
                $rustVariantOutputPath = Join-Path $tmpDir "$safeName-rust-$rustVariantSafeName.exe"
                Compile-AndTimeRust $rustVariantPath.FullName $benchmarkId $rustVariantOutputPath "rust-$rustVariantName"
            }
        }
    }
}
finally {
    if (Test-Path -LiteralPath $tmpDir) {
        Remove-Item -LiteralPath $tmpDir -Recurse -Force
    }
}
