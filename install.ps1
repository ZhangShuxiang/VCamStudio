param(
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'VCamStudio'),
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $PSScriptRoot 'dist\VCamStudio\VCamStudio.exe'))) {
    throw '找不到 dist\VCamStudio，请先运行 .\build.ps1。'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份运行 install.ps1（右键 → 以管理员身份运行）。'
}

$runtimeRoot = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'
$runtime = Get-ChildItem $runtimeRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '8.*' }
if (-not $runtime) {
    throw '未检测到 .NET 8 Desktop Runtime。请先安装：https://dotnet.microsoft.com/download/dotnet/8.0'
}

Get-Process VCamStudio -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "停止正在运行的 VCam Studio (PID $($_.Id))..."
    $_.CloseMainWindow() | Out-Null
    Start-Sleep -Milliseconds 600
    if (-not $_.HasExited) { $_.Kill() }
}

New-Item -ItemType Directory -Force $InstallDirectory | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'dist\VCamStudio\*') $InstallDirectory -Recurse -Force

Write-Host '注册虚拟摄像头 COM 组件...'
$process = Start-Process -FilePath (Join-Path $InstallDirectory 'VCamStudio.exe') `
    -ArgumentList '--install' -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "组件注册失败（退出代码 $($process.ExitCode)）。请查看 $env:ProgramData\VCamStudio\logs。"
}

$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'VCam Studio.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $InstallDirectory 'VCamStudio.exe'
$shortcut.WorkingDirectory = $InstallDirectory
$shortcut.IconLocation = Join-Path $InstallDirectory 'VCamStudio.exe,0'
$shortcut.Description = 'VCam Studio 多路虚拟摄像头'
$shortcut.Save()

Write-Host "安装完成：$InstallDirectory" -ForegroundColor Green
Write-Host "开始菜单快捷方式：$shortcutPath"

if (-not $NoLaunch) {
    Start-Process -FilePath (Join-Path $InstallDirectory 'VCamStudio.exe')
}
