# Builds the plugin and packs a ready-to-install folder + zip into .\dist
param(
    [string]$Version = "1.0.0.0"
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "Jellyfin.Plugin.AltTitles\Jellyfin.Plugin.AltTitles.csproj"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (Test-Path $userDotnet) { $dotnet = $userDotnet }

& $dotnet build $proj -c Release -nologo -p:AssemblyVersion=$Version -p:FileVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$dist = Join-Path $root "dist"
$folder = Join-Path $dist "AltTitles_$Version"
if (Test-Path $folder) { Remove-Item $folder -Recurse -Force -Confirm:$false }
New-Item -ItemType Directory -Force $folder | Out-Null

Copy-Item (Join-Path $root "Jellyfin.Plugin.AltTitles\bin\Release\net10.0\Jellyfin.Plugin.AltTitles.dll") $folder

Copy-Item (Join-Path $root "images\logo.png") $folder

$meta = [ordered]@{
    category    = "General"
    changelog   = "First release: search movies and series by their English, German, romanized and other titles (TMDB, AniDB, Korean romanization), in all apps."
    description = "Makes movies and series findable under their other titles: English, German, romanized and original titles, in the web client and in all apps."
    guid        = "d98afd97-f0cf-4d6b-8a32-3009cd835ac4"
    name        = "Alternative Titles"
    overview    = "Search by English, German, romanized and original titles"
    owner       = "skriewel"
    targetAbi   = "12.0.0.0"
    timestamp   = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    version     = $Version
    status      = "Active"
    autoUpdate  = $true
    imagePath   = "logo.png"
    assemblies  = @("Jellyfin.Plugin.AltTitles.dll")
}
[IO.File]::WriteAllText((Join-Path $folder "meta.json"), ($meta | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))

$zip = Join-Path $dist "AltTitles_$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force -Confirm:$false }
Compress-Archive -Path (Join-Path $folder "*") -DestinationPath $zip
$md5 = (Get-FileHash $zip -Algorithm MD5).Hash
Write-Host "Done: $zip (MD5 $md5)"

& (Join-Path $root "update-manifest.ps1") -Version $Version
