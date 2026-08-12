function Invoke-ReleaseDownload {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Uri,

        [Parameter(Mandatory = $true)]
        [string] $OutFile,

        [ValidateRange(1, 10)]
        [int] $MaximumAttempts = 3
    )

    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        try {
            Invoke-WebRequest -Uri $Uri -OutFile $OutFile
            return
        } catch {
            Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
            if ($attempt -eq $MaximumAttempts) {
                throw
            }

            $delaySeconds = 2 * $attempt
            Write-Warning "Download attempt $attempt of $MaximumAttempts failed for '$Uri': $($_.Exception.Message). Retrying in $delaySeconds second(s)."
            Start-Sleep -Seconds $delaySeconds
        }
    }
}
