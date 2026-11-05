# Build the SDR# plugin on Windows and assemble an install folder and zip.
#
#   .\build-plugin.ps1 -SdrSharpLib "C:\dev\sdrsharp-plugin-sdk\sdrplugins\lib"
#   .\build-plugin.ps1 -SdrSharpLib ... -Install "C:\SDRSharp\Plugins"     # also copies it into SDR#
#
# Needs the .NET 9 or 10 SDK. The native decoder (plugin\native\x86, x64) is prebuilt;
# rebuild it with build.sh on Linux if you change the C code.
# SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
param(
    [Parameter(Mandatory = $true)][string]$SdrSharpLib,
    [string]$Install = "",
    [string]$Framework = "net9.0-windows"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "plugin\SDRSharp.M17.csproj"

# Accept the SDK's lib folder or any folder above it; find SDRSharp.Common.dll ourselves.
if (-not (Test-Path $SdrSharpLib)) { throw "SdrSharpLib folder not found: $SdrSharpLib" }
$SdrSharpLib = (Resolve-Path $SdrSharpLib).Path.TrimEnd('\')
if (-not (Test-Path (Join-Path $SdrSharpLib "SDRSharp.Common.dll"))) {
    $found = Get-ChildItem -Path $SdrSharpLib -Recurse -Filter "SDRSharp.Common.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $found) { throw "SDRSharp.Common.dll not found in or below $SdrSharpLib" }
    $SdrSharpLib = $found.DirectoryName
}
Write-Host "SDR# SDK lib: $SdrSharpLib"

$log = Join-Path $root "build.log"
dotnet build $proj -c Release -f $Framework "-p:SdrSharpLib=$SdrSharpLib" -clp:ErrorsOnly -flp:"logfile=$log;verbosity=normal"
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Errors:" -ForegroundColor Red
    Select-String -Path $log -Pattern "error" | Select-Object -First 20 | ForEach-Object { Write-Host $_.Line }
    throw "build failed (full log: $log)"
}

$out = Join-Path $root "plugin\bin\Release\$Framework"
$dist = Join-Path $root "dist\M17Decoder"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$dist\x86", "$dist\x64" | Out-Null
Copy-Item "$out\SDRSharp.M17.dll" $dist
Copy-Item "$out\x86\m17rx.dll" "$dist\x86"
Copy-Item "$out\x64\m17rx.dll" "$dist\x64"
Copy-Item "$root\COPYING", "$root\SDRSHARP-EXCEPTION.txt", "$root\THIRD-PARTY-NOTICES.txt", "$root\README.md" $dist

$zip = Join-Path $root "dist\M17Decoder.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $dist -DestinationPath $zip
Write-Host "Plugin folder: $dist"
Write-Host "Zip:           $zip"

if ($Install -ne "") {
    $target = Join-Path $Install "M17Decoder"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $dist $target -Recurse
    Write-Host "Installed to   $target (restart SDR#)"
}
