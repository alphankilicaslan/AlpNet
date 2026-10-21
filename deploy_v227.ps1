Stop-Process -Name AlpNet.Pulse -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$src = "C:\Users\Alphan.MonsterAlphan\Desktop\AlpNet\AlpNetPulse\bin\Release\net9.0-windows\win-x64\publish\AlpNetPulse.exe"
$destAppData = "C:\Users\Alphan.MonsterAlphan\AppData\Local\AlpNet\AlpNet.Pulse.exe"
$destRoot = "C:\Users\Alphan.MonsterAlphan\Desktop\AlpNet\AlpNet.Pulse.exe"

Copy-Item $src $destAppData -Force
Copy-Item $src $destRoot -Force

$item = Get-Item $destAppData
Write-Host "Copied to AppData: Length=$($item.Length), Version=$($item.VersionInfo.FileVersion)"

$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut("C:\Users\Alphan.MonsterAlphan\Desktop\AlpNet Pulse.lnk")
$lnk.TargetPath = $destAppData
$lnk.WorkingDirectory = "C:\Users\Alphan.MonsterAlphan\AppData\Local\AlpNet"
$lnk.IconLocation = "$destAppData,0"
$lnk.Save()
Write-Host "Desktop Shortcut updated successfully."

Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "AlpNetPulse" -Value "`"$destAppData`" /autostart"
Write-Host "Startup Run key verified."
