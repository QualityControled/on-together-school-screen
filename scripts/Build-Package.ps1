param(
    [string]$GameManagedDir = $env:ON_TOGETHER_MANAGED_DIR,
    [string]$BepInExCoreDir = $env:BEPINEX_CORE_DIR,
    [string]$OutputDir,
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$repositoryDir = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = $repositoryDir }
if (-not $DotnetPath) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetCommand) { $DotnetPath = $dotnetCommand.Source }
    else { $DotnetPath = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
}
$manifest = Get-Content -LiteralPath (Join-Path $repositoryDir 'manifest.json') -Raw | ConvertFrom-Json
$version = $manifest.version_number
$pluginSource = Get-Content -LiteralPath (Join-Path $repositoryDir 'src\SchoolScreenMod\SchoolScreenPlugin.cs') -Raw
if ($pluginSource -notmatch ('BepInPlugin\([^\r\n]*"' + [regex]::Escape($version) + '"\)')) {
    throw 'The manifest and plugin versions must match.'
}
$buildDir = Join-Path $repositoryDir "work\build-$version"
$pluginBuildDir = Join-Path $buildDir 'plugin'
$browserBuildDir = Join-Path $buildDir 'browser'
& $DotnetPath build (Join-Path $repositoryDir 'src\SchoolScreenMod\SchoolScreenMod.csproj') --configuration Release --output $pluginBuildDir "-p:GameManagedDir=$GameManagedDir" "-p:BepInExCoreDir=$BepInExCoreDir"
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
& $DotnetPath build (Join-Path $repositoryDir 'src\SchoolScreenBrowser\SchoolScreenBrowser.csproj') --configuration Release --output $browserBuildDir
if ($LASTEXITCODE -ne 0) { throw 'Browser build failed.' }

$releaseDir = Join-Path $OutputDir "OnTogetherSchoolScreen-$version"
$zipPath = Join-Path $OutputDir "OnTogetherSchoolScreen-$version.zip"
if ((Test-Path -LiteralPath $releaseDir) -or (Test-Path -LiteralPath $zipPath)) {
    throw 'Release output already exists. Use a fresh OutputDir to preserve the existing artifact.'
}
$payloadDir = Join-Path $releaseDir 'BepInEx\plugins'
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $pluginBuildDir 'OnTogetherSchoolScreen.dll') -Destination $payloadDir
foreach ($filename in @('SchoolScreenBrowser.exe','SchoolScreenBrowser.exe.config','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll')) {
    Copy-Item -LiteralPath (Join-Path $browserBuildDir $filename) -Destination $payloadDir
}
Copy-Item -LiteralPath (Join-Path $browserBuildDir 'www') -Destination $payloadDir -Recurse
foreach ($filename in @('manifest.json','README.md','CHANGELOG.md','icon.png')) {
    Copy-Item -LiteralPath (Join-Path $repositoryDir $filename) -Destination $releaseDir
}
$playerReadme = (Get-Content -LiteralPath (Join-Path $repositoryDir 'README.md') -Raw).Split(@('## Build from source'), [StringSplitOptions]::None)[0].TrimEnd() + "`r`n"
[IO.File]::WriteAllText((Join-Path $releaseDir 'README.md'), $playerReadme, [Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($releaseDir, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Output $zipPath
