# Bir sonraki sürümü hazırlar; "Sürüm yayınla" workflow'u çağırır, elle de çalıştırılabilir.
#  1. Sürümü belirler: yayınlanmış en büyük v* etiketinden -Bump kadar artırır (minor: 0.6.0 → 0.7.0,
#     patch: 0.6.1, major: 1.0.0). -Bump elle: Directory.Build.props'taki sürüm olduğu gibi kullanılır.
#  2. Directory.Build.props'taki <Version>'ı yazar. Kurulum (build.ps1 → vidra.iss) ve uygulamanın kendi
#     sürümü (güncelleme karşılaştırması) buradan okunduğu için tek kaynak bu dosya olarak kalır.
#  3. Sürüm notunu yazar: docs/releases/v<sürüm>.md zaten varsa dokunmaz; yoksa docs/releases/next.md'deki
#     maddelerden üretir ve next.md'yi boş şablona döndürür. next.md'de madde yoksa birleşen PR'ların
#     başlıklarından üretir (GitHub generate-notes; gh ve GH_TOKEN gerekir).
# Çıktı: version, tag, previous (GITHUB_OUTPUT varsa oraya da yazılır). Commit/push yapmaz.
# Çalıştır: pwsh installer/prepare-release.ps1 -Bump minor
param(
    [ValidateSet('minor', 'patch', 'major', 'elle')]
    [string]$Bump = 'minor',
    # Verilmezse origin'deki v* etiketleri okunur (sığ checkout'ta da çalışır).
    [string[]]$Tags
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$propsPath = Join-Path $root 'Directory.Build.props'
$notesDir = Join-Path $root 'docs/releases'
$nextPath = Join-Path $notesDir 'next.md'
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Parse-Semver([string]$s) {
    if ($s -match '^v?(\d+)\.(\d+)\.(\d+)$') { return [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3]) }
    return $null
}

# --- 1. Sürüm ---
$props = [IO.File]::ReadAllText($propsPath)
if ($props -notmatch '<Version>([^<]+)</Version>') { throw 'Directory.Build.props içinde <Version> yok' }
$propsVersion = Parse-Semver $Matches[1]
if (-not $propsVersion) { throw "Directory.Build.props sürümü X.Y.Z biçiminde değil: $($Matches[1])" }

if (-not $PSBoundParameters.ContainsKey('Tags')) {
    $Tags = git -C $root ls-remote --tags --refs origin 'v*' | ForEach-Object { ($_ -split 'refs/tags/')[1] }
    if ($LASTEXITCODE) { throw "Etiketler okunamadı (git ls-remote: $LASTEXITCODE)" }
}
$released = @($Tags | ForEach-Object { Parse-Semver $_ } | Where-Object { $_ } | Sort-Object -Descending)
$previous = if ($released.Count) { $released[0] } else { $null }
$base = if ($previous) { $previous } else { [version]::new(0, 0, 0) }

$version = switch ($Bump) {
    'major' { [version]::new($base.Major + 1, 0, 0) }
    'minor' { [version]::new($base.Major, $base.Minor + 1, 0) }
    'patch' { [version]::new($base.Major, $base.Minor, $base.Build + 1) }
    'elle' { $propsVersion }
}
if ($previous -and $version -le $previous) {
    throw "Sürüm $version, yayınlanmış son sürümden ($previous) büyük değil; Directory.Build.props'ta artır ya da -Bump minor/patch/major kullan"
}
$tag = "v$version"
$prevTag = if ($previous) { "v$previous" } else { '' }
Write-Host "Sürüm: $prevTag → $tag ($Bump)"

# --- 2. Directory.Build.props ---
$newProps = $props -replace '<Version>[^<]+</Version>', "<Version>$version</Version>"
if ($newProps -ne $props) { [IO.File]::WriteAllText($propsPath, $newProps, $utf8) }

# --- 3. Sürüm notu ---
$nextTemplate = @'
<!--
  Bir sonraki sürümün notları. Kullanıcının fark edeceği her değişiklikte PR'da buraya bir madde ekle.
  "Sürüm yayınla" bu dosyayı docs/releases/v<sürüm>.md yapar ve boş şablona döndürür; başlık, İndir ve
  Gereksinim satırlarını kendisi ekler. Hiç madde yoksa notlar birleşen PR başlıklarından üretilir.
  Boş kalan bölüm yayına girmez.
-->

### Yenilikler

### Düzeltmeler
'@

# Boş bölümleri ve yorumları atar; madde ("- ") yoksa boş döner.
function Get-NotesBody([string]$text) {
    $text = [regex]::Replace($text, '(?s)<!--.*?-->', '').Replace("`r`n", "`n")
    if ($text -notmatch '(?m)^\s*[-*] \S') { return '' }
    $sections = [regex]::Split($text.Trim(), '(?m)^(?=#{2,3} )')
    $kept = $sections | ForEach-Object { $_.Trim() } | Where-Object { $_ -and ($_ -notmatch '^#{2,3} ' -or $_ -match '(?m)^\s*[-*] \S') }
    return ($kept -join "`n`n")
}

$notesPath = Join-Path $notesDir "$tag.md"
if (Test-Path $notesPath) {
    Write-Host "Sürüm notu zaten var: docs/releases/$tag.md"
}
else {
    $body = if (Test-Path $nextPath) { Get-NotesBody ([IO.File]::ReadAllText($nextPath)) } else { '' }
    if ($body) {
        Write-Host 'Sürüm notu docs/releases/next.md maddelerinden'
    }
    else {
        Write-Host 'next.md boş: notlar birleşen PR başlıklarından üretiliyor'
        $items = @()
        try {
            $repo = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'MihrimatriX/vidra' }
            $apiArgs = @("repos/$repo/releases/generate-notes", '-f', "tag_name=$tag", '--jq', '.body')
            if ($prevTag) { $apiArgs += @('-f', "previous_tag_name=$prevTag") }
            if ($env:GITHUB_SHA) { $apiArgs += @('-f', "target_commitish=$env:GITHUB_SHA") }
            $generated = gh api @apiArgs 2>$null
            if ($LASTEXITCODE -eq 0) {
                # "* Başlık by @kişi in https://…/pull/12" → "- Başlık (#12)"
                $items = @($generated | Where-Object { $_ -match '^\* ' } | ForEach-Object {
                        $line = $_ -replace '^\* ', '- '
                        if ($line -match '^(- .+?) by @\S+ in \S+/pull/(\d+)$') { "$($Matches[1]) (#$($Matches[2]))" } else { $line }
                    })
            }
        }
        catch { }
        if (-not $items) { $items = @('- Küçük iyileştirmeler ve düzeltmeler.') }
        $body = "### Değişiklikler`n" + ($items -join "`n")
    }

    $notes = @"
## Vidra $version

### İndir
- **Vidra-Setup.exe**: Kurulum (önerilen). Yönetici izni istemez; eski sürümün üzerine kurulur, ayarlar ve geçmiş korunur.
- **Vidra-Portable.exe**: Kurulumsuz tek dosya. .NET gerekmez.

$body

Gereksinim: Windows 10 1809 veya üstü, x64.

"@
    [IO.File]::WriteAllText($notesPath, $notes.Replace("`r`n", "`n"), $utf8)
    [IO.File]::WriteAllText($nextPath, $nextTemplate.Replace("`r`n", "`n") + "`n", $utf8)
}

"version=$version", "tag=$tag", "previous=$prevTag" | ForEach-Object {
    Write-Host $_
    if ($env:GITHUB_OUTPUT) { $_ | Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8 }
}
