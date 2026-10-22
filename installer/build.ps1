# Sürüm dosyalarını üretir: dist/Vidra-Setup.exe (kurulum) ve dist/Vidra-Portable.exe (tek exe).
# Adlar sürüm içermez; böylece releases/latest/download/<ad> bağlantıları her zaman son sürümü indirir.
# Gerekenler: .NET SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup)
# Çalıştır: pwsh installer/build.ps1
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version

# Tek, bağımsız exe (.NET kurulu olmayan makinelerde de çalışır). Eski dosyalar karışmasın diye klasör temizlenir.
Remove-Item "$root\publish" -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\src\Vidra.App" -c Release -r win-x64 -o "$root\publish"
if ($LASTEXITCODE) { throw "dotnet publish başarısız ($LASTEXITCODE)" }

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 bulunamadı: winget install JRSoftware.InnoSetup' }

& $iscc /Q "/DAppVersion=$version" "$PSScriptRoot\vidra.iss"
if ($LASTEXITCODE) { throw "ISCC başarısız ($LASTEXITCODE)" }
Copy-Item "$root\publish\Vidra.exe" "$root\dist\Vidra-Portable.exe" -Force
Get-Item "$root\dist\Vidra-Setup.exe", "$root\dist\Vidra-Portable.exe"
