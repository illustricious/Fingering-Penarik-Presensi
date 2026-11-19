param([ValidateSet('win-x64', 'win-arm64')][string]$Rid = 'win-x64')
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '../..')
$version = ([xml](Get-Content src/Fingering.App/Fingering.App.csproj -Raw)).Project.PropertyGroup.Version
$publishDir = Join-Path (Get-Location) "artifacts/publish-$Rid"
$packageDir = Join-Path (Get-Location) 'artifacts/packages'
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
dotnet publish src/Fingering.App -c Release -r $Rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Publish gagal' }
$name = "Fingering-$version-$Rid.exe"
$exe = Join-Path $packageDir $name
Copy-Item -LiteralPath (Join-Path $publishDir 'Fingering.exe') -Destination $exe -Force

# Verify PE Machine, so an ARM64 package cannot accidentally contain x64 code.
$reader = [IO.BinaryReader]::new([IO.File]::OpenRead($exe))
try {
    if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Header MZ tidak valid' }
    $reader.BaseStream.Position = 0x3C
    $peOffset = $reader.ReadUInt32()
    $reader.BaseStream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Header PE tidak valid' }
    $machine = $reader.ReadUInt16()
    $expected = if ($Rid -eq 'win-arm64') { 0xAA64 } else { 0x8664 }
    if ($machine -ne $expected) { throw "Arsitektur PE tidak cocok: $machine" }
} finally { $reader.Dispose() }

# Native runners execute their matching package; redirect output for WinExe.
$versionLog = Join-Path $publishDir 'version.log'
$versionError = Join-Path $publishDir 'version-error.log'
$process = Start-Process -FilePath $exe -ArgumentList '--version' -Wait -PassThru `
    -WindowStyle Hidden -RedirectStandardOutput $versionLog -RedirectStandardError $versionError
if ($process.ExitCode -ne 0) { throw "Executable gagal: $(Get-Content $versionError -Raw)" }
if ((Get-Content $versionLog -Raw).Trim() -ne $version) { throw 'Versi executable tidak sesuai' }
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $packageDir "SHA256SUMS-$Rid.txt"), "$hash  $name`n", [Text.UTF8Encoding]::new($false))
Write-Output "Verified $Rid PE machine, executable version $version, SHA256 $hash"
