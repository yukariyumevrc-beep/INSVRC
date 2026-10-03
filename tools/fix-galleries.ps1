<#
  fix-galleries.ps1 - ตรวจและซ่อมทุกแกลเลอรีในคราวเดียว

  ไล่อ่านรายชื่อแกลเลอรีจาก galleries.json แล้วเช็กให้ 5 อย่าง
    1. รูปใหญ่เกิน 2048  -> VRChat โหลดไม่ขึ้น
    2. นามสกุลปนกัน      -> ตัวเติม URL ใช้นามสกุลเดียวทั้งแกลเลอรี
    3. ชื่อไฟล์ไม่เป็นเลข -> Udon ต้องเดาชื่อไฟล์ล่วงหน้าได้
    4. เลขขาดช่วง        -> ต้องเรียง 0,1,2,... ติดกัน
    5. count ไม่ตรง      -> โหลดเกินแล้วรอ 5 วินาทีต่อช่องที่ไม่มีไฟล์

  ค่าเริ่มต้นคือ "ดูเฉย ๆ" ไม่แตะไฟล์
  ใส่ -Fix ถึงจะลงมือแก้จริง

  ตัวอย่าง
    .\tools\fix-galleries.ps1
    .\tools\fix-galleries.ps1 -Fix
    .\tools\fix-galleries.ps1 -Fix -Order byDate
#>
param(
    [switch]$Fix,

    # name   = เรียงตามชื่อไฟล์ (ค่าเริ่มต้น คาดเดาง่าย)
    # byDate = เรียงตามเวลาที่แก้ไขไฟล์ คือลำดับที่เอาเข้ามา
    [ValidateSet("name", "byDate")]
    [string]$Order = "name",

    [int]$MaxSize = 2048
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repo       = Split-Path -Parent $PSScriptRoot
$galleryDir = Join-Path $repo "Paradise\paradise_central_control\gallery"
$indexPath  = Join-Path $galleryDir "galleries.json"

if (-not (Test-Path $indexPath)) {
    Write-Host "ไม่พบ $indexPath" -ForegroundColor Red
    return
}

$index  = Get-Content $indexPath -Raw -Encoding UTF8 | ConvertFrom-Json
$exts   = @(".png", ".jpg", ".jpeg")
$issues = 0
$fixed  = 0

function Set-Count([string]$cfgPath, [int]$n) {
    # เขียน JSON เองให้ฟอร์แมตเหมือนไฟล์อื่นทั้งหมด และคง duration / loop เดิมไว้
    $duration = 10.0
    $loop     = $true
    if (Test-Path $cfgPath) {
        $old = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -ne $old.duration) { $duration = [double]$old.duration }
        if ($null -ne $old.loop)     { $loop     = [bool]$old.loop }
    }

    $durStr  = $duration.ToString("0.0##", [System.Globalization.CultureInfo]::InvariantCulture)
    $loopStr = if ($loop) { "true" } else { "false" }
    $json    = "{`n  `"count`": $n,`n  `"duration`": $durStr,`n  `"loop`": $loopStr`n}`n"

    [System.IO.File]::WriteAllText($cfgPath, $json, (New-Object System.Text.UTF8Encoding $false))
}

function Resolve-GalleryPath([string]$rel) {
    # path ใน galleries.json เทียบกับโฟลเดอร์ gallery/ และมี ../ ได้
    $combined = Join-Path $galleryDir ($rel -replace '/', '\')
    return [System.IO.Path]::GetFullPath($combined)
}

foreach ($g in $index.galleries) {
    $dir    = Resolve-GalleryPath $g.path
    $imgDir = Join-Path $dir "images"
    $cfg    = Join-Path $dir "config.json"

    Write-Host ""
    Write-Host "=== $($g.name) ===" -ForegroundColor Cyan

    if (-not (Test-Path $imgDir)) {
        Write-Host "  ไม่พบโฟลเดอร์ images" -ForegroundColor Red
        $issues++
        continue
    }

    $files = Get-ChildItem $imgDir -File |
             Where-Object { $exts -contains $_.Extension.ToLower() }

    if ($files.Count -eq 0) {
        Write-Host "  ยังไม่มีรูป" -ForegroundColor DarkGray
        # ยังต้องเช็กว่า count เป็น 0 จริงไหม
        if (Test-Path $cfg) {
            $c = (Get-Content $cfg -Raw -Encoding UTF8 | ConvertFrom-Json).count
            if ($c -ne 0) {
                Write-Host "  count = $c แต่ไม่มีรูปเลย" -ForegroundColor Yellow
                $issues++
                if ($Fix) { Set-Count $cfg 0; Write-Host "    -> ตั้ง count = 0" -ForegroundColor Green; $fixed++ }
            }
        }
        continue
    }

    # ---- เรียงลำดับ ----
    if ($Order -eq "byDate") {
        $sorted = $files | Sort-Object LastWriteTime, Name
    } else {
        # ชื่อที่เป็นเลขล้วนเรียงแบบตัวเลข ที่เหลือเรียงตามตัวอักษรต่อท้าย
        $sorted = $files |
            Sort-Object @{ Expression = { if ($_.BaseName -match '^\d+$') { 0 } else { 1 } } },
                        @{ Expression = { if ($_.BaseName -match '^\d+$') { [int]$_.BaseName } else { 0 } } },
                        Name
    }

    # ---- ตรวจ ----
    $extSet     = ($files | ForEach-Object { $_.Extension.ToLower() } | Sort-Object -Unique)
    $mixedExt   = $extSet.Count -gt 1
    $targetExt  = if ($extSet -contains ".png") { ".png" } else { ".jpg" }

    $oversize = @()
    foreach ($f in $files) {
        $im = [System.Drawing.Image]::FromFile($f.FullName)
        if ($im.Width -gt $MaxSize -or $im.Height -gt $MaxSize) { $oversize += $f.Name }
        $im.Dispose()
    }

    $expected = 0..($sorted.Count - 1) | ForEach-Object { "$_$targetExt" }
    $actual   = $sorted | ForEach-Object { $_.Name }
    $needsRename = (Compare-Object $expected $actual -SyncWindow 0) -ne $null

    $count = if (Test-Path $cfg) { (Get-Content $cfg -Raw -Encoding UTF8 | ConvertFrom-Json).count } else { -1 }
    $countOk = ($count -eq $sorted.Count)

    Write-Host ("  รูป {0} ใบ | count {1}" -f $sorted.Count, $count)

    if ($oversize.Count -gt 0) {
        Write-Host ("  เกิน $MaxSize px : " + ($oversize -join ", ")) -ForegroundColor Yellow
        $issues++
    }
    if ($mixedExt)    { Write-Host ("  นามสกุลปนกัน : " + ($extSet -join " ")) -ForegroundColor Yellow; $issues++ }
    if ($needsRename) { Write-Host "  ชื่อไฟล์ไม่เรียง 0..$($sorted.Count - 1)$targetExt" -ForegroundColor Yellow; $issues++ }
    if (-not $countOk){ Write-Host "  count ไม่ตรงจำนวนรูป" -ForegroundColor Yellow; $issues++ }

    if (-not $oversize -and -not $mixedExt -and -not $needsRename -and $countOk) {
        Write-Host "  ปกติ" -ForegroundColor Green
        continue
    }

    if (-not $Fix) {
        Write-Host "  (ใส่ -Fix เพื่อแก้)" -ForegroundColor DarkGray
        continue
    }

    # ---- ซ่อม ----
    # เปลี่ยนชื่อเป็นชื่อชั่วคราวก่อน กันชนกับไฟล์ที่ยังไม่ได้ย้าย
    $tmp = @()
    for ($i = 0; $i -lt $sorted.Count; $i++) {
        $t = Join-Path $imgDir ("__tmp_{0}{1}" -f $i, $sorted[$i].Extension)
        Move-Item $sorted[$i].FullName $t -Force
        $tmp += [pscustomobject]@{ Path = $t; Original = $sorted[$i].Name; Index = $i }
    }

    $names = @()
    foreach ($t in $tmp) {
        $dest = Join-Path $imgDir ("{0}{1}" -f $t.Index, $targetExt)

        $im = [System.Drawing.Image]::FromFile($t.Path)
        $scale = [Math]::Min(1.0, $MaxSize / [double][Math]::Max($im.Width, $im.Height))
        $needConvert = ([System.IO.Path]::GetExtension($t.Path).ToLower() -ne $targetExt)

        if ($scale -lt 1.0 -or $needConvert) {
            $w = [Math]::Max(1, [int][Math]::Round($im.Width * $scale))
            $h = [Math]::Max(1, [int][Math]::Round($im.Height * $scale))

            $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $gr  = [System.Drawing.Graphics]::FromImage($bmp)
            $gr.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $gr.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            if ($targetExt -eq ".jpg") { $gr.Clear([System.Drawing.Color]::Black) }
            $gr.DrawImage($im, 0, 0, $w, $h)
            $gr.Dispose(); $im.Dispose()

            if ($targetExt -eq ".png") {
                $bmp.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
            } else {
                $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
                         Where-Object { $_.MimeType -eq "image/jpeg" }
                $ep = New-Object System.Drawing.Imaging.EncoderParameters(1)
                $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
                    [System.Drawing.Imaging.Encoder]::Quality, [int64]90)
                $bmp.Save($dest, $codec, $ep)
                $ep.Dispose()
            }
            $bmp.Dispose()
            Remove-Item $t.Path -Force
        } else {
            $im.Dispose()
            Move-Item $t.Path $dest -Force
        }

        $names += ("{0}{1}  <-  {2}" -f $t.Index, $targetExt, $t.Original)
        Write-Host ("    {0}{1}  <-  {2}" -f $t.Index, $targetExt, $t.Original) -ForegroundColor Green
    }

    Set-Count $cfg $tmp.Count

    # เก็บไว้ว่าเลขไหนมาจากไฟล์ชื่ออะไร เผื่อแกลเลอรีสตาฟที่ชื่อไฟล์มีความหมาย
    [System.IO.File]::WriteAllText(
        (Join-Path $dir "names.txt"),
        ($names -join "`r`n") + "`r`n",
        (New-Object System.Text.UTF8Encoding $false))

    Write-Host "  -> แก้แล้ว count = $($tmp.Count)" -ForegroundColor Green
    $fixed++
}

Write-Host ""
if ($issues -eq 0) {
    Write-Host "ทุกแกลเลอรีปกติ" -ForegroundColor Green
} elseif ($Fix) {
    Write-Host "พบปัญหา $issues จุด แก้ไปแล้ว $fixed แกลเลอรี" -ForegroundColor Green
} else {
    Write-Host "พบปัญหา $issues จุด — รันซ้ำด้วย -Fix เพื่อแก้" -ForegroundColor Yellow
}
