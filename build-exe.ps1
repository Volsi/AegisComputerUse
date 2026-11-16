param(
    [switch]$SkipTests,
    [switch]$Unpacked
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$appProj = Join-Path $root "src\AegisComputerUse.App\AegisComputerUse.App.csproj"
$distDir = Join-Path $root "dist\AegisComputerUse"

if (-not $SkipTests) {
    Write-Host "Running security & integration tests..." -ForegroundColor Cyan
    dotnet test (Join-Path $root "AegisComputerUse.slnx") -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed. Aborting EXE build." }
}

if (Test-Path $distDir) {
    try {
        Remove-Item $distDir -Recurse -Force
    } catch {
        Write-Warning "Output directory or executable is currently in use by a running instance: $($_.Exception.Message)"
        $distDir = Join-Path $root "dist\AegisComputerUse_Update"
        if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

$singleFile = if ($Unpacked) { "false" } else { "true" }

Write-Host "Publishing self-contained win-x64 executable to $distDir ..." -ForegroundColor Cyan
dotnet publish $appProj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=$singleFile `
    -p:IncludeNativeLibrariesForSelfExtract=$singleFile `
    -p:EnableCompressionInSingleFile=$singleFile `
    -p:PublishReadyToRun=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $distDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Write-Host "Done! Output:" -ForegroundColor Green
Get-ChildItem $distDir | Select-Object Name, Length
