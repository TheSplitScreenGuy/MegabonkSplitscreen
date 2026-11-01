# Builds the release zip players install: dist\<release name>.zip (Plugin.ReleaseName, spaces -> dashes:
# SplitBonk-V1.0-Alpha.zip).
# Contents: the BepInEx loader (winhttp.dll, doorstop_config.ini, .doorstop_version, dotnet\, BepInEx\core\) copied from
# the game folder (read only - nothing there is changed), an empty BepInEx\plugins and the mod's DLL, plus README.txt,
# and BepInEx\config\BepInEx.cfg from release\ with only the console switched off (1.0.1: without it BepInEx made its
# default config on the first start - console on - and Steam Input then turned the pads into keyboard/mouse input).
# Left out: BepInEx\cache, interop, unity-libs, the rest of config (made on each player's first start), logs, the mod's
# test and dump files, and every file of the game itself.
#   powershell -ExecutionPolicy Bypass -File tools\make_release.ps1 [-Config Release]
param(
    [string]$GameDir = "H:\SteamLibrary\steamapps\common\Megabonk",
    [string]$Config = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dll = Join-Path $root "src\MegabonkSplitscreen\bin\$Config\net6.0\MegabonkSplitscreen.dll"
if (-not (Test-Path $dll)) { throw "Build output not found: $dll (build first)" }
$version = (Select-String -Path (Join-Path $root "src\MegabonkSplitscreen\Plugin.cs") -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
$release = (Select-String -Path (Join-Path $root "src\MegabonkSplitscreen\Plugin.cs") -Pattern 'ReleaseName = "([^"]+)"').Matches[0].Groups[1].Value
$bepVersion = (Get-Item (Join-Path $GameDir "BepInEx\core\BepInEx.Core.dll")).VersionInfo.ProductVersion -replace '\+.*', ''
$doorstop = (Get-Content (Join-Path $GameDir ".doorstop_version") -Raw).Trim()

$dist = Join-Path $root "dist"
$stage = Join-Path $dist "stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version") { Copy-Item (Join-Path $GameDir $f) (Join-Path $stage $f) }
Copy-Item (Join-Path $GameDir "dotnet") (Join-Path $stage "dotnet") -Recurse
New-Item -ItemType Directory -Force (Join-Path $stage "BepInEx") | Out-Null
Copy-Item (Join-Path $GameDir "BepInEx\core") (Join-Path $stage "BepInEx\core") -Recurse
New-Item -ItemType Directory -Force (Join-Path $stage "BepInEx\config") | Out-Null
Copy-Item (Join-Path $root "release\BepInEx.cfg") (Join-Path $stage "BepInEx\config\BepInEx.cfg")
$plug = Join-Path $stage "BepInEx\plugins\MegabonkSplitscreen"
New-Item -ItemType Directory -Force $plug | Out-Null
Copy-Item $dll $plug

$readme = (Get-Content (Join-Path $root "release\README.txt") -Raw).Replace("{RELEASE}", $release).Replace("{VERSION}", $version).Replace("{BEPINEX}", $bepVersion).Replace("{DOORSTOP}", $doorstop)
Set-Content -Path (Join-Path $stage "README.txt") -Value $readme -Encoding utf8
Copy-Item (Join-Path $root "release\THIRD-PARTY-NOTICES.txt") (Join-Path $stage "THIRD-PARTY-NOTICES.txt")

$zip = Join-Path $dist (($release -replace '\s+', '-') + ".zip")
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Entry names with '/' (user, Steam Deck: ZipFile.CreateFromDirectory on Windows PowerShell wrote 'BepInEx\core\...' -
# Linux unzippers made flat files with backslashes in their names, so the loader found no BepInEx)
$fs = [System.IO.File]::Open($zip, [System.IO.FileMode]::CreateNew)
$archive = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $base = (Resolve-Path $stage).Path.TrimEnd('\') + '\'
    foreach ($f in Get-ChildItem $stage -Recurse -File -Force) {
        $name = $f.FullName.Substring($base.Length).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose(); $fs.Dispose() }
Remove-Item $stage -Recurse -Force

$z = [System.IO.Compression.ZipFile]::OpenRead($zip)
$files = $z.Entries.Count
$top = $z.Entries | ForEach-Object { ($_.FullName -split '[\\/]')[0] } | Sort-Object -Unique
# The console must be off in the shipped config (see the top of this file)
$cfgEntry = $z.Entries | Where-Object { $_.FullName -eq "BepInEx/config/BepInEx.cfg" }
$cfgOk = $false
if ($cfgEntry) {
    $r = New-Object System.IO.StreamReader($cfgEntry.Open()); $text = $r.ReadToEnd(); $r.Close()
    $section = ($text -split '(?m)^\[')  | Where-Object { $_ -like 'Logging.Console]*' } | Select-Object -First 1
    $cfgOk = $section -and ($section -split "`r?`n" | Where-Object { $_.Trim() -eq 'Enabled = false' })
}
$z.Dispose()
if (-not $cfgOk) { throw "The zip has no BepInEx/config/BepInEx.cfg with the console off" }
Write-Output "Made $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB, $files files)"
Write-Output "Top level: $($top -join ', ')"
Write-Output "$release (mod $version), BepInEx $bepVersion, Doorstop $doorstop"
