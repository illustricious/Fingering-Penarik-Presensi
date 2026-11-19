<#
  Build + tes + publish Fingering menjadi SATU exe (self-contained). Hasil disalin KELUAR dari folder "SOURCE CODE !!" (ke folder induknya, rewrite\).
  Pakai:  powershell -ExecutionPolicy Bypass -File publish.ps1                   # Windows x64 -> ..\Fingering.exe
          powershell -ExecutionPolicy Bypass -File publish.ps1 -Rid linux-x64    # -> ..\Fingering-linux-x64\
  Parameter -SkipTests melewati tes. Tutup Fingering.exe lebih dulu (berkas terkunci saat berjalan).
          -Rid linux-x64 -Deb membuat ..\fingering_<versi>_amd64.deb (self-contained; pembuat .deb: installer\deb\MakeDeb, tanpa dpkg).
          -Installer juga membuat ..\Fingering-Setup-<versi>.exe (Inno Setup 6: ISCC.exe di PATH, %USERPROFILE%\tools\InnoSetup, atau Program Files).
#>
param([string]$Rid = "win-x64", [switch]$SkipTests, [switch]$Installer, [switch]$Deb)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Split-Path -Parent $here
# SDK .NET 10: dari PATH, atau lokasi yang dipasang sesi sebelumnya
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -or -not ((& $dotnet --list-sdks) -match '^10\.')) { throw "SDK .NET 10 tidak ditemukan. Pasang dari https://dot.net (channel 10.0)." }
if ($Rid -notin @('win-x64', 'win-arm64', 'linux-x64', 'osx-x64', 'osx-arm64')) { throw "RID tidak didukung: $Rid" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"
Set-Location $here

if (-not $SkipTests) {
  & $dotnet test "tests/Fingering.Tests"
  if ($LASTEXITCODE -ne 0) { throw "tes gagal; publish dibatalkan" }
}

# CATATAN: publish dengan -o langsung ke folder induk menghasilkan galat CS5001 (penyebab tidak diselidiki).
# Karena itu publish ke artifacts\publish-<rid> dulu, lalu exe disalin keluar dari folder source code.
$stage = Join-Path $here "artifacts\publish-$Rid"
if (Test-Path $stage) {
  $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
  $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $here 'artifacts')) + [IO.Path]::DirectorySeparatorChar
  if (-not $resolvedStage.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Folder stage di luar artifacts' }
  Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
& $dotnet publish "src/Fingering.App" -c Release -r $Rid --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $stage
if ($LASTEXITCODE -ne 0) { throw "publish gagal" }

if ($Rid -eq "win-x64") {
  Copy-Item (Join-Path $stage "Fingering.exe") (Join-Path $out "Fingering.exe") -Force
  $final = Join-Path $out "Fingering.exe"
} else {
  $final = Join-Path $out "Fingering-$Rid"
  New-Item -ItemType Directory -Force $final | Out-Null
  Copy-Item (Join-Path $stage "*") $final -Force -Recurse
}
Write-Host "Selesai -> $final"

if ($Installer) {
  if ($Rid -ne "win-x64") { throw "installer hanya untuk win-x64" }
  $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
  if (-not $iscc) {
    $iscc = @("$env:USERPROFILE\tools\InnoSetup\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
      Where-Object { Test-Path $_ } | Select-Object -First 1
  }
  if (-not $iscc) { throw "ISCC.exe (Inno Setup 6) tidak ditemukan. Pasang dari https://jrsoftware.org/isdl.php" }
  # exe untuk installer = SLIM: framework-dependent satu berkas (tanpa runtime di dalamnya; installer memasang .NET 10 Runtime bila belum ada)
  $slim = Join-Path $here "artifacts\publish-slim-$Rid"
  if (Test-Path $slim) {
    $resolvedSlim = (Resolve-Path -LiteralPath $slim).Path
    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $here 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedSlim.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Folder slim di luar artifacts' }
    Remove-Item -LiteralPath $resolvedSlim -Recurse -Force
  }
  & $dotnet publish "src/Fingering.App" -c Release -r $Rid --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $slim
  if ($LASTEXITCODE -ne 0) { throw "publish slim gagal" }
  $ver = ([xml](Get-Content (Join-Path $here "src\Fingering.App\Fingering.App.csproj"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
  & $iscc "/DMyAppVersion=$ver" "/DPayloadExe=$(Join-Path $slim "Fingering.exe")" (Join-Path $here "installer\Fingering.iss")
  if ($LASTEXITCODE -ne 0) { throw "ISCC gagal" }
  Write-Host "Installer -> $(Join-Path $out "Fingering-Setup-$ver.exe")"
}

if ($Deb) {
  if ($Rid -ne "linux-x64") { throw "-Deb hanya untuk -Rid linux-x64" }
  $ver = ([xml](Get-Content (Join-Path $here "src\Fingering.App\Fingering.App.csproj"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
  $debFile = Join-Path $out "fingering_${ver}_amd64.deb"
  if (Test-Path $debFile) { Remove-Item $debFile }
  & $dotnet run --project (Join-Path $here "installer\deb\MakeDeb") -c Release -- $stage $ver $debFile (Join-Path $here "src\Fingering.App\Assets\icon.png")
  if ($LASTEXITCODE -ne 0) { throw "MakeDeb gagal" }
  Write-Host ".deb -> $debFile"
}
