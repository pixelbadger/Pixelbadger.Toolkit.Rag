# PowerShell script to check if package version has been incremented against NuGet.org
param(
    [string]$ProjectFile = "Pixelbadger.Toolkit.csproj",
    [string]$PackageId = "Pixelbadger.Toolkit"
)

# Get the current version from the project file
$currentVersionMatch = Select-String -Path $ProjectFile -Pattern '<Version>(.+)</Version>'
if (-not $currentVersionMatch) {
    Write-Error "Could not find Version element in $ProjectFile"
    exit 1
}
$currentVersion = $currentVersionMatch.Matches[0].Groups[1].Value
Write-Host "Current version in project file: $currentVersion"

# Query NuGet.org API for the latest published version
$nugetApiUrl = "https://api.nuget.org/v3-flatcontainer/$($PackageId.ToLower())/index.json"
Write-Host "Checking NuGet.org for package: $PackageId"

try {
    $response = Invoke-RestMethod -Uri $nugetApiUrl -ErrorAction Stop
    $publishedVersions = $response.versions

    if (-not $publishedVersions -or $publishedVersions.Count -eq 0) {
        Write-Host "No published versions found on NuGet.org, assuming this is the first release"
        Write-Host "Version check passed: $currentVersion"
        exit 0
    }

    # Get the latest published version (last in the array)
    $latestPublishedVersion = $publishedVersions[-1]
    Write-Host "Latest published version on NuGet.org: $latestPublishedVersion"

    # Compare versions using System.Version
    $current = [System.Version]$currentVersion
    $latest = [System.Version]$latestPublishedVersion

    if ($current -gt $latest) {
        Write-Host "✅ Version check passed: $currentVersion > $latestPublishedVersion"
        exit 0
    } else {
        Write-Error "❌ Version check failed: Current version ($currentVersion) must be greater than latest published version ($latestPublishedVersion)"
        exit 1
    }
} catch {
    if ($_.Exception.Response.StatusCode -eq 404) {
        Write-Host "Package not found on NuGet.org, assuming this is the first release"
        Write-Host "Version check passed: $currentVersion"
        exit 0
    } else {
        Write-Error "Error querying NuGet.org: $_"
        exit 1
    }
}