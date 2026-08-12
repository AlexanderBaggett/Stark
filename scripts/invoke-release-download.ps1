function Get-GitHubReleaseAssetApiUri {
    param(
        [Parameter(Mandatory = $true)]
        [string] $BrowserDownloadUri
    )

    $match = [regex]::Match(
        $BrowserDownloadUri,
        '^https://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repository>[A-Za-z0-9_.-]+)/releases/download/(?<tag>[^/]+)/(?<asset>[^/?#]+)$',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        return $null
    }

    $owner = $match.Groups['owner'].Value
    $repository = $match.Groups['repository'].Value
    $tag = [Uri]::UnescapeDataString($match.Groups['tag'].Value)
    $assetName = [Uri]::UnescapeDataString($match.Groups['asset'].Value)
    $headers = @{
        Accept = 'application/vnd.github+json'
        'User-Agent' = 'Stark-release-builder'
    }
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        $headers.Authorization = "Bearer $env:GITHUB_TOKEN"
    }

    $releaseUri = "https://api.github.com/repos/$owner/$repository/releases/tags/$([Uri]::EscapeDataString($tag))"
    $release = Invoke-RestMethod -Uri $releaseUri -Headers $headers -MaximumRedirection 10
    $matchingAssets = @($release.assets | Where-Object { [string]$_.name -ceq $assetName })
    if ($matchingAssets.Count -ne 1) {
        throw "GitHub release '$owner/$repository@$tag' exposes $($matchingAssets.Count) assets named '$assetName'; expected exactly one."
    }

    $assetId = [int64]$matchingAssets[0].id
    if ($assetId -le 0) {
        throw "GitHub release asset '$owner/$repository@$tag/$assetName' has invalid id '$assetId'."
    }
    return "https://api.github.com/repos/$owner/$repository/releases/assets/$assetId"
}

function Invoke-ReleaseDownload {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Uri,

        [Parameter(Mandatory = $true)]
        [string] $OutFile,

        [ValidateRange(1, 10)]
        [int] $MaximumAttempts = 3
    )

    $downloadUri = $Uri
    $downloadHeaders = @{}
    $usingGitHubAssetApi = $false
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        try {
            Invoke-WebRequest -Uri $downloadUri -Headers $downloadHeaders -OutFile $OutFile -MaximumRedirection 10
            return
        } catch {
            $downloadFailure = $_.Exception.Message
            Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
            if ($attempt -eq $MaximumAttempts) {
                throw
            }

            $switchedToGitHubAssetApi = $false
            if (-not $usingGitHubAssetApi) {
                try {
                    $assetApiUri = Get-GitHubReleaseAssetApiUri -BrowserDownloadUri $Uri
                    if (-not [string]::IsNullOrWhiteSpace($assetApiUri)) {
                        $usingGitHubAssetApi = $true
                        $switchedToGitHubAssetApi = $true
                        $downloadUri = $assetApiUri
                        $downloadHeaders = @{
                            Accept = 'application/octet-stream'
                            'User-Agent' = 'Stark-release-builder'
                        }
                        if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
                            $downloadHeaders.Authorization = "Bearer $env:GITHUB_TOKEN"
                        }
                    }
                } catch {
                    Write-Warning "GitHub release-assets API resolution also failed for '$Uri': $($_.Exception.Message)."
                }
            }

            $delaySeconds = 2 * $attempt
            if ($switchedToGitHubAssetApi) {
                Write-Warning "Direct GitHub release download failed for '$Uri': $downloadFailure. Retrying through GitHub's release-assets API in $delaySeconds second(s)."
            } else {
                Write-Warning "Download attempt $attempt of $MaximumAttempts failed for '$Uri': $downloadFailure. Retrying in $delaySeconds second(s)."
            }
            Start-Sleep -Seconds $delaySeconds
        }
    }
}
