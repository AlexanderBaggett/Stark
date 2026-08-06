Set-StrictMode -Version Latest

function Get-LlvmSourceBuildExecutable {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "Pinned LLVM source builds require an explicit -$($Name)Path."
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    Assert-NoReparsePointPath -Path $fullPath -Label "LLVM source-build $Name"
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "LLVM source-build $Name executable '$fullPath' does not exist."
    }

    return $fullPath
}

function Get-LlvmSourceBuildToolVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [ValidateSet("cmake", "ninja")]
        [string] $Tool,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedVersion
    )

    $output = @(& $Path --version 2>&1)
    if ($LASTEXITCODE -ne 0 -or $output.Count -eq 0) {
        throw "Pinned LLVM source-build $Tool executable '$Path' could not report its version."
    }

    $actualVersion = if ($Tool -ceq "cmake") {
        $firstLine = ([string]$output[0]).Trim()
        if ($firstLine -cnotmatch '^cmake version ([0-9]+\.[0-9]+\.[0-9]+)$') {
            throw "Pinned CMake emitted unexpected version line '$firstLine'."
        }
        $Matches[1]
    } else {
        ([string]$output[0]).Trim()
    }

    if ($actualVersion -cne $ExpectedVersion) {
        throw "Pinned LLVM source-build $Tool reports '$actualVersion'; expected '$ExpectedVersion'."
    }

    return $actualVersion
}

function Invoke-LlvmSourceBuildProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FileName,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    Write-Host "[$([DateTimeOffset]::UtcNow.ToString('O'))] Starting $Label."
    & $FileName @Arguments 2>&1 | ForEach-Object {
        Write-Host ([string]$_)
    }
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Label failed with exit code $exitCode."
    }

    Write-Host "[$([DateTimeOffset]::UtcNow.ToString('O'))] Completed $Label."
}

function Get-XcrunValue {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $output = @(& /usr/bin/xcrun @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0 -or $output.Count -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$output[0])) {
        throw "Could not resolve $Label through xcrun."
    }

    return ([string]$output[0]).Trim()
}

function Resolve-LlvmSourceBuildApplePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [ValidateSet("Leaf", "Container")]
        [string] $PathType,

        [Parameter(Mandatory = $true)]
        [string] $Label,

        [switch] $PreserveInvocationPath
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    $invocationPath = [System.IO.Path]::GetFullPath($item.FullName)
    $linkTypeProperty = $item.PSObject.Properties["LinkType"]
    $linkType = if ($null -eq $linkTypeProperty) { "" } else { [string]$linkTypeProperty.Value }
    if (-not [string]::IsNullOrWhiteSpace($linkType)) {
        $resolved = $item.ResolveLinkTarget($true)
        if ($null -eq $resolved) {
            throw "$Label '$Path' is a dangling or cyclic link."
        }
        $item = $resolved
    }

    $resolvedPath = [System.IO.Path]::GetFullPath($item.FullName)
    Assert-NoReparsePointPath -Path $resolvedPath -Label $Label
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType $PathType)) {
        throw "$Label '$resolvedPath' does not have required path type '$PathType'."
    }

    if ($PreserveInvocationPath) {
        if ($PathType -cne "Leaf" -or
            -not (Test-Path -LiteralPath $invocationPath -PathType Leaf)) {
            throw "$Label invocation path '$invocationPath' is not an executable leaf."
        }
        return $invocationPath
    }

    return $resolvedPath
}

function Assert-LlvmSourceBuildCxxCompilerConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CMakeCachePath,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedCompilerPath
    )

    if (-not (Test-Path -LiteralPath $CMakeCachePath -PathType Leaf)) {
        throw "LLVM source configuration did not produce '$CMakeCachePath'."
    }

    $configuredCompilers = @(
        foreach ($line in Get-Content -LiteralPath $CMakeCachePath) {
            $match = [regex]::Match(
                [string]$line,
                '^CMAKE_CXX_COMPILER:[^=]+=(.+)$',
                [Text.RegularExpressions.RegexOptions]::CultureInvariant)
            if ($match.Success) {
                $match.Groups[1].Value
            }
        }
    )
    if ($configuredCompilers.Count -ne 1) {
        throw "LLVM source configuration must record exactly one CMAKE_CXX_COMPILER entry."
    }

    $configuredCompiler = [System.IO.Path]::GetFullPath([string]$configuredCompilers[0])
    $expectedCompiler = [System.IO.Path]::GetFullPath($ExpectedCompilerPath)
    if ($configuredCompiler -cne $expectedCompiler -or
        [System.IO.Path]::GetFileName($configuredCompiler) -cne "clang++") {
        throw "LLVM source configuration lost Apple Clang++ driver mode: expected '$expectedCompiler', found '$configuredCompiler'."
    }
}

function Get-LlvmSourceBuildAppleToolchainVersion {
    $developerDirectory = @(& /usr/bin/xcode-select -p 2>&1)
    if ($LASTEXITCODE -ne 0 -or $developerDirectory.Count -ne 1) {
        throw "The active Apple developer directory could not be identified."
    }

    if (-not ([string]$developerDirectory[0]).EndsWith(
        "$([System.IO.Path]::DirectorySeparatorChar)CommandLineTools",
        [StringComparison]::Ordinal)) {
        $xcodeVersion = @(& /usr/bin/xcodebuild -version 2>&1)
        if ($LASTEXITCODE -eq 0 -and $xcodeVersion.Count -gt 0) {
            return ($xcodeVersion -join "`n").Trim()
        }
    }

    $commandLineTools = @(& /usr/sbin/pkgutil --pkg-info=com.apple.pkg.CLTools_Executables 2>&1)
    if ($LASTEXITCODE -ne 0 -or $commandLineTools.Count -eq 0) {
        throw "Neither Xcode nor Command Line Tools could report source-build provenance."
    }

    return ("Command Line Tools`n" + ($commandLineTools -join "`n")).Trim()
}

function Invoke-LlvmPinnedSourceBuild {
    param(
        [Parameter(Mandatory = $true)]
        [object] $SourceBuild,

        [Parameter(Mandatory = $true)]
        [string] $ExtractedSourceRoot,

        [Parameter(Mandatory = $true)]
        [string] $WorkRoot,

        [Parameter(Mandatory = $true)]
        [string] $CMakePath,

        [Parameter(Mandatory = $true)]
        [string] $NinjaPath
    )

    if (-not $IsMacOS) {
        throw "The configured LLVM pinned source build requires a native macOS host."
    }

    $hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $expectedArchitecture = [string](Get-JsonProperty -Object $SourceBuild -Name "hostArchitecture")
    if ($hostArchitecture -cne $expectedArchitecture) {
        throw "The configured LLVM source build requires native '$expectedArchitecture'; this process is '$hostArchitecture'."
    }
    if ([string](Get-JsonProperty -Object $SourceBuild -Name "hostOperatingSystem") -cne "macos") {
        throw "The configured LLVM source build has an unsupported host operating system."
    }

    $cmakeExecutable = Get-LlvmSourceBuildExecutable -Path $CMakePath -Name "CMake"
    $ninjaExecutable = Get-LlvmSourceBuildExecutable -Path $NinjaPath -Name "Ninja"
    $cmakeVersion = Get-LlvmSourceBuildToolVersion `
        -Path $cmakeExecutable `
        -Tool "cmake" `
        -ExpectedVersion ([string](Get-JsonProperty -Object $SourceBuild -Name "cmakeVersion"))
    $ninjaVersion = Get-LlvmSourceBuildToolVersion `
        -Path $ninjaExecutable `
        -Tool "ninja" `
        -ExpectedVersion ([string](Get-JsonProperty -Object $SourceBuild -Name "ninjaVersion"))

    $sourceSubdirectory = [string](Get-JsonProperty -Object $SourceBuild -Name "sourceSubdirectory")
    Assert-SafeRelativePath -Path $sourceSubdirectory -Name "LLVM source subdirectory"
    $sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $ExtractedSourceRoot $sourceSubdirectory))
    if (-not (Test-IsSameOrDescendantPath -Path $sourceRoot -Root $ExtractedSourceRoot) -or
        -not (Test-Path -LiteralPath (Join-Path $sourceRoot "CMakeLists.txt") -PathType Leaf)) {
        throw "LLVM source archive does not contain the configured '$sourceSubdirectory/CMakeLists.txt'."
    }

    $buildRoot = Get-ContainedChildPath -Root $WorkRoot -Child "source-build" -Name "LLVM source-build directory"
    $installRoot = Get-ContainedChildPath -Root $WorkRoot -Child "source-install" -Name "LLVM source-install directory"
    New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $installRoot | Out-Null

    $clangPath = Resolve-LlvmSourceBuildApplePath `
        -Path (Get-XcrunValue -Arguments @("--find", "clang") -Label "Apple Clang") `
        -PathType "Leaf" `
        -Label "LLVM source-build Apple Clang"
    $clangxxPath = Resolve-LlvmSourceBuildApplePath `
        -Path (Get-XcrunValue -Arguments @("--find", "clang++") -Label "Apple Clang++") `
        -PathType "Leaf" `
        -Label "LLVM source-build Apple Clang++" `
        -PreserveInvocationPath
    $sdkPath = Resolve-LlvmSourceBuildApplePath `
        -Path (Get-XcrunValue -Arguments @("--sdk", "macosx", "--show-sdk-path") -Label "the macOS SDK") `
        -PathType "Container" `
        -Label "LLVM source-build macOS SDK"
    $sdkVersion = Get-XcrunValue -Arguments @("--sdk", "macosx", "--show-sdk-version") -Label "the macOS SDK version"
    $clangVersion = @(& $clangPath --version 2>&1)
    if ($LASTEXITCODE -ne 0 -or $clangVersion.Count -eq 0) {
        throw "Apple Clang could not report source-build provenance."
    }
    $xcodeVersion = Get-LlvmSourceBuildAppleToolchainVersion
    $clangSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $clangPath).Hash.ToLowerInvariant()
    $clangxxSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $clangxxPath).Hash.ToLowerInvariant()
    $qualifiedAppleToolchain = Get-JsonProperty -Object $SourceBuild -Name "qualifiedAppleToolchain"
    $actualAppleToolchain = [ordered]@{
        xcodeVersion = $xcodeVersion
        sdkVersion = $sdkVersion
        clangVersionLine = ([string]$clangVersion[0]).Trim()
        clangSha256 = $clangSha256
        clangxxSha256 = $clangxxSha256
    }
    foreach ($field in @("xcodeVersion", "sdkVersion", "clangVersionLine", "clangSha256", "clangxxSha256")) {
        $expected = [string](Get-JsonProperty -Object $qualifiedAppleToolchain -Name $field)
        $actual = [string]$actualAppleToolchain[$field]
        if (-not [string]::Equals($actual, $expected, [StringComparison]::Ordinal)) {
            throw "Apple source-build identity '$field' is '$actual'; expected the qualified value '$expected'."
        }
    }

    $projects = @(Get-ArrayValues -Value (Get-JsonProperty -Object $SourceBuild -Name "projects") |
        ForEach-Object { [string]$_ })
    $targets = @(Get-ArrayValues -Value (Get-JsonProperty -Object $SourceBuild -Name "targetsToBuild") |
        ForEach-Object { [string]$_ })
    $cmakeOptions = @(Get-ArrayValues -Value (Get-JsonProperty -Object $SourceBuild -Name "cmakeOptions") |
        ForEach-Object { [string]$_ })
    $linkJobs = [int](Get-JsonProperty -Object $SourceBuild -Name "parallelLinkJobs")
    $maxCompileJobs = [int](Get-JsonProperty -Object $SourceBuild -Name "maxParallelCompileJobs")
    $compileJobs = [Math]::Max(1, [Math]::Min([Environment]::ProcessorCount, $maxCompileJobs))
    $sourceDateEpoch = [int64](Get-JsonProperty -Object $SourceBuild -Name "sourceDateEpoch")
    $deploymentTarget = [string](Get-JsonProperty -Object $SourceBuild -Name "minimumDeploymentTarget")

    $prefixMapFlags = "-O3 -DNDEBUG -ffile-prefix-map=$WorkRoot=/stark-llvm-build -fdebug-prefix-map=$WorkRoot=/stark-llvm-build"
    $configureArguments = @(
        "-S", $sourceRoot,
        "-B", $buildRoot,
        "-G", ([string](Get-JsonProperty -Object $SourceBuild -Name "generator")),
        "-DCMAKE_MAKE_PROGRAM=$ninjaExecutable",
        "-DCMAKE_INSTALL_PREFIX=$installRoot",
        "-DCMAKE_C_COMPILER=$clangPath",
        "-DCMAKE_CXX_COMPILER=$clangxxPath",
        "-DCMAKE_OSX_SYSROOT=$sdkPath",
        "-DCMAKE_C_FLAGS_RELEASE=$prefixMapFlags",
        "-DCMAKE_CXX_FLAGS_RELEASE=$prefixMapFlags",
        "-DCMAKE_EXE_LINKER_FLAGS=-Wl,-no_uuid",
        "-DCMAKE_SHARED_LINKER_FLAGS=-Wl,-no_uuid",
        "-DLLVM_ENABLE_PROJECTS=$($projects -join ';')",
        "-DLLVM_TARGETS_TO_BUILD=$($targets -join ';')",
        "-DLLVM_PARALLEL_LINK_JOBS=$linkJobs"
    ) + $cmakeOptions

    $environmentNames = @("LANG", "LC_ALL", "MACOSX_DEPLOYMENT_TARGET", "SOURCE_DATE_EPOCH", "TZ", "ZERO_AR_DATE")
    $previousEnvironment = @{}
    foreach ($name in $environmentNames) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    }

    try {
        $env:LANG = "C"
        $env:LC_ALL = "C"
        $env:MACOSX_DEPLOYMENT_TARGET = $deploymentTarget
        $env:SOURCE_DATE_EPOCH = [string]$sourceDateEpoch
        $env:TZ = "UTC"
        $env:ZERO_AR_DATE = "1"

        Invoke-LlvmSourceBuildProcess `
            -FileName $cmakeExecutable `
            -Arguments $configureArguments `
            -Label "LLVM $($SourceBuild.configuration) source configuration"
        Assert-LlvmSourceBuildCxxCompilerConfiguration `
            -CMakeCachePath (Join-Path $buildRoot "CMakeCache.txt") `
            -ExpectedCompilerPath $clangxxPath
        Invoke-LlvmSourceBuildProcess `
            -FileName $cmakeExecutable `
            -Arguments @(
                "--build", $buildRoot,
                "--target", ([string](Get-JsonProperty -Object $SourceBuild -Name "buildTarget")),
                "--parallel", [string]$compileJobs) `
            -Label "LLVM $($SourceBuild.configuration) source build"
    } finally {
        foreach ($name in $environmentNames) {
            [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
        }
    }

    return [pscustomobject]@{
        PayloadRoot = $installRoot
        Evidence = [ordered]@{
            schemaVersion = 1
            recipeKind = "pinned-source-build"
            hostOperatingSystem = "macos"
            hostArchitecture = $hostArchitecture
            minimumDeploymentTarget = $deploymentTarget
            configuration = [string](Get-JsonProperty -Object $SourceBuild -Name "configuration")
            optimization = [string](Get-JsonProperty -Object $SourceBuild -Name "optimization")
            lto = [string](Get-JsonProperty -Object $SourceBuild -Name "lto")
            generator = [string](Get-JsonProperty -Object $SourceBuild -Name "generator")
            sourceSubdirectory = $sourceSubdirectory
            projects = $projects
            targetsToBuild = $targets
            buildTarget = [string](Get-JsonProperty -Object $SourceBuild -Name "buildTarget")
            cmakeOptions = $cmakeOptions
            sourceDateEpoch = $sourceDateEpoch
            compileJobs = $compileJobs
            parallelLinkJobs = $linkJobs
            buildTools = [ordered]@{
                cmake = [ordered]@{
                    version = $cmakeVersion
                    sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $cmakeExecutable).Hash.ToLowerInvariant()
                }
                ninja = [ordered]@{
                    version = $ninjaVersion
                    sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $ninjaExecutable).Hash.ToLowerInvariant()
                }
            }
            appleToolchain = [ordered]@{
                xcodeVersion = $xcodeVersion
                sdkVersion = $sdkVersion
                clangVersion = ($clangVersion -join "`n").Trim()
                clangSha256 = $clangSha256
                clangxxSha256 = $clangxxSha256
            }
        }
    }
}
