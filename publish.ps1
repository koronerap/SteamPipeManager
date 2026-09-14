#requires -Version 5.1
<#
.SYNOPSIS
    Ürünleri kendi kendine yeten, derli toplu birer klasör olarak yayımlar ve zip'ler.

.DESCRIPTION
    Varsayılan olarak üç ürünün hepsini yayımlar: Steam Pipe Manager, Epic Build Manager
    ve Pipe Manager Hub. Her biri 6 dosya: ürün adını taşıyan exe ve WPF'in yanında
    durması gereken 5 yerel DLL. Kullanıcıda .NET kurulu olmasına gerek yok.

    Yönetilen derlemeler exe'nin içinde ama SIKIŞTIRILMADAN duruyor. Sıkıştırılmış
    tek dosya paketleri çalışırken kendini diske açtığı için SmartScreen ve kurumsal
    virüs taramalarında şüpheli işaretlenip indirilemiyordu; sıkıştırmasız paket
    doğrudan bellekten okunuyor ve bu tetiklemeyi yapmıyor.

    Başta testlerin atlanıp atlanmayacağı sorulur. -SkipTests ya da -RunTests verilirse
    sorulmaz; etkileşimsiz oturumda (CI) testler çalıştırılır.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -SkipTests
    .\publish.ps1 -Product EpicBuildManager -Version 1.0.2-test.1
#>
param(
    # All | SteamPipeManager | EpicBuildManager | PipeManagerHub
    [ValidateSet("All", "SteamPipeManager", "EpicBuildManager", "PipeManagerHub")]
    [string]$Product = "All",

    [string]$Output = "publish",
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",

    # Boşsa csproj'daki sürüm kullanılır. Güncelleyiciyi sınamak için eski/yeni
    # sürüm üretirken işe yarıyor.
    [string]$Version = "",

    # İkisi de verilmezse sorulur.
    [switch]$SkipTests,
    [switch]$RunTests
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\SteamPipeManager.App\SteamPipeManager.App.csproj"
$staging = Join-Path $root $Output
$clock = [Diagnostics.Stopwatch]::StartNew()

if ($SkipTests -and $RunTests) { throw "-SkipTests ile -RunTests birlikte verilemez." }

function Test-Interactive {
    if (-not [Environment]::UserInteractive) { return $false }
    $flags = [Environment]::GetCommandLineArgs() | Where-Object { $_ -like "-NonI*" }
    return -not $flags
}

# Enter varsayılan olarak testleri çalıştırır: yanlışlıkla atlamak, yanlışlıkla
# beklemekten pahalı.
function Read-SkipTests {
    if (-not (Test-Interactive)) {
        Write-Host "Etkileşimsiz oturum: testler çalıştırılacak (atlamak için -SkipTests)."
        return $false
    }

    while ($true) {
        $answer = Read-Host "Testler atlansın mı? [e/H] (Enter = çalıştır, yaklaşık 1-2 dk)"
        if ($null -eq $answer) { return $false }

        switch -Regex ($answer.Trim()) {
            "^$"                       { return $false }
            "^(e|evet|y|yes)$"         { return $true }
            "^(h|hay[ıi]r|n|no)$"      { return $false }
        }

        Write-Host "Lütfen 'e' (atla) ya da 'h' (çalıştır) yazın."
    }
}

$skip = if ($SkipTests) { $true } elseif ($RunTests) { $false } else { Read-SkipTests }

if ($skip) {
    Write-Host "Testler atlandı." -ForegroundColor Yellow
} else {
    Write-Host "Testler çalıştırılıyor…" -ForegroundColor Cyan
    dotnet test (Join-Path $root "tests\SteamPipeManager.Core.Tests") --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Testler başarısız; yayımlama durduruldu." }
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory $staging | Out-Null

# Ürün adları ve exe adları; üçü de aynı koddan çıkıyor, fark markalama ve
# hangi sağlayıcıların açık olduğu. Sıra sabit ki çıktı her seferinde aynı olsun.
$catalog = [ordered]@{
    SteamPipeManager = @{ Name = "Steam Pipe Manager"; Exe = "SteamPipeManager" }
    EpicBuildManager = @{ Name = "Epic Build Manager"; Exe = "EpicBuildManager" }
    PipeManagerHub   = @{ Name = "Pipe Manager Hub";   Exe = "PipeManagerHub"   }
}

$products = if ($Product -eq "All") { @($catalog.Keys) } else { @($Product) }

# Paketler ürüne göre değişmiyor; bir kez geri yükleyip her publish'te atlıyoruz.
Write-Host ""
Write-Host "Paketler geri yükleniyor…" -ForegroundColor Cyan
dotnet restore $project -r $Runtime --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Paket geri yükleme başarısız." }

$extra = @()
if ($Version) { $extra += "-p:Version=$Version" }

# Zip'leme publish'in yarısı kadar sürüyor ve CPU'yu az kullanıyor; bir sonraki ürün
# derlenirken arka planda yapılıyor. Publish'ler ise sırayla: aynı obj klasörünü
# paylaşıyorlar.
$zipJob = {
    param($dir, $zip)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
}

$jobs = @()

foreach ($id in $products) {
    $info = $catalog[$id]
    $productDir = Join-Path $staging $info.Exe
    $productZip = Join-Path $staging "$($info.Exe)-$Runtime.zip"

    Write-Host ""
    Write-Host "Yayımlanıyor: $($info.Name) ($Runtime, $Configuration)…" -ForegroundColor Cyan

    dotnet publish $project -c $Configuration -r $Runtime -o $productDir --nologo --no-restore --verbosity quiet -p:SpmProduct=$id @extra
    if ($LASTEXITCODE -ne 0) { throw "Yayımlama başarısız: $id" }

    # Kullanıcının çift tıklayacağı dosya ürünün adını taşısın.
    Rename-Item (Join-Path $productDir "SteamPipeManager.App.exe") "$($info.Exe).exe"

    $jobs += Start-Job -Name $id -ScriptBlock $zipJob -ArgumentList $productDir, $productZip
}

Write-Host ""
Write-Host "Zip'lerin bitmesi bekleniyor…" -ForegroundColor Cyan
$jobs | Wait-Job | Out-Null

$failed = @($jobs | Where-Object { $_.State -ne "Completed" })
foreach ($job in $failed) {
    Receive-Job $job -ErrorAction Continue
}
$jobs | Remove-Job
if ($failed.Count -gt 0) { throw "Zip'lenemedi: $(($failed | ForEach-Object Name) -join ', ')" }

# Uygulama içi güncelleyicinin doğruladığı sağlama listesi. Bu dosyada adı geçmeyen
# ya da sağlaması tutmayan paket kurulmuyor; yayına zip'lerle birlikte eklenmeli.
$sums = @(Get-ChildItem $staging -Filter "*-$Runtime.zip" | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
})
$sumsPath = Join-Path $staging "SHA256SUMS.txt"
[IO.File]::WriteAllLines($sumsPath, [string[]]$sums, (New-Object Text.UTF8Encoding $false))

Write-Host ""
foreach ($id in $products) {
    $info = $catalog[$id]
    $productDir = Join-Path $staging $info.Exe
    $productZip = Join-Path $staging "$($info.Exe)-$Runtime.zip"

    $files = @(Get-ChildItem $productDir -Recurse -File)
    $rawMb = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 1)
    $zipMb = [math]::Round((Get-Item $productZip).Length / 1MB, 1)

    Write-Host ("  Hazır: {0,-32} {1} MB, {2} dosya, zip {3} MB" -f "$($info.Exe)-$Runtime.zip", $rawMb, $files.Count, $zipMb) -ForegroundColor Green
}

Write-Host ""
Write-Host "Sağlama listesi: $sumsPath" -ForegroundColor Green
Write-Host "  Yayına zip'lerle birlikte SHA256SUMS.txt de yüklenmeli; yoksa uygulama içi güncelleme sunulmaz."
Write-Host ("Toplam süre: {0:N0} sn" -f $clock.Elapsed.TotalSeconds)
