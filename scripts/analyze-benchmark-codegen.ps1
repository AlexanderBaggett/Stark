param(
    [string[]]$Benchmarks = @(
        "benchmarks/micro/Arithmetic",
        "benchmarks/allocator/MemoryCopyFill",
        "benchmarks/collections/DictionaryLookup",
        "benchmarks/collections/DictionaryMixed",
        "benchmarks/console/ConsoleWrites"
    ),
    [string]$Languages = $(if ($env:STARK_CODEGEN_LANGUAGES) { $env:STARK_CODEGEN_LANGUAGES } else { "stark,c,rust" }),
    [string]$Target = $env:STARK_TARGET,
    [string]$ExtraCompilerArgs = $env:STARK_COMPILER_ARGS,
    [string]$CCompiler = $(if ($env:STARK_BENCH_C_COMPILER) { $env:STARK_BENCH_C_COMPILER } else { "clang" }),
    [string]$RustCompiler = $(if ($env:STARK_BENCH_RUST_COMPILER) { $env:STARK_BENCH_RUST_COMPILER } else { "rustc" }),
    [string]$Objdump = $(if ($env:STARK_OBJDUMP) { $env:STARK_OBJDUMP } else { "llvm-objdump" }),
    [string]$OutputDir = $env:STARK_CODEGEN_OUTPUT_DIR,
    [string]$SummaryFile = $env:STARK_CODEGEN_SUMMARY_FILE,
    [switch]$LlvmOnly
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..")).Path
$stdlibRoot = Join-Path $repoRoot "stdlib\src"
$timestamp = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ")
$cFlags = @("-O3", "-DNDEBUG", "-std=c17")
$rustFlags = @("-C", "opt-level=3", "-C", "debug-assertions=no", "-C", "overflow-checks=no")

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "benchmarks\codegen"
}

if ([string]::IsNullOrWhiteSpace($SummaryFile)) {
    $SummaryFile = Join-Path $OutputDir "codegen-$timestamp.csv"
}

function Ensure-Directory {
    param([string]$Path)

    if (![string]::IsNullOrWhiteSpace($Path) -and !(Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
}

function ConvertTo-DisplayPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    return (Resolve-Path -LiteralPath $Path).Path.Replace($repoRoot + [IO.Path]::DirectorySeparatorChar, "").Replace("\", "/")
}

function ConvertTo-SafeName {
    param([string]$Name)

    return ($Name -replace '[\\/]', '_') -replace '[^A-Za-z0-9_.-]', '_'
}

function Test-CommandExists {
    param([string]$Name)

    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Invoke-Process {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $argumentListProperty = [System.Diagnostics.ProcessStartInfo].GetProperty("ArgumentList")
    if ($null -ne $argumentListProperty) {
        foreach ($argument in @($Arguments)) {
            [void]$startInfo.ArgumentList.Add($argument)
        }
    }
    else {
        $startInfo.Arguments = ($Arguments | ForEach-Object {
            if ($_ -match '[\s"]') {
                '"' + ($_ -replace '"', '\"') + '"'
            }
            else {
                $_
            }
        }) -join " "
    }

    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $process.Dispose()

    return [PSCustomObject]@{
        ExitCode = $exitCode
        Stdout = $stdout
        Stderr = $stderr
    }
}

function Get-BenchmarkId {
    param([string]$Benchmark)

    $normalized = $Benchmark.Replace("\", "/")
    if ($normalized.EndsWith(".stark", [StringComparison]::Ordinal) -or
        $normalized.EndsWith(".c", [StringComparison]::Ordinal) -or
        $normalized.EndsWith(".rs", [StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(0, $normalized.LastIndexOf(".", [StringComparison]::Ordinal))
    }

    return $normalized.Trim("/")
}

function Get-SourcePath {
    param(
        [string]$BenchmarkId,
        [string]$Extension
    )

    return Join-Path $repoRoot (($BenchmarkId -replace '/', [IO.Path]::DirectorySeparatorChar) + $Extension)
}

function ConvertTo-RustCrateName {
    param([string]$SourcePath)

    $crateName = [IO.Path]::GetFileNameWithoutExtension($SourcePath) -replace '[^A-Za-z0-9_]', '_'
    if ($crateName -match '^[0-9]') {
        return "bench_$crateName"
    }

    return $crateName
}

function Get-DefinedFunctionNames {
    param([string]$Text)

    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($match in [regex]::Matches($Text, '(?m)^define\s+.*?@([^(\s]+)\(')) {
        [void]$set.Add($match.Groups[1].Value)
    }

    return $set
}

function Count-ExternalCalls {
    param([string]$Text)

    $defined = Get-DefinedFunctionNames $Text
    $count = 0
    foreach ($match in [regex]::Matches($Text, '\b(?:tail\s+)?call\b[^@]*@([^(\s]+)\(')) {
        $name = $match.Groups[1].Value
        if ($name.StartsWith("llvm.", [StringComparison]::Ordinal)) {
            continue
        }

        if (!$defined.Contains($name)) {
            $count += 1
        }
    }

    return $count
}

function Count-Matches {
    param(
        [string]$Text,
        [string]$Pattern
    )

    return [regex]::Matches($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase).Count
}

function New-ReportRow {
    param(
        [string]$Benchmark,
        [string]$Language,
        [string]$SourcePath,
        [string]$IrPath,
        [string]$OptimizedIrPath,
        [string]$AsmPath,
        [string]$ObjectPath,
        [string]$DisassemblyPath,
        [string]$Notes
    )

    $irText = ""
    if (Test-Path -LiteralPath $OptimizedIrPath) {
        $irText = Get-Content -LiteralPath $OptimizedIrPath -Raw
    }
    elseif (Test-Path -LiteralPath $IrPath) {
        $irText = Get-Content -LiteralPath $IrPath -Raw
    }

    $asmText = ""
    if (Test-Path -LiteralPath $DisassemblyPath) {
        $asmText = Get-Content -LiteralPath $DisassemblyPath -Raw
    }
    elseif (Test-Path -LiteralPath $AsmPath) {
        $asmText = Get-Content -LiteralPath $AsmPath -Raw
    }

    return [PSCustomObject]@{
        benchmark = $Benchmark
        language = $Language
        source = $(if (Test-Path -LiteralPath $SourcePath) { ConvertTo-DisplayPath $SourcePath } else { $SourcePath })
        ir = $(if (Test-Path -LiteralPath $IrPath) { ConvertTo-DisplayPath $IrPath } else { "" })
        optimized_ir = $(if (Test-Path -LiteralPath $OptimizedIrPath) { ConvertTo-DisplayPath $OptimizedIrPath } else { "" })
        asm = $(if (Test-Path -LiteralPath $AsmPath) { ConvertTo-DisplayPath $AsmPath } else { "" })
        obj = $(if (Test-Path -LiteralPath $ObjectPath) { ConvertTo-DisplayPath $ObjectPath } else { "" })
        disasm = $(if (Test-Path -LiteralPath $DisassemblyPath) { ConvertTo-DisplayPath $DisassemblyPath } else { "" })
        external_call_count = $(if ($irText.Length -gt 0) { Count-ExternalCalls $irText } else { 0 })
        optnone_count = $(if ($irText.Length -gt 0) { Count-Matches $irText '\boptnone\b' } else { 0 })
        memory_intrinsic_count = $(if ($irText.Length -gt 0) { Count-Matches $irText '@llvm\.mem(?:cpy|move|set)' } else { 0 })
        vector_or_bitop_count = $(if ($asmText.Length -gt 0) { Count-Matches $asmText '\b(?:xmm\d*|ymm\d*|zmm\d*|v?pcmpeq\w*|pmovmskb|tzcnt\w*|bsf\w*|lzcnt\w*|popcnt\w*|movdqu\w*|movdqa\w*|movups\w*|movaps\w*)\b' } else { 0 })
        runtime_helper_call_count = $(if ($irText.Length -gt 0) { Count-Matches $irText '@(?:__stark_runtime|__stark_dynamic|System_Runtime_|System_Memory_)' } else { 0 })
        notes = $Notes
    }
}

function Run-AndNote {
    param(
        [string]$Tool,
        [string[]]$Arguments,
        [string]$FailureNote
    )

    $result = Invoke-Process $Tool $Arguments
    if ($result.ExitCode -ne 0) {
        return "$FailureNote exit=$($result.ExitCode) $($result.Stderr.Trim())"
    }

    return ""
}

function Try-Disassemble {
    param(
        [string]$ObjectPath,
        [string]$DisassemblyPath
    )

    if (!(Test-Path -LiteralPath $ObjectPath)) {
        return "object-missing"
    }

    if (!(Test-CommandExists $Objdump)) {
        return "objdump-not-found"
    }

    $result = Invoke-Process $Objdump @("-d", $ObjectPath)
    if ($result.ExitCode -ne 0) {
        return "objdump-failed exit=$($result.ExitCode)"
    }

    Set-Content -LiteralPath $DisassemblyPath -Value $result.Stdout
    return ""
}

function Analyze-Stark {
    param(
        [string]$BenchmarkId,
        [string]$Language,
        [string]$SourcePath,
        [string]$SafeName
    )

    $irPath = Join-Path $OutputDir "$SafeName.ll"
    $optimizedIrPath = Join-Path $OutputDir "$SafeName.opt.ll"
    $asmPath = Join-Path $OutputDir "$SafeName.s"
    $objectPath = Join-Path $OutputDir "$SafeName.obj"
    $disassemblyPath = Join-Path $OutputDir "$SafeName.disasm.txt"
    $notes = New-Object System.Collections.Generic.List[string]

    $compilerArgs = @("run", "--project", (Join-Path $repoRoot "src\compiler.csproj"), "--", $SourcePath, "--emit-llvm", "-O3", "-I", $stdlibRoot, "-o", $irPath)
    if (![string]::IsNullOrWhiteSpace($Target)) {
        $compilerArgs += @("--target", $Target)
    }

    if (![string]::IsNullOrWhiteSpace($ExtraCompilerArgs)) {
        $compilerArgs += $ExtraCompilerArgs.Split(" ", [System.StringSplitOptions]::RemoveEmptyEntries)
    }

    $note = Run-AndNote "dotnet" $compilerArgs "stark-emit-llvm-failed"
    if (![string]::IsNullOrWhiteSpace($note)) {
        $notes.Add($note)
        return New-ReportRow $BenchmarkId $Language $SourcePath $irPath $optimizedIrPath $asmPath $objectPath $disassemblyPath ($notes -join "; ")
    }

    if (Test-CommandExists $CCompiler) {
        $note = Run-AndNote $CCompiler @("-O3", "-S", "-emit-llvm", $irPath, "-o", $optimizedIrPath) "llvm-opt-failed"
        if (![string]::IsNullOrWhiteSpace($note)) { $notes.Add($note) }
        if ($LlvmOnly) {
            $notes.Add("llvm-only")
            return New-ReportRow $BenchmarkId $Language $SourcePath $irPath $optimizedIrPath $asmPath $objectPath $disassemblyPath ($notes -join "; ")
        }

        $note = Run-AndNote $CCompiler @("-O3", "-S", $irPath, "-o", $asmPath) "stark-asm-failed"
        if (![string]::IsNullOrWhiteSpace($note)) { $notes.Add($note) }
        $note = Run-AndNote $CCompiler @("-O3", "-c", $irPath, "-o", $objectPath) "stark-obj-failed"
        if (![string]::IsNullOrWhiteSpace($note)) { $notes.Add($note) }
    }
    else {
        $notes.Add("clang-not-found")
    }

    $note = Try-Disassemble $objectPath $disassemblyPath
    if (![string]::IsNullOrWhiteSpace($note) -and $note -ne "object-missing") {
        $notes.Add($note)
    }

    return New-ReportRow $BenchmarkId $Language $SourcePath $irPath $optimizedIrPath $asmPath $objectPath $disassemblyPath ($notes -join "; ")
}

function Analyze-C {
    param(
        [string]$BenchmarkId,
        [string]$SourcePath,
        [string]$SafeName
    )

    $irPath = Join-Path $OutputDir "$SafeName.opt.ll"
    $asmPath = Join-Path $OutputDir "$SafeName.s"
    $objectPath = Join-Path $OutputDir "$SafeName.obj"
    $disassemblyPath = Join-Path $OutputDir "$SafeName.disasm.txt"
    $notes = New-Object System.Collections.Generic.List[string]

    if (!(Test-CommandExists $CCompiler)) {
        $notes.Add("c-compiler-not-found")
        return New-ReportRow $BenchmarkId "c" $SourcePath $irPath $irPath $asmPath $objectPath $disassemblyPath ($notes -join "; ")
    }

    $targetArgs = @()
    if (![string]::IsNullOrWhiteSpace($Target)) {
        $targetArgs = @("--target", $Target)
    }

    $note = Run-AndNote $CCompiler ($targetArgs + $cFlags + @("-S", "-emit-llvm", $SourcePath, "-o", $irPath)) "c-llvm-failed"
    if (![string]::IsNullOrWhiteSpace($note)) { $notes.Add($note) }
    if ($LlvmOnly) {
        $notes.Add("llvm-only")
        return New-ReportRow $BenchmarkId "c" $SourcePath $irPath $irPath $asmPath $objectPath $disassemblyPath ($notes -join "; ")
    }

    $note = Run-AndNote $CCompiler ($targetArgs + $cFlags + @("-S", $SourcePath, "-o", $asmPath)) "c-asm-failed"
    if (![string]::IsNullOrWhiteSpace($note)) { $notes.Add($note) }
    $note = Run-AndNote $CCompiler ($targetArgs + $cFlags + @("-c", $SourcePath, "-o", $objectPath)) "c-obj-failed"
    if (![string]::IsNullOrWhiteSpace($note)) { $notes.Add($note) }
    $note = Try-Disassemble $objectPath $disassemblyPath
    if (![string]::IsNullOrWhiteSpace($note) -and $note -ne "object-missing") { $notes.Add($note) }

    return New-ReportRow $BenchmarkId "c" $SourcePath $irPath $irPath $asmPath $objectPath $disassemblyPath ($notes -join "; ")
}

function Analyze-Rust {
    param(
        [string]$BenchmarkId,
        [string]$SourcePath,
        [string]$SafeName
    )

    $irPath = Join-Path $OutputDir "$SafeName.opt.ll"
    $asmPath = Join-Path $OutputDir "$SafeName.s"
    $objectPath = Join-Path $OutputDir "$SafeName.obj"
    $disassemblyPath = Join-Path $OutputDir "$SafeName.disasm.txt"
    $notes = New-Object System.Collections.Generic.List[string]

    if (!(Test-CommandExists $RustCompiler)) {
        $notes.Add("rust-compiler-not-found")
        return New-ReportRow $BenchmarkId "rust" $SourcePath $irPath $irPath $asmPath $objectPath $disassemblyPath ($notes -join "; ")
    }

    $crateArgs = @("--crate-name", (ConvertTo-RustCrateName $SourcePath))
    $targetArgs = @()
    if (![string]::IsNullOrWhiteSpace($Target)) {
        $targetArgs = @("--target", $Target)
    }

    $note = Run-AndNote $RustCompiler ($crateArgs + $SourcePath + $rustFlags + $targetArgs + @("--emit=llvm-ir", "-o", $irPath)) "rust-llvm-failed"
    if (![string]::IsNullOrWhiteSpace($note)) { $notes.Add($note) }
    if ($LlvmOnly) {
        $notes.Add("llvm-only")
        return New-ReportRow $BenchmarkId "rust" $SourcePath $irPath $irPath $asmPath $objectPath $disassemblyPath ($notes -join "; ")
    }

    $note = Run-AndNote $RustCompiler ($crateArgs + $SourcePath + $rustFlags + $targetArgs + @("--emit=asm", "-o", $asmPath)) "rust-asm-failed"
    if (![string]::IsNullOrWhiteSpace($note)) { $notes.Add($note) }
    $note = Run-AndNote $RustCompiler ($crateArgs + $SourcePath + $rustFlags + $targetArgs + @("--emit=obj", "-o", $objectPath)) "rust-obj-failed"
    if (![string]::IsNullOrWhiteSpace($note)) { $notes.Add($note) }
    $note = Try-Disassemble $objectPath $disassemblyPath
    if (![string]::IsNullOrWhiteSpace($note) -and $note -ne "object-missing") { $notes.Add($note) }

    return New-ReportRow $BenchmarkId "rust" $SourcePath $irPath $irPath $asmPath $objectPath $disassemblyPath ($notes -join "; ")
}

Ensure-Directory $OutputDir
Ensure-Directory (Split-Path -Parent $SummaryFile)

$selectedLanguages = @{}
foreach ($language in $Languages.Split(",")) {
    $normalized = $language.Trim().ToLowerInvariant()
    if (![string]::IsNullOrWhiteSpace($normalized)) {
        $selectedLanguages[$normalized] = $true
    }
}

$rows = New-Object System.Collections.Generic.List[object]
foreach ($benchmark in $Benchmarks) {
    $benchmarkId = Get-BenchmarkId $benchmark
    $starkPath = Get-SourcePath $benchmarkId ".stark"
    $cPath = Get-SourcePath $benchmarkId ".c"
    $rustPath = Get-SourcePath $benchmarkId ".rs"

    if ($selectedLanguages.ContainsKey("stark") -and (Test-Path -LiteralPath $starkPath)) {
        $rows.Add((Analyze-Stark $benchmarkId "stark" $starkPath (ConvertTo-SafeName "$benchmarkId-stark")))
    }

    if ($selectedLanguages.ContainsKey("c") -and (Test-Path -LiteralPath $cPath)) {
        $rows.Add((Analyze-C $benchmarkId $cPath (ConvertTo-SafeName "$benchmarkId-c")))
    }

    if ($selectedLanguages.ContainsKey("rust") -and (Test-Path -LiteralPath $rustPath)) {
        $rows.Add((Analyze-Rust $benchmarkId $rustPath (ConvertTo-SafeName "$benchmarkId-rust")))
    }
}

$rows | Export-Csv -LiteralPath $SummaryFile -NoTypeInformation
$rows | Format-Table benchmark, language, external_call_count, optnone_count, memory_intrinsic_count, vector_or_bitop_count, runtime_helper_call_count, notes -AutoSize
Write-Output "Codegen summary: $SummaryFile"
