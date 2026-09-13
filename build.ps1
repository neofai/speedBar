$ErrorActionPreference = "Stop"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "未找到 .NET SDK。请安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0"
}

$output = Join-Path $PSScriptRoot "SpeedBar-optimized"
if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

dotnet publish "$PSScriptRoot\SpeedBar.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $output `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true

if ($LASTEXITCODE -ne 0) {
    throw "SpeedBar publish failed."
}

$files = @(Get-ChildItem -LiteralPath $output -File)
if ($files.Count -ne 1 -or $files[0].Name -ne "SpeedBar.exe") {
    throw "Publish output is not a single SpeedBar.exe."
}
