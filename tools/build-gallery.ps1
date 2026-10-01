<#
  build-gallery.ps1 - เตรียมรูปสำหรับ slideshow ใน VRChat
  โครงสร้างผลลัพธ์: config.json + images/0.jpg, 1.jpg, ...

  ตัวอย่าง:
    .\tools\build-gallery.ps1 -Album "Paradise\paradise_central_control\gallery"
    .\tools\build-gallery.ps1 -Album "Paradise\Seaside\gallery" -Source "D:\pics" -Duration 5
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Album,                 # โฟลเดอร์ปลายทางใน repo
    [string]$Source,                # โฟลเดอร์รูปต้นฉบับ (ดีฟอลต์: _source\ ที่ root ของ repo)
    [double]$Duration = 10.0,       # วินาทีต่อรูป
    [int]$MaxSize     = 2048,       # ด้านยาวสุด - VRChat รับได้สูงสุด 2048
    [int]$Quality     = 90,         # คุณภาพ JPEG 1-100
    [switch]$NoLoop
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $repo "_source" }

$srcDir = if ([System.IO.Path]::IsPathRooted($Source)) { $Source } else { Join-Path $repo $Source }
$albDir = if ([System.IO.Path]::IsPathRooted($Album))  { $Album }  else { Join-Path $repo $Album }
$outDir = Join-Path $albDir "images"

foreach ($d in @($srcDir, $outDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

$exts  = @(".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff")
$files = Get-ChildItem -Path $srcDir -File |
         Where-Object { $exts -contains $_.Extension.ToLower() } |
         Sort-Object Name

if ($files.Count -eq 0) {
    Write-Host "ไม่พบรูปใน $srcDir" -ForegroundColor Yellow
    Write-Host "เอารูปไปวางในโฟลเดอร์นั้นก่อนแล้วรันใหม่" -ForegroundColor Yellow
    return
}

# ล้าง output เก่า กันรูปที่ลบออกแล้วค้างอยู่
Get-ChildItem -Path $outDir -Filter *.jpg | Remove-Item -Force

$codec     = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
             Where-Object { $_.MimeType -eq "image/jpeg" }
$encParams = New-Object System.Drawing.Imaging.EncoderParameters(1)
$encParams.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
    [System.Drawing.Imaging.Encoder]::Quality, [int64]$Quality)

$n = 0
$vram = 0
foreach ($f in $files) {
    $img = $null; $bmp = $null; $g = $null
    try {
        $img = [System.Drawing.Image]::FromFile($f.FullName)

        # หมุนตาม EXIF orientation (รูปจากมือถือมักตะแคง)
        if ($img.PropertyIdList -contains 274) {
            switch ($img.GetPropertyItem(274).Value[0]) {
                3 { $img.RotateFlip([System.Drawing.RotateFlipType]::Rotate180FlipNone) }
                6 { $img.RotateFlip([System.Drawing.RotateFlipType]::Rotate90FlipNone)  }
                8 { $img.RotateFlip([System.Drawing.RotateFlipType]::Rotate270FlipNone) }
            }
        }

        $scale = [Math]::Min(1.0, $MaxSize / [double][Math]::Max($img.Width, $img.Height))
        $w = [int][Math]::Round($img.Width  * $scale)
        $h = [int][Math]::Round($img.Height * $scale)

        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g   = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Black)   # รองพื้น เผื่อต้นฉบับมี alpha
        $g.DrawImage($img, 0, 0, $w, $h)

        $name = "$n.jpg"              # ไล่เลขจาก 0
        $dest = Join-Path $outDir $name
        $bmp.Save($dest, $codec, $encParams)

        $kb    = [int]((Get-Item $dest).Length / 1KB)
        $vram += $w * $h * 4 / 1MB
        Write-Host ("  {0,-8} <- {1,-28} {2}x{3}  {4} KB" -f $name, $f.Name, $w, $h, $kb)
        $n++
    }
    catch {
        Write-Host ("  ข้าม {0}: {1}" -f $f.Name, $_.Exception.Message) -ForegroundColor Yellow
    }
    finally {
        if ($g)   { $g.Dispose() }
        if ($bmp) { $bmp.Dispose() }
        if ($img) { $img.Dispose() }
    }
}

# เขียน JSON เองเพื่อคุมฟอร์แมตให้คงที่
# (ConvertTo-Json ของ PS 5.1 ใส่ช่องว่างเกิน และตัด .0 ของ duration ทิ้ง)
$loopStr = if ($NoLoop) { "false" } else { "true" }
$durStr  = $Duration.ToString("0.0##", [System.Globalization.CultureInfo]::InvariantCulture)
$json    = "{`n  `"count`": $n,`n  `"duration`": $durStr,`n  `"loop`": $loopStr`n}`n"
[System.IO.File]::WriteAllText((Join-Path $albDir "config.json"), $json, (New-Object System.Text.UTF8Encoding $false))

$totalKb = [int](((Get-ChildItem $outDir -Filter *.jpg | Measure-Object Length -Sum).Sum) / 1KB)
Write-Host ""
Write-Host "เสร็จแล้ว: $n รูป (รวม $totalKb KB) -> $Album\images\" -ForegroundColor Green
Write-Host ("VRAM ที่จะกินใน VRChat ประมาณ {0} MB" -f [int]$vram) -ForegroundColor DarkGray
if ($vram -gt 150) {
    Write-Host "เตือน: เกิน 150 MB แล้ว ลองลด -MaxSize 1024 หรือลดจำนวนรูป" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "เวลาโหลดในเกม: $n รูป x 5 วิ = $($n * 5) วินาทีกว่าจะครบ (แสดงรูปแรกได้ทันที)" -ForegroundColor DarkGray
