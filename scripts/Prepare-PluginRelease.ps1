[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InternalName
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

$dotnetCandidates = @(
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    (Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'),
    'E:/01_Dev/SDKs/dotnet10/dotnet.exe'
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
$dotnet = $dotnetCandidates | Where-Object {
    (& $_ --list-sdks 2>$null) -match '^10\.0\.'
} | Select-Object -First 1
if (!$dotnet) {
    throw 'A .NET 10 SDK could not be found.'
}

$plugins = @{
    CrescentCompass = @{
        Project = 'src/CrescentCompass/CrescentCompass.csproj'
        TestProject = 'tests/CrescentCompass.Core.Tests/CrescentCompass.Core.Tests.csproj'
        PackedManifest = 'src/CrescentCompass/bin/Release/CrescentCompass/CrescentCompass.json'
        PackedZip = 'src/CrescentCompass/bin/Release/CrescentCompass/latest.zip'
    }
}

if (!$plugins.ContainsKey($InternalName)) {
    throw "Unknown plugin '$InternalName'. Add it to the plugin registry in this script first."
}

$plugin = $plugins[$InternalName]
$projectPath = Join-Path $repositoryRoot $plugin.Project
[xml]$project = Get-Content -Raw $projectPath
$version = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Project $($plugin.Project) does not define Version."
}

Push-Location $repositoryRoot
try {
    $testProjectPath = Join-Path $repositoryRoot $plugin.TestProject
    if (Test-Path $testProjectPath) {
        & $dotnet run -c Release --project $plugin.TestProject
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }

    & $dotnet build $plugin.Project -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    $packedManifestPath = Join-Path $repositoryRoot $plugin.PackedManifest
    $packedZipPath = Join-Path $repositoryRoot $plugin.PackedZip
    if (!(Test-Path $packedManifestPath) -or !(Test-Path $packedZipPath)) {
        throw 'Dalamud packager output is missing.'
    }

    $manifestPath = Join-Path $repositoryRoot "plugins/$InternalName/manifest.json"
    $manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
    $packedManifest = Get-Content -Raw $packedManifestPath | ConvertFrom-Json
    $releaseTag = "$InternalName-v$version"
    $downloadUrl = "https://github.com/G-Yoka/DalamudPluginsCN/releases/download/$releaseTag/$InternalName.zip"

    $manifest.AssemblyVersion = $packedManifest.AssemblyVersion
    $manifest.DalamudApiLevel = $packedManifest.DalamudApiLevel
    $manifest.DownloadLinkInstall = $downloadUrl
    $manifest.DownloadLinkUpdate = $downloadUrl
    $manifest.DownloadLinkTesting = $downloadUrl
    if ($null -eq $manifest.PSObject.Properties['LastUpdate']) {
        $manifest | Add-Member -NotePropertyName LastUpdate -NotePropertyValue 0
    }
    $manifest.LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $manifestPath

    $catalogPath = Join-Path $repositoryRoot 'pluginmaster.json'
    $catalog = @(Get-Content -Raw $catalogPath | ConvertFrom-Json)
    $otherPlugins = @($catalog | Where-Object InternalName -ne $InternalName)
    ConvertTo-Json -InputObject @($otherPlugins + $manifest) -Depth 10 | Set-Content -Encoding utf8 $catalogPath

    $releaseDirectory = Join-Path $repositoryRoot "artifacts/releases/$releaseTag"
    New-Item -ItemType Directory -Force $releaseDirectory | Out-Null
    Copy-Item $packedZipPath (Join-Path $releaseDirectory "$InternalName.zip") -Force

    Write-Host "Prepared $InternalName $version"
    Write-Host "Package: $releaseDirectory/$InternalName.zip"
    Write-Host "Release tag: $releaseTag"
}
finally {
    Pop-Location
}
