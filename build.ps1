param([switch]$SkipTests)

$ErrorActionPreference = "Stop"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "未找到 .NET SDK。请安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0"
}

if (-not $SkipTests) {
    dotnet run --project "$PSScriptRoot\tests\SpeedBar.RegressionTests.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "Regression tests failed." }
}

$workspaceRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$output = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot "SpeedBar-optimized"))
if ((Split-Path -Parent $output) -ne $workspaceRoot -or (Split-Path -Leaf $output) -ne 'SpeedBar-optimized') {
    throw "Unexpected publish output path: $output"
}
if (Test-Path -LiteralPath $output) {
    if ((Get-Item -LiteralPath $output).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "Publish directory must not be a reparse point."
    }
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

$releaseDirectory = Join-Path $workspaceRoot 'artifacts\release'
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$releaseExecutable = Join-Path $releaseDirectory 'SpeedBar.exe'
Copy-Item -LiteralPath (Join-Path $output 'SpeedBar.exe') -Destination $releaseExecutable -Force
Compress-Archive -LiteralPath $releaseExecutable -DestinationPath (Join-Path $releaseDirectory 'SpeedBar-v2-win-x64.zip') -Force
$checksums = foreach ($name in @('SpeedBar.exe', 'SpeedBar-v2-win-x64.zip')) {
    $hash = Get-FileHash -LiteralPath (Join-Path $releaseDirectory $name) -Algorithm SHA256
    '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), $name
}
$checksums | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Release files: $releaseDirectory"
