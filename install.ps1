# devStarter'ı derler ve %LOCALAPPDATA%\Programs\devStarter altına kurar.
# Başlat menüsü + masaüstü kısayolu oluşturur. Güncellemek için tekrar çalıştırman yeterli.
# Kullanım: install.cmd'ye çift tıkla  veya  powershell -ExecutionPolicy Bypass -File install.ps1
$ErrorActionPreference = 'Stop'

$src   = $PSScriptRoot
$dest  = Join-Path $env:LOCALAPPDATA 'Programs\devStarter'
$exe   = Join-Path $dest 'DevStarter.exe'
$built = Join-Path $src 'bin\Release\net10.0-windows\win-x64\publish\DevStarter.exe'

# Çalışan kopya derlenen/kopyalanan exe'yi kilitler; önce kapat (ayarlar zaten her değişiklikte kaydediliyor)
Get-Process DevStarter -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Çalışan devStarter kapatılıyor (PID $($_.Id))..."
    $_.Kill(); $_.WaitForExit(5000) | Out-Null
}

Write-Host 'Derleniyor...'
dotnet publish (Join-Path $src 'DevStarter.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Derleme başarısız.' }

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $built $exe -Force
Write-Host "Kuruldu: $exe"

# Başkalarına gönderilecek kopya (tek dosya, .NET kurulumu gerektirmez)
$dist = Join-Path $src 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item $built (Join-Path $dist 'DevStarter.exe') -Force
Write-Host "Paylaşılacak dosya: $(Join-Path $dist 'DevStarter.exe')"

$shell = New-Object -ComObject WScript.Shell
foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {
    $lnk = $shell.CreateShortcut((Join-Path $dir 'devStarter.lnk'))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $dest
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'devStarter — proje başlatıcı'
    $lnk.Save()
    Write-Host "Kısayol: $(Join-Path $dir 'devStarter.lnk')"
}

# "Windows ile başlat" açıksa kayıt yeni konumu göstersin
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty $run -Name devStarter -ErrorAction SilentlyContinue) {
    Set-ItemProperty $run -Name devStarter -Value "`"$exe`""
    Write-Host 'Windows ile başlat kaydı güncellendi.'
}

Start-Process $exe
Write-Host 'devStarter başlatıldı.'
