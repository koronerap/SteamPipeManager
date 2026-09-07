#requires -Version 5.1
<#
.SYNOPSIS
    Steam Pipe Manager'ı tek dosya, kendi kendine yeten bir exe olarak yayımlar.

.DESCRIPTION
    Çıktı tek bir .exe: kullanıcıda .NET kurulu olmasına gerek yok, yanında
    klasör taşınmıyor. Dil dosyaları exe'ye gömülüdür ve ilk çalıştırmada
    %AppData%\SteamPipeManager\lang altına örnek olarak yazılır.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Output dist -Runtime win-x64
#>
param(
    [string]$Output = "publish",
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\SteamPipeManager.App\SteamPipeManager.App.csproj"
$target = Join-Path $root $Output

Write-Host "Testler çalıştırılıyor…" -ForegroundColor Cyan
dotnet test (Join-Path $root "tests\SteamPipeManager.Core.Tests") --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Testler başarısız; yayımlama durduruldu." }

if (Test-Path $target) { Remove-Item $target -Recurse -Force }

Write-Host "Yayımlanıyor ($Runtime, $Configuration)…" -ForegroundColor Cyan
dotnet publish $project -c $Configuration -r $Runtime -o $target --nologo
if ($LASTEXITCODE -ne 0) { throw "Yayımlama başarısız." }

$exe = Get-ChildItem $target -Filter *.exe | Select-Object -First 1
$size = [math]::Round($exe.Length / 1MB, 1)

Write-Host ""
Write-Host "Hazır: $($exe.FullName)" -ForegroundColor Green
Write-Host "Boyut: $size MB · dosya sayısı: $((Get-ChildItem $target -Recurse -File).Count)"
