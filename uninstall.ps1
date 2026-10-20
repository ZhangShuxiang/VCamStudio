param(
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'VCamStudio'),
    [switch]$RemoveUserData
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份运行 uninstall.ps1。'
}

Get-Process VCamStudio -ErrorAction SilentlyContinue | ForEach-Object {
    $_.CloseMainWindow() | Out-Null
    Start-Sleep -Milliseconds 600
    if (-not $_.HasExited) { $_.Kill() }
}

$exe = Join-Path $InstallDirectory 'VCamStudio.exe'
if (Test-Path $exe) {
    $process = Start-Process -FilePath $exe -ArgumentList '--uninstall' -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        Write-Warning "COM 组件注销返回代码 $($process.ExitCode)，将继续删除程序文件。"
    }
}

$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'VCam Studio.lnk'
if (Test-Path $shortcutPath) { Remove-Item $shortcutPath -Force }

if (Test-Path $InstallDirectory) { Remove-Item $InstallDirectory -Recurse -Force }

if ($RemoveUserData) {
    Remove-Item (Join-Path $env:ProgramData 'VCamStudio') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA 'VCamStudio') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:USERPROFILE '.vcam') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:USERPROFILE '.vcam') -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'VCam Studio 已卸载。' -ForegroundColor Green
