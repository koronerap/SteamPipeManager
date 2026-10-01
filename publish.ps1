#requires -Version 5.1
<#
.SYNOPSIS
    Ürünleri Windows, Linux ve macOS için yayımlar ve paketler.

.DESCRIPTION
    Üç ürün (Steam Pipe Manager, Epic Build Manager, Pipe Manager Hub) × platformlar:

      Windows  WPF uygulaması, <Ürün>-win-x64.zip
               Exe ve WPF'in yanında durması gereken 5 yerel DLL. Sıkıştırılmış tek
               dosya paketleri SmartScreen'e takıldığı için sıkıştırma yok.
      Linux    Avalonia uygulaması, <Ürün>-linux-x64.tar.gz
      macOS    Avalonia uygulaması, <Ürün>-osx-arm64.tar.gz ve <Ürün>-osx-x64.tar.gz,
               içinde Finder'ın tanıdığı "<Ürün Adı>.app" paketi

    Linux ve macOS paketleri tar.gz: zip çalıştırma iznini taşımıyor, izinsiz açılan bir
    ikili hiç başlamaz. Paketleri tools/Packager yazıyor.

    macOS: .NET uygulamanın kendi dosyasını ad-hoc imzalıyor ama paketin bütününü değil.
    rcodesign (https://github.com/indygreg/apple-platform-rs) bulunursa .app bütünüyle
    ad-hoc imzalanıyor; bulunmazsa paketler imzasız çıkar ve bir uyarı yazılır.
    rcodesign'ın yeri RCODESIGN ortam değişkeniyle de verilebilir. Notarize yok:
    Gatekeeper ilk açılışta onay ister (bkz. README).

    Başta testlerin atlanıp atlanmayacağı sorulur. -SkipTests ya da -RunTests verilirse
    sorulmaz; etkileşimsiz oturumda (CI) testler çalıştırılır.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Platform Windows -SkipTests
    .\publish.ps1 -Product EpicBuildManager -Platform MacOS -Version 1.2.0-test.1
#>
param(
    # All | SteamPipeManager | EpicBuildManager | PipeManagerHub
    [ValidateSet("All", "SteamPipeManager", "EpicBuildManager", "PipeManagerHub")]
    [string]$Product = "All",

    # All | Windows | Linux | MacOS
    [ValidateSet("All", "Windows", "Linux", "MacOS")]
    [string]$Platform = "All",

    [string]$Output = "publish",
    [string]$Configuration = "Release",

    # Boşsa src/Product.props'taki sürüm kullanılır. Güncelleyiciyi sınamak için eski/yeni
    # sürüm üretirken işe yarıyor.
    [string]$Version = "",

    # İkisi de verilmezse sorulur.
    [switch]$SkipTests,
    [switch]$RunTests
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$wpfProject = Join-Path $root "src\SteamPipeManager.App\SteamPipeManager.App.csproj"
$avaloniaProject = Join-Path $root "src\SteamPipeManager.Desktop\SteamPipeManager.Desktop.csproj"
$staging = Join-Path $root $Output
$work = Join-Path $staging ".work"
$icon = Join-Path $root "src\SteamPipeManager.App\app.ico"
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

# Ürün adları, exe adları ve macOS paket kimlikleri; üçü de aynı koddan çıkıyor.
$catalog = [ordered]@{
    SteamPipeManager = @{ Name = "Steam Pipe Manager"; Exe = "SteamPipeManager"; BundleId = "io.github.koronerap.steampipemanager" }
    EpicBuildManager = @{ Name = "Epic Build Manager"; Exe = "EpicBuildManager"; BundleId = "io.github.koronerap.epicbuildmanager" }
    PipeManagerHub   = @{ Name = "Pipe Manager Hub";   Exe = "PipeManagerHub";   BundleId = "io.github.koronerap.pipemanagerhub" }
}

$products = if ($Product -eq "All") { @($catalog.Keys) } else { @($Product) }

$platforms = if ($Platform -eq "All") { @("Windows", "Linux", "MacOS") } else { @($Platform) }

$versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }

# macOS Info.plist'e yazılacak sürüm.
$bundleVersion = if ($Version) { $Version } else {
    (dotnet msbuild $avaloniaProject -getProperty:Version -nologo).Trim()
}

# Paketleyici bir kez derleniyor.
$packagerDir = Join-Path $work "packager"
if ($platforms -contains "Linux" -or $platforms -contains "MacOS") {
    dotnet build (Join-Path $root "tools\Packager\Packager.csproj") -c Release -o $packagerDir --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Paketleyici derlenemedi." }
}
$packager = Join-Path $packagerDir "Packager.exe"

function Invoke-Packager([string[]]$arguments) {
    & $packager @arguments
    if ($LASTEXITCODE -ne 0) { throw "Paketleyici başarısız: $($arguments -join ' ')" }
}

# rcodesign: macOS paketini bütünüyle ad-hoc imzalamak için.
$rcodesign = $env:RCODESIGN
if (-not $rcodesign) {
    $found = Get-Command rcodesign -ErrorAction SilentlyContinue
    if ($found) { $rcodesign = $found.Source }
}
if ($platforms -contains "MacOS" -and -not $rcodesign) {
    Write-Warning "rcodesign bulunamadı: macOS paketleri imzasız çıkacak. Kullanıcılar ilk açılışta 'hasarlı' uyarısı görebilir."
}

# ------------------------------------------------------------------ Windows (WPF)
$jobs = @()
$zipJob = {
    param($dir, $zip)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
}

if ($platforms -contains "Windows") {
    Write-Host ""
    Write-Host "Paketler geri yükleniyor (Windows)…" -ForegroundColor Cyan
    dotnet restore $wpfProject -r win-x64 --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Paket geri yükleme başarısız." }

    foreach ($id in $products) {
        $info = $catalog[$id]
        $productDir = Join-Path $staging $info.Exe
        $productZip = Join-Path $staging "$($info.Exe)-win-x64.zip"

        Write-Host ""
        Write-Host "Yayımlanıyor: $($info.Name) (win-x64)…" -ForegroundColor Cyan

        dotnet publish $wpfProject -c $Configuration -r win-x64 -o $productDir --nologo --no-restore --verbosity quiet -p:SpmProduct=$id @versionArgs
        if ($LASTEXITCODE -ne 0) { throw "Yayımlama başarısız: $id (win-x64)" }

        # Kullanıcının çift tıklayacağı dosya ürünün adını taşısın.
        Rename-Item (Join-Path $productDir "SteamPipeManager.App.exe") "$($info.Exe).exe"

        # Zip'leme bir sonraki ürün derlenirken arka planda yapılıyor.
        $jobs += Start-Job -Name "$id-win" -ScriptBlock $zipJob -ArgumentList $productDir, $productZip
    }
}

# ------------------------------------------------------------------ Linux ve macOS (Avalonia)
$unixRids = @()
if ($platforms -contains "Linux") { $unixRids += "linux-x64" }
if ($platforms -contains "MacOS") { $unixRids += "osx-arm64", "osx-x64" }

foreach ($rid in $unixRids) {
    Write-Host ""
    Write-Host "Paketler geri yükleniyor ($rid)…" -ForegroundColor Cyan
    dotnet restore $avaloniaProject -r $rid --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Paket geri yükleme başarısız: $rid" }

    foreach ($id in $products) {
        $info = $catalog[$id]
        $publishDir = Join-Path $work "$($info.Exe)-$rid"
        $package = Join-Path $staging "$($info.Exe)-$rid.tar.gz"

        Write-Host ""
        Write-Host "Yayımlanıyor: $($info.Name) ($rid)…" -ForegroundColor Cyan

        dotnet publish $avaloniaProject -c $Configuration -r $rid -o $publishDir --nologo --no-restore --verbosity quiet -p:SpmProduct=$id @versionArgs
        if ($LASTEXITCODE -ne 0) { throw "Yayımlama başarısız: $id ($rid)" }

        if ($rid -like "linux-*") {
            # Paketin içinde ürün adıyla bir klasör; ikili çalıştırma izniyle.
            Invoke-Packager @("tar", $publishDir, $package, $info.Exe, $info.Exe, "createdump")
            continue
        }

        # macOS: "<Ürün Adı>.app" paketi.
        $bundle = Join-Path $work "$rid\$($info.Name).app"
        Invoke-Packager @("macapp", $publishDir, $bundle, $info.Name, $info.Exe, $info.BundleId, $bundleVersion, $icon)

        if ($rcodesign) {
            # rcodesign ilerlemesini stderr'e yazıyor; PowerShell 5.1 bunu hata sayardı.
            # Sonuç çıkış kodundan okunuyor.
            $previous = $ErrorActionPreference
            $ErrorActionPreference = "Continue"
            $signLog = & $rcodesign sign $bundle 2>&1 | Out-String
            $signExit = $LASTEXITCODE
            $ErrorActionPreference = $previous

            if ($signExit -ne 0) { throw "rcodesign imzalayamadı: $bundle`n$signLog" }
        }

        Invoke-Packager @("tar", $bundle, $package, "$($info.Name).app", "Contents/MacOS/$($info.Exe)", "Contents/MacOS/createdump")
    }
}

if ($jobs.Count -gt 0) {
    Write-Host ""
    Write-Host "Zip'lerin bitmesi bekleniyor…" -ForegroundColor Cyan
    $jobs | Wait-Job | Out-Null

    $failed = @($jobs | Where-Object { $_.State -ne "Completed" })
    foreach ($job in $failed) { Receive-Job $job -ErrorAction Continue }
    $jobs | Remove-Job
    if ($failed.Count -gt 0) { throw "Zip'lenemedi: $(($failed | ForEach-Object Name) -join ', ')" }
}

if (Test-Path $work) { Remove-Item $work -Recurse -Force }

# Uygulama içi güncelleyicinin doğruladığı sağlama listesi. Bu dosyada adı geçmeyen
# ya da sağlaması tutmayan paket kurulmuyor; yayına paketlerle birlikte eklenmeli.
$packages = @(Get-ChildItem $staging -File | Where-Object { $_.Name -like "*.zip" -or $_.Name -like "*.tar.gz" } | Sort-Object Name)
$sums = @($packages | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
})
$sumsPath = Join-Path $staging "SHA256SUMS.txt"
[IO.File]::WriteAllLines($sumsPath, [string[]]$sums, (New-Object Text.UTF8Encoding $false))

Write-Host ""
foreach ($file in $packages) {
    $mb = [math]::Round($file.Length / 1MB, 1)
    Write-Host ("  Hazır: {0,-40} {1} MB" -f $file.Name, $mb) -ForegroundColor Green
}

Write-Host ""
Write-Host "Sağlama listesi: $sumsPath" -ForegroundColor Green
Write-Host "  Yayına paketlerle birlikte SHA256SUMS.txt de yüklenmeli; yoksa uygulama içi güncelleme sunulmaz."
Write-Host ("Toplam süre: {0:N0} sn" -f $clock.Elapsed.TotalSeconds)
