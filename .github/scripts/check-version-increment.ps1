# PowerShell script to check that the project version has been incremented over the latest release.
# A release is a git tag v<MAJOR.MINOR.PATCH> (created by the publish-container workflow after it pushes the image).
# Tags must be fetched (actions/checkout with fetch-depth: 0). No release tag yet means the check passes.
param(
    [string]$ProjectFile = "Pixelbadger.Toolkit.Rag/Pixelbadger.Toolkit.Rag.csproj",
    [string]$TagPrefix = "v"
)

# Get the current version from the project file
$currentVersionMatch = Select-String -Path $ProjectFile -Pattern '<Version>(.+)</Version>'
if (-not $currentVersionMatch) {
    Write-Error "Could not find Version element in $ProjectFile"
    exit 1
}
$currentVersion = $currentVersionMatch.Matches[0].Groups[1].Value
Write-Host "Current version in project file: $currentVersion"

# Collect released versions from the git tags (only plain MAJOR.MINOR.PATCH ones count)
$tags = @(git tag --list "$TagPrefix*")
if ($LASTEXITCODE -ne 0) {
    Write-Error "git tag failed (exit code $LASTEXITCODE)"
    exit 1
}

$released = @()
foreach ($tag in $tags) {
    $text = $tag.Substring($TagPrefix.Length)
    if ($text -match '^\d+\.\d+\.\d+$') {
        $released += [System.Version]$text
    }
}

if ($released.Count -eq 0) {
    Write-Host "No release tags ($TagPrefix*) found, assuming this is the first release"
    Write-Host "Version check passed: $currentVersion"
    exit 0
}

$latest = ($released | Sort-Object -Descending | Select-Object -First 1)
Write-Host "Latest released version (git tag): $latest"

$current = [System.Version]$currentVersion
if ($current -gt $latest) {
    Write-Host "Version check passed: $currentVersion > $latest"
    exit 0
}

Write-Error "Version check failed: current version ($currentVersion) must be greater than the latest released version ($latest)"
exit 1
