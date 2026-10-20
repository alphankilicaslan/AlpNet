Add-Type -AssemblyName System.Drawing
$logo = [System.Drawing.Image]::FromFile("C:\Users\Alphan.MonsterAlphan\Desktop\ethernet\Alphan Logo.PNG")

# WixUIDialogBmp: 493x312
$dialogBmp = New-Object System.Drawing.Bitmap(493, 312)
$g1 = [System.Drawing.Graphics]::FromImage($dialogBmp)
$g1.Clear([System.Drawing.Color]::White)
$leftBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(21, 23, 30))
$g1.FillRectangle($leftBrush, 0, 0, 164, 312)
$logoWidth = 140
$logoHeight = 140
$x = [int]((164 - $logoWidth) / 2)
$y = [int]((312 - $logoHeight) / 2)
$g1.DrawImage($logo, $x, $y, $logoWidth, $logoHeight)
$dialogBmp.Save("C:\Users\Alphan.MonsterAlphan\Desktop\ethernet\SetupProject\dialog.bmp", [System.Drawing.Imaging.ImageFormat]::Bmp)
$g1.Dispose()
$dialogBmp.Dispose()

# WixUIBannerBmp: 493x58
$bannerBmp = New-Object System.Drawing.Bitmap(493, 58)
$g2 = [System.Drawing.Graphics]::FromImage($bannerBmp)
$g2.Clear([System.Drawing.Color]::White)
$g2.DrawImage($logo, 493 - 48 - 5, 5, 48, 48)
$bannerBmp.Save("C:\Users\Alphan.MonsterAlphan\Desktop\ethernet\SetupProject\banner.bmp", [System.Drawing.Imaging.ImageFormat]::Bmp)
$g2.Dispose()
$bannerBmp.Dispose()
$logo.Dispose()
