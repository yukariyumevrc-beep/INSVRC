<#
  serve.ps1 - เปิดเว็บเซิร์ฟเวอร์เล็ก ๆ ไว้ดูหน้า gallery ในเครื่องก่อน push
  รันแล้วเปิด http://localhost:8080  (กด Ctrl+C เพื่อหยุด)
#>
param([int]$Port = 8080)

$root = Split-Path -Parent $PSScriptRoot
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()

Write-Host "เสิร์ฟ $root" -ForegroundColor DarkGray
Write-Host "เปิด http://localhost:$Port/  (Ctrl+C เพื่อหยุด)" -ForegroundColor Green

$types = @{
    ".html" = "text/html; charset=utf-8"; ".txt" = "text/plain; charset=utf-8"
    ".jpg"  = "image/jpeg"; ".png" = "image/png"; ".css" = "text/css"; ".js" = "text/javascript"
}

try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()

        # กันเซิร์ฟเวอร์ล้มทั้งตัวเพราะ request เดียวพัง
        try {
            $path = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath).TrimStart('/')
            if ($path -eq "") { $path = "index.html" }
            $file = Join-Path $root $path
            # โฟลเดอร์ -> เสิร์ฟ index.html ข้างใน (เหมือนที่ GitHub Pages ทำ)
            if (Test-Path $file -PathType Container) { $file = Join-Path $file "index.html" }

            if ((Test-Path $file -PathType Leaf) -and $file.StartsWith($root)) {
                $ext = [System.IO.Path]::GetExtension($file).ToLower()
                if ($types.ContainsKey($ext)) { $ctx.Response.ContentType = $types[$ext] }
                $bytes = [System.IO.File]::ReadAllBytes($file)
                $ctx.Response.ContentLength64 = $bytes.Length
                # HEAD ขอแค่ header ห้ามเขียน body ไม่งั้น HttpListener โยน exception
                if ($ctx.Request.HttpMethod -ne "HEAD") {
                    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                }
            } else {
                $ctx.Response.StatusCode = 404
            }
        }
        catch {
            Write-Host ("  คำขอพัง: {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
            try { $ctx.Response.StatusCode = 500 } catch { }
        }
        finally {
            try { $ctx.Response.Close() } catch { }
        }
    }
}
finally { $listener.Stop(); $listener.Close() }
