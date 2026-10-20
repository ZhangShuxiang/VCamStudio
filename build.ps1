param(
    [switch]$SkipArchive
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Find-DotNet {
    # Prefer the repository-local SDK when present (used by the development environment).
    $local = Join-Path $root '.tmp\dotnet\dotnet.exe'
    if (Test-Path $local) { return $local }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        $sdks = & $command.Source --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $command.Source }
    }

    throw '找不到 .NET 8 SDK。请安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0'
}

$dotnet = Find-DotNet

# Keep build artifacts inside the repository. This also avoids inherited
# profile ACL issues on locked-down machines.
$env:DOTNET_CLI_HOME = Join-Path $root '.build\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.build\nuget'
$env:TEMP = Join-Path $root '.build\temp'
$env:TMP = $env:TEMP
$env:APPDATA = Join-Path $root '.build\appdata\Roaming'
$env:LOCALAPPDATA = Join-Path $root '.build\appdata\Local'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
New-Item -ItemType Directory -Force $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:TEMP, $env:APPDATA, $env:LOCALAPPDATA | Out-Null

Write-Host '==> 还原依赖' -ForegroundColor Cyan
& $dotnet restore (Join-Path $root 'VCamStudio.sln') --configfile (Join-Path $root 'NuGet.config')
if ($LASTEXITCODE -ne 0) { throw "依赖还原失败（退出代码 $LASTEXITCODE）。" }

Write-Host '==> 编译' -ForegroundColor Cyan
& $dotnet build (Join-Path $root 'VCamStudio.sln') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "编译失败（退出代码 $LASTEXITCODE）。" }

$publishDir = Join-Path $root 'dist\VCamStudio'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

Write-Host '==> 发布单文件 WPF 应用' -ForegroundColor Cyan
& $dotnet publish (Join-Path $root 'src\VCamStudio\VCamStudio.csproj') `
    -c Release -r win-x64 --self-contained false --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "发布失败（退出代码 $LASTEXITCODE）。" }

$runtimeFiles = @(Get-ChildItem $publishDir -File | Where-Object {
    $_.Extension -in '.exe', '.dll', '.pdb', '.json'
})
if ($runtimeFiles.Count -ne 1 -or $runtimeFiles[0].Name -ne 'VCamStudio.exe') {
    $names = ($runtimeFiles | ForEach-Object { $_.Name }) -join ', '
    throw "单文件发布失败，发布目录仍包含额外运行时文件：$names"
}

Copy-Item (Join-Path $root 'README.md') $publishDir -Force
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $publishDir -Force
New-Item -ItemType Directory -Force (Join-Path $publishDir 'docs') | Out-Null
Copy-Item (Join-Path $root 'docs\screenshot.png') (Join-Path $publishDir 'docs') -Force

if (-not $SkipArchive) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = Join-Path $root 'dist\VCamStudio-win-x64.zip'
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            if (Test-Path $archive) { Remove-Item $archive -Force }
            [System.IO.Compression.ZipFile]::CreateFromDirectory(
                $publishDir,
                $archive,
                [System.IO.Compression.CompressionLevel]::Optimal,
                $false)
            break
        }
        catch {
            if ($attempt -eq 3) { throw }
            Write-Host "压缩包被占用，1 秒后重试（$attempt/3）..." -ForegroundColor Yellow
            Start-Sleep -Seconds 1
        }
    }
    Write-Host "==> 已生成 $archive" -ForegroundColor Green
}

$exe = Get-Item (Join-Path $publishDir 'VCamStudio.exe')
Write-Host ("==> 单文件应用: {0} ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green
Write-Host '运行 dist\VCamStudio\VCamStudio.exe，首次启动点击“安装 / 修复组件”。'
