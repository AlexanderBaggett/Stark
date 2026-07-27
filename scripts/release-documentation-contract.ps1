function Get-ReleaseDocumentationRequiredProperty {
    param(
        [Parameter(Mandatory = $true)] [object] $Object,
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [string] $Context
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "$Context is missing required property '$Name'."
    }
    return $property.Value
}

function Get-ReleaseDocumentationRequiredString {
    param(
        [Parameter(Mandatory = $true)] [object] $Object,
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [string] $Context
    )

    $value = [string](Get-ReleaseDocumentationRequiredProperty -Object $Object -Name $Name -Context $Context)
    if ([string]::IsNullOrWhiteSpace($value) -or $value -match '[\x00-\x1f]') {
        throw "$Context property '$Name' must be a nonempty single-line string."
    }
    return $value
}

function Get-ReleaseDocumentationOptionalString {
    param(
        [Parameter(Mandatory = $true)] [object] $Object,
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [string] $Context
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return ""
    }
    $value = [string]$property.Value
    if ($value -match '[\x00-\x1f]') {
        throw "$Context property '$Name' must be a single-line string."
    }
    return $value
}

function ConvertTo-ReleaseDocumentationNormalizedText {
    param([Parameter(Mandatory = $true)] [string] $Text)

    return $Text.Replace("`r`n", "`n").TrimEnd([char[]]"`r`n")
}

function New-ReleaseDocumentationQuickStartSteps {
    param([Parameter(Mandatory = $true)] [string] $TargetTriple)

    if ([string]::IsNullOrWhiteSpace($TargetTriple) -or
        $TargetTriple -notmatch '^[A-Za-z0-9][A-Za-z0-9._+\-]*$') {
        throw "Release quick-start target triple '$TargetTriple' is not a safe command argument."
    }

    return @(
        [pscustomobject][ordered]@{
            id = "doctor"
            workingDirectory = "."
            arguments = @("doctor", "--strict")
            expectedStdoutContains = ""
        },
        [pscustomobject][ordered]@{
            id = "check-hello"
            workingDirectory = "."
            arguments = @("examples/hello.stark", "--check")
            expectedStdoutContains = ""
        },
        [pscustomobject][ordered]@{
            id = "build-hello"
            workingDirectory = "examples/hello"
            arguments = @("build", "--target", $TargetTriple)
            expectedStdoutContains = ""
        },
        [pscustomobject][ordered]@{
            id = "run-hello"
            workingDirectory = "examples/hello"
            arguments = @("run", "--target", $TargetTriple)
            expectedStdoutContains = "Hello, World!"
        }
    )
}

function Assert-ReleaseDocumentationQuickStartSteps {
    param(
        [Parameter(Mandatory = $true)] [object[]] $ActualValue,
        [Parameter(Mandatory = $true)] [string] $TargetTriple
    )

    $expectedSteps = @(New-ReleaseDocumentationQuickStartSteps -TargetTriple $TargetTriple)
    $steps = @($ActualValue)
    if ($steps.Count -ne $expectedSteps.Count) {
        throw "Release command contract must contain exactly $($expectedSteps.Count) quick-start steps."
    }

    for ($index = 0; $index -lt $expectedSteps.Count; $index++) {
        $step = $steps[$index]
        $expected = $expectedSteps[$index]
        $stepContext = "release command step $index"
        $id = Get-ReleaseDocumentationRequiredString -Object $step -Name "id" -Context $stepContext
        $workingDirectory = Get-ReleaseDocumentationRequiredString -Object $step -Name "workingDirectory" -Context $stepContext
        $arguments = Get-ReleaseDocumentationRequiredProperty -Object $step -Name "arguments" -Context $stepContext
        $expectedOutput = Get-ReleaseDocumentationOptionalString -Object $step -Name "expectedStdoutContains" -Context $stepContext
        if ($id -cne [string]$expected.id -or
            $workingDirectory -cne [string]$expected.workingDirectory -or
            $expectedOutput -cne [string]$expected.expectedStdoutContains) {
            throw "$stepContext does not match the '$($expected.id)' quick-start operation."
        }
        Assert-ReleaseDocumentationStringArray `
            -ActualValue $arguments `
            -Expected @($expected.arguments) `
            -Context "$stepContext arguments"
    }
}

function ConvertTo-ReleaseDocumentationQuickStartMarkdown {
    param(
        [Parameter(Mandatory = $true)] [object[]] $Steps,
        [Parameter(Mandatory = $true)] [string] $TargetTriple,
        [Parameter(Mandatory = $true)] [string] $OperatingSystem,
        [Parameter(Mandatory = $true)] [string] $CompilerRelativePath
    )

    if ($OperatingSystem -notin @("windows", "linux", "macos")) {
        throw "Release quick-start operating system '$OperatingSystem' is unsupported."
    }
    $expectedCompiler = if ($OperatingSystem -eq "windows") { "bin/stark.exe" } else { "bin/stark" }
    if ($CompilerRelativePath -cne $expectedCompiler) {
        throw "Release quick-start compiler '$CompilerRelativePath' does not match '$expectedCompiler'."
    }

    $quickStartSteps = @($Steps)
    Assert-ReleaseDocumentationQuickStartSteps `
        -ActualValue $quickStartSteps `
        -TargetTriple $TargetTriple
    $commandLines = @($quickStartSteps | ForEach-Object {
        "stark " + (@($_.arguments | ForEach-Object { [string]$_ }) -join " ")
    })

    if ($OperatingSystem -eq "windows") {
        $projectDirectory = ([string]$quickStartSteps[2].workingDirectory).Replace('/', '\')
        $lines = @(
            '```powershell',
            '$env:Path = "$PWD\bin;$env:Path"',
            $commandLines[0],
            $commandLines[1],
            "Push-Location .\$projectDirectory",
            'try {',
            "    $($commandLines[2])",
            "    $($commandLines[3])",
            '} finally {',
            '    Pop-Location',
            '}',
            '```'
        )
    } else {
        $projectDirectory = [string]$quickStartSteps[2].workingDirectory
        $lines = @(
            '```sh',
            'export PATH="$PWD/bin:$PATH"',
            $commandLines[0],
            $commandLines[1],
            '(',
            "  cd $projectDirectory",
            "  $($commandLines[2])",
            "  $($commandLines[3])",
            ')',
            '```'
        )
    }

    return ConvertTo-ReleaseDocumentationNormalizedText -Text ($lines -join "`n")
}

function Resolve-ReleaseDocumentationContractPath {
    param(
        [Parameter(Mandatory = $true)] [string] $SdkRoot,
        [Parameter(Mandatory = $true)] [string] $RelativePath,
        [Parameter(Mandatory = $true)] [string] $Context,
        [switch] $AllowRoot
    )

    $root = [System.IO.Path]::GetFullPath($SdkRoot)
    if ($AllowRoot -and [string]::Equals($RelativePath, ".", [StringComparison]::Ordinal)) {
        return $root
    }
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [System.IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains('\') -or
        $RelativePath.Contains(':') -or
        $RelativePath.IndexOf([char]0) -ge 0 -or
        $RelativePath -match '(^|/)\.?\.(/|$)') {
        throw "$Context '$RelativePath' is not a safe canonical SDK-relative path."
    }

    $candidate = [System.IO.Path]::GetFullPath((Join-Path $root $RelativePath))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $rootWithSeparator = [System.IO.Path]::TrimEndingDirectorySeparator($root) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($rootWithSeparator, $comparison)) {
        throw "$Context '$RelativePath' escapes SDK root '$root'."
    }
    return $candidate
}

function Assert-ReleaseDocumentationStringArray {
    param(
        [Parameter(Mandatory = $true)] [object[]] $ActualValue,
        [Parameter(Mandatory = $true)] [string[]] $Expected,
        [Parameter(Mandatory = $true)] [string] $Context
    )

    $actual = @($ActualValue)
    if ($actual.Count -ne $Expected.Count) {
        throw "$Context has $($actual.Count) value(s); expected $($Expected.Count)."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if (-not [string]::Equals([string]$actual[$index], $Expected[$index], [StringComparison]::Ordinal)) {
            throw "$Context value $index is '$($actual[$index])'; expected '$($Expected[$index])'."
        }
    }
}

function Get-ReleaseDocumentationCommandContract {
    param(
        [Parameter(Mandatory = $true)] [string] $SdkRoot,
        [string] $ExpectedTargetTriple = ""
    )

    $root = [System.IO.Path]::GetFullPath($SdkRoot)
    $contractPath = Join-Path $root "release-commands.json"
    if (-not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
        throw "Release SDK is missing release-commands.json."
    }
    try {
        $contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
    } catch {
        throw "Release command contract '$contractPath' is invalid JSON: $($_.Exception.Message)"
    }

    $schemaVersion = [int](Get-ReleaseDocumentationRequiredProperty -Object $contract -Name "schemaVersion" -Context "release command contract")
    $kind = Get-ReleaseDocumentationRequiredString -Object $contract -Name "kind" -Context "release command contract"
    if ($schemaVersion -ne 1 -or $kind -cne "stark-release-quick-start") {
        throw "release-commands.json must use schemaVersion 1 and kind 'stark-release-quick-start'."
    }

    $targetId = Get-ReleaseDocumentationRequiredString -Object $contract -Name "targetId" -Context "release command contract"
    $targetTriple = Get-ReleaseDocumentationRequiredString -Object $contract -Name "targetTriple" -Context "release command contract"
    $operatingSystem = Get-ReleaseDocumentationRequiredString -Object $contract -Name "operatingSystem" -Context "release command contract"
    if ($operatingSystem -notin @("windows", "linux", "macos")) {
        throw "release command contract operatingSystem '$operatingSystem' is unsupported."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedTargetTriple) -and
        -not [string]::Equals($targetTriple, $ExpectedTargetTriple, [StringComparison]::Ordinal)) {
        throw "Release command contract target '$targetTriple' does not match expected target '$ExpectedTargetTriple'."
    }
    if ($targetId -notmatch '^[a-z][a-z0-9-]*$') {
        throw "Release command contract targetId '$targetId' is invalid."
    }

    $paths = Get-ReleaseDocumentationRequiredProperty -Object $contract -Name "paths" -Context "release command contract"
    $compilerRelative = Get-ReleaseDocumentationRequiredString -Object $paths -Name "compiler" -Context "release command contract paths"
    $helloSourceRelative = Get-ReleaseDocumentationRequiredString -Object $paths -Name "helloSource" -Context "release command contract paths"
    $helloProjectRelative = Get-ReleaseDocumentationRequiredString -Object $paths -Name "helloProject" -Context "release command contract paths"
    $expectedCompiler = if ($operatingSystem -eq "windows") { "bin/stark.exe" } else { "bin/stark" }
    if ($compilerRelative -cne $expectedCompiler -or
        $helloSourceRelative -cne "examples/hello.stark" -or
        $helloProjectRelative -cne "examples/hello") {
        throw "Release command contract paths do not match the relocatable SDK layout."
    }

    $documentation = Get-ReleaseDocumentationRequiredProperty -Object $contract -Name "documentation" -Context "release command contract"
    $readmeRelative = Get-ReleaseDocumentationRequiredString -Object $documentation -Name "readme" -Context "release command contract documentation"
    $installRelative = Get-ReleaseDocumentationRequiredString -Object $documentation -Name "install" -Context "release command contract documentation"
    $marker = Get-ReleaseDocumentationRequiredString -Object $documentation -Name "quickStartMarker" -Context "release command contract documentation"
    $quickStartMarkdown = [string](Get-ReleaseDocumentationRequiredProperty -Object $documentation -Name "quickStartMarkdown" -Context "release command contract documentation")
    if ($readmeRelative -cne "README.md" -or $installRelative -cne "INSTALL.md" -or
        $marker -cne "stark-release-quick-start-v1" -or [string]::IsNullOrWhiteSpace($quickStartMarkdown) -or
        $quickStartMarkdown.Contains("`r")) {
        throw "Release command contract documentation metadata is invalid."
    }

    $steps = @(Get-ReleaseDocumentationRequiredProperty -Object $contract -Name "steps" -Context "release command contract")
    Assert-ReleaseDocumentationQuickStartSteps -ActualValue $steps -TargetTriple $targetTriple
    $renderedQuickStartMarkdown = ConvertTo-ReleaseDocumentationQuickStartMarkdown `
        -Steps $steps `
        -TargetTriple $targetTriple `
        -OperatingSystem $operatingSystem `
        -CompilerRelativePath $compilerRelative
    if (-not [string]::Equals(
        (ConvertTo-ReleaseDocumentationNormalizedText -Text $quickStartMarkdown),
        $renderedQuickStartMarkdown,
        [StringComparison]::Ordinal)) {
        throw "release-commands.json quickStartMarkdown is not the canonical rendering of its executable steps."
    }
    foreach ($step in $steps) {
        $stepContext = "release command '$($step.id)'"
        $workingDirectory = Get-ReleaseDocumentationRequiredString `
            -Object $step `
            -Name "workingDirectory" `
            -Context $stepContext
        [void](Resolve-ReleaseDocumentationContractPath -SdkRoot $root -RelativePath $workingDirectory -Context "$stepContext workingDirectory" -AllowRoot)
    }

    foreach ($requiredPath in @(
        @{ Relative = $compilerRelative; Kind = "Leaf"; Context = "release compiler" },
        @{ Relative = $helloSourceRelative; Kind = "Leaf"; Context = "shipped hello source" },
        @{ Relative = $helloProjectRelative; Kind = "Container"; Context = "shipped hello project" },
        @{ Relative = $readmeRelative; Kind = "Leaf"; Context = "release README" },
        @{ Relative = $installRelative; Kind = "Leaf"; Context = "release install manual" }
    )) {
        $resolved = Resolve-ReleaseDocumentationContractPath -SdkRoot $root -RelativePath $requiredPath.Relative -Context $requiredPath.Context
        if (-not (Test-Path -LiteralPath $resolved -PathType $requiredPath.Kind)) {
            throw "$($requiredPath.Context) '$($requiredPath.Relative)' is missing from the SDK."
        }
    }

    return $contract
}

function Get-ReleaseDocumentationMarkedBlock {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Marker
    )

    $text = (Get-Content -LiteralPath $Path -Raw).Replace("`r`n", "`n")
    $start = "<!-- ${Marker}:start -->"
    $end = "<!-- ${Marker}:end -->"
    $pattern = "(?ms)^" + [regex]::Escape($start) + "`n(?<body>.*?)`n" + [regex]::Escape($end) + "\s*$"
    $matches = [regex]::Matches($text, $pattern)
    if ($matches.Count -ne 1) {
        throw "Release document '$Path' must contain exactly one '$Marker' command block."
    }
    return (ConvertTo-ReleaseDocumentationNormalizedText -Text $matches[0].Groups["body"].Value)
}

function Assert-ReleaseDocumentationCommandContract {
    param(
        [Parameter(Mandatory = $true)] [string] $SdkRoot,
        [string] $ExpectedTargetTriple = ""
    )

    $contract = Get-ReleaseDocumentationCommandContract -SdkRoot $SdkRoot -ExpectedTargetTriple $ExpectedTargetTriple
    $documentation = Get-ReleaseDocumentationRequiredProperty -Object $contract -Name "documentation" -Context "release command contract"
    $marker = [string]$documentation.quickStartMarker
    $expectedMarkdown = ConvertTo-ReleaseDocumentationNormalizedText -Text ([string]$documentation.quickStartMarkdown)
    foreach ($propertyName in @("readme", "install")) {
        $relative = [string]$documentation.$propertyName
        $path = Resolve-ReleaseDocumentationContractPath -SdkRoot $SdkRoot -RelativePath $relative -Context "release document"
        $actualMarkdown = Get-ReleaseDocumentationMarkedBlock -Path $path -Marker $marker
        if (-not [string]::Equals($actualMarkdown, $expectedMarkdown, [StringComparison]::Ordinal)) {
            throw "Release document '$relative' quick-start commands drift from release-commands.json."
        }
    }
    return $contract
}

function Copy-ReleaseDocumentationQuickStartInputs {
    param(
        [Parameter(Mandatory = $true)] [string] $SdkRoot,
        [Parameter(Mandatory = $true)] [string] $DestinationRoot
    )

    $contract = Assert-ReleaseDocumentationCommandContract -SdkRoot $SdkRoot
    $destination = [System.IO.Path]::GetFullPath($DestinationRoot)
    if (Test-Path -LiteralPath $destination) {
        throw "Documented-command execution root '$destination' already exists."
    }

    $sourcePath = Resolve-ReleaseDocumentationContractPath `
        -SdkRoot $SdkRoot `
        -RelativePath ([string]$contract.paths.helloSource) `
        -Context "shipped hello source"
    $projectPath = Resolve-ReleaseDocumentationContractPath `
        -SdkRoot $SdkRoot `
        -RelativePath ([string]$contract.paths.helloProject) `
        -Context "shipped hello project"
    $destinationSource = Join-Path $destination ([string]$contract.paths.helloSource)
    $destinationProject = Join-Path $destination ([string]$contract.paths.helloProject)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationSource), $destinationProject | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $destinationSource
    foreach ($entry in (Get-ChildItem -LiteralPath $projectPath -Force)) {
        Copy-Item -LiteralPath $entry.FullName -Destination $destinationProject -Recurse
    }
    return $destination
}

function Invoke-ReleaseDocumentationCommandContract {
    param(
        [Parameter(Mandatory = $true)] [string] $SdkRoot,
        [string] $ExpectedTargetTriple = "",
        [string] $ExecutionRoot = "",
        [Parameter(Mandatory = $true)] [scriptblock] $CompilerInvoker
    )

    $contract = Assert-ReleaseDocumentationCommandContract -SdkRoot $SdkRoot -ExpectedTargetTriple $ExpectedTargetTriple
    $commandRoot = if ([string]::IsNullOrWhiteSpace($ExecutionRoot)) {
        [System.IO.Path]::GetFullPath($SdkRoot)
    } else {
        [System.IO.Path]::GetFullPath($ExecutionRoot)
    }
    foreach ($requiredRelative in @([string]$contract.paths.helloSource, [string]$contract.paths.helloProject)) {
        $requiredPath = Resolve-ReleaseDocumentationContractPath `
            -SdkRoot $commandRoot `
            -RelativePath $requiredRelative `
            -Context "documented-command execution input"
        if (-not (Test-Path -LiteralPath $requiredPath)) {
            throw "Documented-command execution input '$requiredRelative' is missing from '$commandRoot'."
        }
    }
    foreach ($step in @($contract.steps)) {
        $workingDirectory = Resolve-ReleaseDocumentationContractPath `
            -SdkRoot $commandRoot `
            -RelativePath ([string]$step.workingDirectory) `
            -Context "release command '$($step.id)' workingDirectory" `
            -AllowRoot
        $arguments = @($step.arguments | ForEach-Object { [string]$_ })
        Write-Host "Executing documented release command '$($step.id)' from '$workingDirectory'."
        $results = @(& $CompilerInvoker -Arguments $arguments -WorkingDirectory $workingDirectory)
        $expectedOutput = Get-ReleaseDocumentationOptionalString -Object $step -Name "expectedStdoutContains" -Context "release command '$($step.id)'"
        if (-not [string]::IsNullOrEmpty($expectedOutput)) {
            $result = if ($results.Count -eq 0) { $null } else { $results[-1] }
            $stdoutProperty = if ($null -eq $result) { $null } else { $result.PSObject.Properties["Stdout"] }
            if ($null -eq $stdoutProperty -or
                -not ([string]$stdoutProperty.Value).Contains($expectedOutput, [StringComparison]::Ordinal)) {
                throw "Documented release command '$($step.id)' did not produce expected output '$expectedOutput'."
            }
        }
    }
    return $contract
}
