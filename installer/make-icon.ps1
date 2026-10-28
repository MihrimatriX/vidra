# Uygulama ikonunu (src/Vidra.App/Assets/vidra.ico) üretir: kenar menüdeki logonun aynısı.
# Accent renkli yuvarlak kare + beyaz indirme oku (Icons.xaml'deki I.Download, 24×24 tuval).
# Çalıştır: pwsh installer/make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\Vidra.App\Assets\vidra.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

function Render([int]$s) {
    $bmp = [Drawing.Bitmap]::new($s, $s)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'

    # Yuvarlak kare: logodaki 34px kutu / 11px köşe oranı. Küçük boyutlarda kenar boşluğu yok, büyüklerde nefes payı.
    $pad = if ($s -ge 48) { $s * 0.06 } else { 0 }
    $w = $s - 2 * $pad; $r = $w * 11 / 34; $d = 2 * $r
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($pad, $pad, $d, $d, 180, 90); $path.AddArc($pad + $w - $d, $pad, $d, $d, 270, 90)
    $path.AddArc($pad + $w - $d, $pad + $w - $d, $d, $d, 0, 90); $path.AddArc($pad, $pad + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    # Hafif dikey geçiş: AccentHover → Accent (Dark.xaml) → biraz koyusu
    $rect = [Drawing.RectangleF]::new($pad, $pad, $w, $w)
    $fill = [Drawing.Drawing2D.LinearGradientBrush]::new($rect, [Drawing.Color]::FromArgb(0xFF, 0x66, 0x76), [Drawing.Color]::FromArgb(0xF2, 0x3D, 0x52), 90)
    $g.FillPath($fill, $path)

    # Ok: logoda 18px ikon / 34px kutu. Tepsi/görev çubuğu boyutlarında (≤32px) ok büyür, çizgi kalınlaşır.
    $k = $w * $(if ($s -le 32) { 23 } else { 18 }) / 34 / 24
    $o = $pad + ($w - 24 * $k) / 2
    $pen = [Drawing.Pen]::new([Drawing.Color]::White, [Math]::Max(2.4 * $k, [Math]::Min(2.0, $s / 10)))
    $pen.StartCap = $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $P = { param($x, $y) [Drawing.PointF]::new($o + $x * $k, $o + $y * $k) }
    $g.DrawLine($pen, (& $P 12 4), (& $P 12 16))
    $g.DrawLines($pen, [Drawing.PointF[]]@((& $P 6 11), (& $P 12 17), (& $P 18 11)))
    $g.DrawLine($pen, (& $P 5 20), (& $P 19 20))
    $g.Dispose()

    $ms = [IO.MemoryStream]::new(); $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}

# ICO: 6 bayt başlık + görüntü başına 16 bayt dizin + PNG verileri (Vista+ PNG gömülü ICO'yu okur)
$pngs = $sizes | ForEach-Object { , (Render $_) }
$f = [IO.BinaryWriter]::new([IO.File]::Create($out))
$f.Write([uint16]0); $f.Write([uint16]1); $f.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = [byte]($s % 256)  # 256 → 0
    $f.Write($b); $f.Write($b); $f.Write([byte]0); $f.Write([byte]0)
    $f.Write([uint16]1); $f.Write([uint16]32); $f.Write([uint32]$pngs[$i].Length); $f.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
$pngs | ForEach-Object { $f.Write($_) }
$f.Dispose()
"$out ($((Get-Item $out).Length) bayt, $($sizes -join '/') px)"
