param(
    [string]$PluginSource,
    [string]$OutputDir,
    [string]$PluginDll,
    [string]$GameDll,
    [string]$NodePath = 'node',
    [string]$DotnetPath = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$PluginSource) { $PluginSource = Join-Path $packageRoot 'src\SchoolScreenMod\SchoolScreenPlugin.cs' }
if (!$OutputDir) { $OutputDir = Join-Path $packageRoot 'work\mixed-lobby-check' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$extracted = Join-Path $OutputDir 'SchoolScreenPlugin.selected.cs'
& $NodePath (Join-Path $PSScriptRoot 'extract-source.mjs') $PluginSource $extracted
if ($LASTEXITCODE -ne 0) { throw 'Outgoing RPC wrapper check failed.' }
& $DotnetPath build (Join-Path $PSScriptRoot 'MixedLobbyCheck.csproj') --configuration Release --output (Join-Path $OutputDir 'bin') "-p:ExtractedSource=$extracted" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Source-linked simulation failed to compile.' }
if ($PluginDll -or $GameDll) {
    if (!$PluginDll -or !$GameDll) { throw 'PluginDll and GameDll must both be supplied for compiled transport checks.' }
    & $DotnetPath (Join-Path $OutputDir 'bin\MixedLobbyCheck.dll') $PluginDll $GameDll
} else {
    & $DotnetPath (Join-Path $OutputDir 'bin\MixedLobbyCheck.dll')
}
if ($LASTEXITCODE -ne 0) { throw 'Mixed-lobby simulation failed.' }
