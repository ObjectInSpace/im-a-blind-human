# Builds a clean Release and assembles the distributable zip, laid out to mirror the game folder
# (Mods\ + native DLLs at root) so a user can extract it straight over their game directory.
# Run from anywhere: pwsh release/pack.ps1
$ErrorActionPreference = "Stop"

$proj = Split-Path $PSScriptRoot -Parent          # ...\No Im Not A Human Access (the .csproj dir)
$csproj = Join-Path $proj "NoImNotAHumanAccess.csproj"
$libs   = Join-Path $proj "libs"
$relDir = $PSScriptRoot                            # ...\release
$bin    = Join-Path $proj "bin\Release"

# Mod version straight from MelonInfo so the zip name can't drift from the assembly. Grab the version-shaped
# quoted token on the MelonInfo line (it's the 3rd string arg, after the display name and author).
$miLine = (Select-String -Path (Join-Path $proj "src\AccessMod.cs") -Pattern 'MelonInfo\(typeof').Line
$m = [regex]::Match($miLine, '"(\d+\.\d+\.\d+)"')
if (-not $m.Success) { throw "Could not parse mod version from AccessMod.cs MelonInfo line: $miLine" }
$version = $m.Groups[1].Value
Write-Host "Mod version: $version"

# 1. Clean Release build (don't trust stale bin).
Write-Host "Building Release..."
dotnet build $csproj -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# 2. Resolve the managed UnityAccessibilityLib.dll (a library PackageReference isn't copied to bin,
#    so pull it from the same NuGet cache the build resolved).
$ualNupkg = Get-ChildItem "$env:USERPROFILE\.nuget\packages\unityaccessibilitylib" -Recurse -Filter "UnityAccessibilityLib.dll" |
            Where-Object { $_.FullName -match "net6" } | Select-Object -First 1
if (-not $ualNupkg) { throw "UnityAccessibilityLib.dll (net6) not found in NuGet cache." }

# 3. Stage the layout.
$stageRoot = Join-Path $relDir "stage"
$pkgName   = "ImABlindHuman-v$version"
$stage     = Join-Path $stageRoot $pkgName
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage "Mods")     | Out-Null
New-Item -ItemType Directory -Force (Join-Path $stage "licenses") | Out-Null

# Managed -> Mods\
Copy-Item (Join-Path $bin "NoImNotAHumanAccess.dll") (Join-Path $stage "Mods") -Force
Copy-Item $ualNupkg.FullName                          (Join-Path $stage "Mods") -Force

# Native speech DLLs -> game root (zip root). Only the runtime DLLs, not the license text files in libs\.
Get-ChildItem $libs -Filter *.dll | Copy-Item -Destination $stage -Force

# Licenses -> licenses\
Get-ChildItem $libs -Filter *LICENSE*.txt | Copy-Item -Destination (Join-Path $stage "licenses") -Force

# README -> zip root
Copy-Item (Join-Path $relDir "README.md") $stage -Force

# 4. Zip it.
$zipPath = Join-Path $relDir "$pkgName.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -CompressionLevel Optimal

# Compress-Archive flattens away the top folder if we glob with \*, so re-pack including the named folder.
Remove-Item $zipPath -Force
Compress-Archive -Path $stage -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ""
Write-Host "Built: $zipPath"
Get-Item $zipPath | ForEach-Object { "  {0:N0} bytes" -f $_.Length }
