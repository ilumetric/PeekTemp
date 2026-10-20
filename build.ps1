# Builds publish\PeekTemp.exe: a single framework-dependent exe (needs .NET 9 Desktop Runtime).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# A running copy locks the exe.
Get-Process PeekTemp -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq (Join-Path $root 'publish\PeekTemp.exe') } |
    ForEach-Object { Write-Host "Stopping running PeekTemp ($($_.Id))"; $_.Kill(); $_.WaitForExit() }

dotnet publish (Join-Path $root 'src\PeekTemp\PeekTemp.csproj') `
    -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -o (Join-Path $root 'publish') -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Write-Host "Done: $(Join-Path $root 'publish\PeekTemp.exe')"
