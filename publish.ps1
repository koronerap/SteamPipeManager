#requires -Version 5.1
<#
.SYNOPSIS
    Ürünleri kendi kendine yeten, derli toplu birer klasör olarak yayımlar ve zip'ler.

.DESCRIPTION
    Çıktı 6 dosya: SteamPipeManager.exe ve WPF'in yanında durması gereken 5 yerel DLL.
    Kullanıcıda .NET kurulu olmasına gerek yok; dağıtım zip ile yapılıyor.

    Yönetilen derlemeler exe'nin içinde ama SIKIŞTIRILMADAN duruyor. Sıkıştırılmış
    tek dosya paketleri çalışırken kendini diske açtığı için SmartScreen ve kurumsal
    virüs taramalarında şüpheli işaretlenip indirilemiyordu; sıkıştırmasız paket
    doğrudan bellekten okunuyor ve bu tetiklemeyi yapmıyor.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Product PipeManagerHub
    .\publish.ps1 -Product All
#>
param(
    # SteamPipeManager | EpicBuildManager | PipeManagerHub | All
    [ValidateSet("SteamPipeManager", "EpicBuildManager", "PipeManagerHub", "All")]
    [string]$Product = "SteamPipeManager",

    [string]$Output = "publish",
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",

    # Boşsa csproj'daki sürüm kullanılır. Güncelleyiciyi sınamak için eski/yeni
    # sürüm üretirken işe yarıyor.
    [string]$Version = "",

    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\SteamPipeManager.App\SteamPipeManager.App.csproj"
$staging = Join-Path $root $Output

if (-not $SkipTests) {
    Write-Host "Testler çalıştırılıyor…" -ForegroundColor Cyan
    dotnet test (Join-Path $root "tests\SteamPipeManager.Core.Tests") --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Testler başarısız; yayımlama durduruldu." }
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

# Ürün adları ve exe adları; üçü de aynı koddan çıkıyor, fark markalama ve
# hangi sağlayıcıların açık olduğu.
$catalog = @{
    SteamPipeManager = @{ Name = "Steam Pipe Manager"; Exe = "SteamPipeManager" }
    EpicBuildManager = @{ Name = "Epic Build Manager"; Exe = "EpicBuildManager" }
    PipeManagerHub   = @{ Name = "Pipe Manager Hub";   Exe = "PipeManagerHub"   }
}

$products = if ($Product -eq "All") { $catalog.Keys } else { @($Product) }

foreach ($id in $products) {
    $info = $catalog[$id]
    $productDir = Join-Path $staging $info.Exe
    $productZip = Join-Path $staging "$($info.Exe)-$Runtime.zip"

    Write-Host ""
    Write-Host "Yayımlanıyor: $($info.Name) ($Runtime, $Configuration)…" -ForegroundColor Cyan

    $extra = @()
    if ($Version) { $extra += "-p:Version=$Version" }

    dotnet publish $project -c $Configuration -r $Runtime -o $productDir --nologo -p:SpmProduct=$id @extra
    if ($LASTEXITCODE -ne 0) { throw "Yayımlama başarısız: $id" }

    # Kullanıcının çift tıklayacağı dosya ürünün adını taşısın.
    Rename-Item (Join-Path $productDir "SteamPipeManager.App.exe") "$($info.Exe).exe"

    Compress-Archive -Path $productDir -DestinationPath $productZip -CompressionLevel Optimal

    $files = @(Get-ChildItem $productDir -Recurse -File)
    $rawMb = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 1)
    $zipMb = [math]::Round((Get-Item $productZip).Length / 1MB, 1)

    Write-Host "  Hazır: $productZip" -ForegroundColor Green
    Write-Host "  $rawMb MB · $($files.Count) dosya · zip: $zipMb MB"
}

# Uygulama içi güncelleyicinin doğruladığı sağlama listesi. Bu dosyada adı geçmeyen
# ya da sağlaması tutmayan paket kurulmuyor; yayına zip'lerle birlikte eklenmeli.
$sums = @(Get-ChildItem $staging -Filter "*-$Runtime.zip" | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
})
$sumsPath = Join-Path $staging "SHA256SUMS.txt"
[IO.File]::WriteAllLines($sumsPath, [string[]]$sums, (New-Object Text.UTF8Encoding $false))

Write-Host ""
Write-Host "Sağlama listesi: $sumsPath" -ForegroundColor Green
Write-Host "  Yayına zip'lerle birlikte SHA256SUMS.txt de yüklenmeli; yoksa uygulama içi güncelleme sunulmaz."
