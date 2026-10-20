param (
    [switch]$SkipNugetRestore
)

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host " AlpNet Pulse - Otomatik Derleme Sistemi " -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""

# 1. Build Python Translator
Write-Host "[1/5] Çeviri Motoru (Python) Derleniyor..." -ForegroundColor Yellow
if (Test-Path "dist\translator.exe") { Remove-Item -Force "dist\translator.exe" }
pyinstaller translator.spec --noconfirm
if ($LASTEXITCODE -ne 0) { Write-Host "HATA: Python derlemesi başarısız!" -ForegroundColor Red; exit 1 }

Write-Host " -> translator.exe ana dizine kopyalanıyor..." -ForegroundColor Green
Copy-Item -Path "dist\translator.exe" -Destination ".\translator.exe" -Force

# 2. Build C# AlpNetPulse
Write-Host "`n[2/5] AlpNet Pulse (C#) Derleniyor..." -ForegroundColor Yellow
Set-Location "AlpNetPulse"

if ($SkipNugetRestore) {
    dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false --no-restore
} else {
    dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false
}
if ($LASTEXITCODE -ne 0) { Write-Host "HATA: C# derlemesi başarısız!" -ForegroundColor Red; Set-Location ".."; exit 1 }

Set-Location ".."

Write-Host " -> AlpNet.Pulse.exe ana dizine kopyalanıyor..." -ForegroundColor Green
Copy-Item -Path "AlpNetPulse\bin\Release\net9.0-windows\win-x64\publish\AlpNetPulse.exe" -Destination ".\AlpNet.Pulse.exe" -Force
# 3. Build C# AlpNetKeygenUI
Write-Host "`n[3/5] AlpNet Keygen (C#) Derleniyor..." -ForegroundColor Yellow
Set-Location "AlpNetKeygenUI"

if ($SkipNugetRestore) {
    dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false --no-restore
} else {
    dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false
}
if ($LASTEXITCODE -ne 0) { Write-Host "HATA: Keygen derlemesi başarısız!" -ForegroundColor Red; Set-Location ".."; exit 1 }

Set-Location ".."
Write-Host " -> AlpNet.Keygen.exe ana dizine kopyalanıyor..." -ForegroundColor Green
Copy-Item -Path "AlpNetKeygenUI\bin\Release\net9.0-windows\win-x64\publish\AlpNetKeygenUI.exe" -Destination ".\AlpNet.Keygen.exe" -Force
# 4. Build C# AlpNetWebSetup (.NET 4.8 Evrensel Kurulum Aracı)
Write-Host "`n[4/5] AlpNet Web Setup (.NET 4.8) Derleniyor..." -ForegroundColor Yellow
dotnet build AlpNetWebSetup\AlpNetWebSetup.csproj -c Release
if ($LASTEXITCODE -ne 0) { Write-Host "HATA: AlpNetWebSetup derlemesi başarısız!" -ForegroundColor Red; exit 1 }

Write-Host " -> AlpNet.Setup.exe ana dizine kopyalanıyor..." -ForegroundColor Green
Copy-Item -Path "AlpNetWebSetup\bin\Release\net48\AlpNet.Setup.exe" -Destination ".\AlpNet.Setup.exe" -Force

# 5. Build WiX MSI Installer
Write-Host "`n[5/5] MSI Kurulum Paketi (WiX) Oluşturuluyor..." -ForegroundColor Yellow
wix build SetupProject\Package.wxs -ext WixToolset.UI.wixext -o AlpNetPulseInstaller.msi
if ($LASTEXITCODE -ne 0) { Write-Host "HATA: WiX derlemesi başarısız!" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "[OK] BUTUN ISLEMLER BASARIYLA TAMAMLANDI!" -ForegroundColor Cyan
Write-Host "-> Cikti Dosyalari: AlpNet.Setup.exe, AlpNet.Pulse.exe, AlpNet.Keygen.exe ve AlpNetPulseInstaller.msi" -ForegroundColor Cyan
