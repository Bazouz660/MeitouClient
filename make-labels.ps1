# Renders Kenshi message-banner labels (MessageSkin from Kenshi_UI.png, Exo 2 SemiBold) as transparent PNGs.
# -Style Modern draws the Meitou UI's paper card instead (MenuArt.Card: rounded, worn rim, drop shadow).
# Usage: .\make-labels.ps1 [-Labels "Vanilla","Faithful","Meitou"] [-Style Modern] [-Scale 1.5] [-Width 480] [-Align Left] [-Padding 40] [-HeightScale 1] [-Cap 1] [-Out .\labels]
param(
  [string[]]$Labels = @('Vanilla', 'Faithful', 'Meitou'),
  [ValidateSet('Classic', 'Modern')][string]$Style = 'Classic',
  [double]$Scale = 1.5,
  [int]$Width = 480,
  [string]$Out = (Join-Path $PSScriptRoot 'labels'),
  [string]$Game = 'E:\Kenshi_RE\data\gui',
  [ValidateSet('Center', 'Left')][string]$Align = 'Center',
  [double]$Padding = 40,      # space beside the text, in source pixels (x Scale)
  [double]$HeightScale = 1,  # squash or stretch the banner vertically
  [double]$Cap = 1           # squash the worn end caps horizontally
)
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null

$atlas = [System.Drawing.Bitmap]::FromFile("$Game\gfx\Kenshi_UI.png")
$fonts = New-Object System.Drawing.Text.PrivateFontCollection
$fonts.AddFontFile("$Game\fonts\Exo2-SemiBold.ttf")
$family = $fonts.Families[0]

# MessageSkin: source 761,251 170x70; left cap 82, stretched middle 35, right cap 53.
$sx = 761; $sy = 251; $sh = 70; $L = 82; $R = 53; $M = 35
$pad = 6                                   # transparent margin so the worn edges never clip
$H = [int][math]::Round($sh * $Scale * $HeightScale)
$cl = [int][math]::Round($L * $Scale * $Cap); $cr = [int][math]::Round($R * $Scale * $Cap)
$W = [math]::Max($Width, $cl + $cr + 1)
# Widen every banner in the set to fit the longest label, so they all match.
$probe = New-Object System.Drawing.Bitmap(1, 1); $pg = [System.Drawing.Graphics]::FromImage($probe)
$pf = New-Object System.Drawing.Font($family, [single](22 * $Scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
foreach ($label in $Labels) { $W = [math]::Max($W, [int]($pg.MeasureString($label, $pf).Width + 2 * $Padding * $Scale)) }
$pg.Dispose(); $probe.Dispose(); $pf.Dispose()

if ($Style -eq 'Modern') {
  # The paper card of src/Meitou.Game/PlayerInterface.TitleMenu.cs MenuArt.Card, baked the same way.
  Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class LabelCard {
  static byte[] src; static int sw, sh;
  static float Rd(float x, float y, float w, float h, float r) {
    float qx = Math.Abs(x - w / 2) - (w / 2 - r), qy = Math.Abs(y - h / 2) - (h / 2 - r);
    float ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
    return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r;
  }
  static float Smooth(float t) { return t * t * (3 - 2 * t); }
  static float Clamp(float v, float a, float b) { return v < a ? a : v > b ? b : v; }
  static void At(int x, int y, float[] c, float k) {
    x = Math.Max(0, Math.Min(sw - 1, x)); y = Math.Max(0, Math.Min(sh - 1, y)); int i = (y * sw + x) * 4;
    c[0] += src[i + 2] / 255f * k; c[1] += src[i + 1] / 255f * k; c[2] += src[i] / 255f * k;
  }
  public static Bitmap Bake(Bitmap atlas, int cx, int cy, int cw, int ch, int width, int height, float s, int m) {
    var lk = atlas.LockBits(new Rectangle(0, 0, atlas.Width, atlas.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    sw = atlas.Width; sh = atlas.Height; src = new byte[sw * sh * 4]; Marshal.Copy(lk.Scan0, src, 0, src.Length); atlas.UnlockBits(lk);
    int w = width + 2 * m, h = height + 2 * m; var outPx = new byte[w * h * 4]; float radius = 10 * s;
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
      float px = x + 0.5f - m, py = y + 0.5f - m, d = Rd(px, py, width, height, radius);
      float shadow = 0.55f * Smooth(1 - Clamp((Rd(px, py - 6 * s, width, height, radius) + 3 * s) / (14 * s), 0, 1));
      float cover = Clamp(0.5f - d, 0, 1); float r = 0, g = 0, b = 0;
      if (cover > 0) {
        float sx = cx + Clamp(px / width, 0, 1) * cw - 0.5f, sy = cy + Clamp(py / height, 0, 1) * ch - 0.5f;
        int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy); float fx = sx - x0, fy = sy - y0; var c = new float[3];
        At(x0, y0, c, (1 - fx) * (1 - fy)); At(x0 + 1, y0, c, fx * (1 - fy)); At(x0, y0 + 1, c, (1 - fx) * fy); At(x0 + 1, y0 + 1, c, fx * fy);
        r = c[0]; g = c[1]; b = c[2]; float inside = -d, t;
        t = 0.35f * Clamp(1 - inside / (10 * s), 0, 1); r += (60 / 255f - r) * t; g += (35 / 255f - g) * t; b += (20 / 255f - b) * t;
        if (inside < 3 * s) { t = 0.25f; r += (20 / 255f - r) * t; g += (8 / 255f - g) * t; b += (6 / 255f - b) * t; }
        if (inside < 2 * s) { t = 0.9f; r += (20 / 255f - r) * t; g += (8 / 255f - g) * t; b += (6 / 255f - b) * t; }
      }
      float a = cover + shadow * (1 - cover); int o = (y * w + x) * 4;
      if (a > 0) { float k = cover / a; outPx[o + 2] = (byte)Clamp(r * k * 255 + 0.5f, 0, 255); outPx[o + 1] = (byte)Clamp(g * k * 255 + 0.5f, 0, 255); outPx[o] = (byte)Clamp(b * k * 255 + 0.5f, 0, 255); }
      outPx[o + 3] = (byte)Clamp(a * 255 + 0.5f, 0, 255);
    }
    var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    var ol = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    Marshal.Copy(outPx, 0, ol.Scan0, outPx.Length); bmp.UnlockBits(ol); return bmp;
  }
}
'@
  $pad = [int][math]::Ceiling(18 * $Scale)   # room for the card's drop shadow
  $card = [LabelCard]::Bake($atlas, 352, 457, 164, 81, $W, $H, [single]$Scale, $pad)
}

$attr = New-Object System.Drawing.Imaging.ImageAttributes
$attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)   # no edge bleed when stretching
function Draw-Part($g, $dx, $dw, $srcX, $srcW) {
  $dest = New-Object System.Drawing.Rectangle(($pad + $dx), $pad, $dw, $H)
  $g.DrawImage($atlas, $dest, $srcX, $sy, $srcW, $sh, [System.Drawing.GraphicsUnit]::Pixel, $attr)
}

foreach ($label in $Labels) {
  $bmp = New-Object System.Drawing.Bitmap(($W + 2 * $pad), ($H + 2 * $pad), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

  if ($card) { $g.DrawImageUnscaled($card, 0, 0) }
  else {
    Draw-Part $g 0 $cl $sx $L
    Draw-Part $g $cl ($W - $cl - $cr) ($sx + $L) $M
    Draw-Part $g ($W - $cr) $cr ($sx + $L + $M) $R
  }

  # Text: the game's Secondary colour on paper, Floater size (22px) scaled with the banner.
  $font = New-Object System.Drawing.Font($family, [single](22 * $Scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
  $fmt = New-Object System.Drawing.StringFormat
  $fmt.Alignment = if ($Align -eq 'Left') { [System.Drawing.StringAlignment]::Near } else { [System.Drawing.StringAlignment]::Center }
  $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
  $brush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#140806'))
  $inset = if ($Align -eq 'Left') { $Padding * $Scale } else { 0 }   # left-aligned text starts inside the worn edge
  $rect = New-Object System.Drawing.RectangleF([single]($pad + $inset), [single]($pad - 1 * $Scale), [single]($W - $inset), [single]$H)
  $g.DrawString($label, $font, $brush, $rect, $fmt)

  $g.Dispose(); $font.Dispose(); $brush.Dispose()
  $slug = ($label.ToLower() -replace '&', 'and' -replace '[^a-z0-9]+', '-').Trim('-')
  $file = Join-Path $Out "$slug.png"
  $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  "{0}  {1}x{2}" -f $file, ($W + 2 * $pad), ($H + 2 * $pad)
}
if ($card) { $card.Dispose() }
$atlas.Dispose(); $fonts.Dispose()
