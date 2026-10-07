[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InternalName,

    [ValidateSet('Stable', 'Testing')]
    [string]$Channel = 'Stable'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

$dotnetCandidates = @(
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }),
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
    AvariceCN = @{
        Project = 'src/AvariceCN/AvariceCN.csproj'
        TestProject = 'tests/AvariceCN.Tests/AvariceCN.Tests.csproj'
        PackedManifest = 'artifacts/build/AvariceCN/Release/AvariceCN/AvariceCN.json'
        PackedZip = 'artifacts/build/AvariceCN/Release/AvariceCN/latest.zip'
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

function Set-JsonProperty {
    param(
        [Parameter(Mandatory)] [psobject]$Object,
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] $Value
    )

    if ($null -eq $Object.PSObject.Properties[$Name]) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
    else {
        $Object.$Name = $Value
    }
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
    if ($null -eq $manifest.PSObject.Properties['Changelog'] -or
        [string]::IsNullOrWhiteSpace([string]$manifest.Changelog)) {
        throw "plugins/$InternalName/manifest.json must define a non-empty Changelog before release."
    }
    $packedManifest = Get-Content -Raw $packedManifestPath | ConvertFrom-Json
    $releaseTag = if ($Channel -eq 'Testing') {
        "$InternalName-v$version-testing"
    }
    else {
        "$InternalName-v$version"
    }
    $downloadUrl = "https://github.com/G-Yoka/DalamudPluginsCN/releases/download/$releaseTag/$InternalName.zip"

    if ($Channel -eq 'Testing') {
        Set-JsonProperty $manifest 'TestingAssemblyVersion' $packedManifest.AssemblyVersion
        Set-JsonProperty $manifest 'TestingDalamudApiLevel' $packedManifest.DalamudApiLevel
        Set-JsonProperty $manifest 'TestingChangelog' $packedManifest.Changelog
        Set-JsonProperty $manifest 'DownloadLinkTesting' $downloadUrl
    }
    else {
        Set-JsonProperty $manifest 'AssemblyVersion' $packedManifest.AssemblyVersion
        Set-JsonProperty $manifest 'DalamudApiLevel' $packedManifest.DalamudApiLevel
        Set-JsonProperty $manifest 'Changelog' $packedManifest.Changelog
        Set-JsonProperty $manifest 'TestingAssemblyVersion' $packedManifest.AssemblyVersion
        Set-JsonProperty $manifest 'TestingDalamudApiLevel' $packedManifest.DalamudApiLevel
        Set-JsonProperty $manifest 'TestingChangelog' $packedManifest.Changelog
        Set-JsonProperty $manifest 'DownloadLinkInstall' $downloadUrl
        Set-JsonProperty $manifest 'DownloadLinkUpdate' $downloadUrl
        Set-JsonProperty $manifest 'DownloadLinkTesting' $downloadUrl
    }
    Set-JsonProperty $manifest 'LastUpdate' ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $manifestPath

    $catalogPath = Join-Path $repositoryRoot 'pluginmaster.json'
    $catalog = @(Get-Content -Raw $catalogPath | ConvertFrom-Json)
    $otherPlugins = @($catalog | Where-Object InternalName -ne $InternalName)
    ConvertTo-Json -InputObject @($otherPlugins + $manifest) -Depth 10 | Set-Content -Encoding utf8 $catalogPath

    $releaseDirectory = Join-Path $repositoryRoot "artifacts/releases/$releaseTag"
    New-Item -ItemType Directory -Force $releaseDirectory | Out-Null
    Copy-Item $packedZipPath (Join-Path $releaseDirectory "$InternalName.zip") -Force

    Write-Host "Prepared $InternalName $version ($Channel)"
    Write-Host "Package: $releaseDirectory/$InternalName.zip"
    Write-Host "Release tag: $releaseTag"
}
finally {
    Pop-Location
}
