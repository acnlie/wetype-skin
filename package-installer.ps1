param(
    [Parameter(Mandatory = $true)][string]$WebView2Installer,
    [string]$BuildDirectory = 'build\windows-installer',
    [string]$ReleaseDirectory = 'Release'
)
$ErrorActionPreference = 'Stop'
function Get-ProjectPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Path))
}
$taskRuntime = [IO.Path]::GetFullPath($WebView2Installer)
$taskSignature = Get-AuthenticodeSignature -LiteralPath $taskRuntime
if ($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
    throw 'WebView2 离线安装器必须具有有效的 Microsoft Corporation 签名。'
}
$taskBuild = Get-ProjectPath $BuildDirectory
$taskRelease = Get-ProjectPath $ReleaseDirectory
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $taskBuild
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskPayload = Join-Path $taskBuild 'payload'
$taskApp = Join-Path $taskPayload 'app'
$taskRuntimeDirectory = Join-Path $taskPayload 'runtime'
New-Item -ItemType Directory -Path $taskApp,$taskRuntimeDirectory,$taskRelease -Force | Out-Null
foreach ($taskName in @('WeTypeSkinStudio.exe','DeepSeekChan.InputPet.exe')) {
    Copy-Item -LiteralPath (Join-Path $taskBuild $taskName) -Destination $taskApp
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $taskApp
$taskLicenses = Join-Path $taskApp 'licenses\whalechan'
New-Item -ItemType Directory -Path $taskLicenses -Force | Out-Null
foreach ($taskName in @('LICENSE.txt','ATTRIBUTION.md','clips.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('assets\pet\' + $taskName)) -Destination $taskLicenses
}
$taskWebViewLicenses = Join-Path $taskApp 'licenses\webview2'
New-Item -ItemType Directory -Path $taskWebViewLicenses -Force | Out-Null
foreach ($taskName in @('LICENSE.txt','NOTICE.txt')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('vendor\webview2\' + $taskName)) -Destination $taskWebViewLicenses
}
Copy-Item -LiteralPath $taskRuntime -Destination (Join-Path $taskRuntimeDirectory 'WebView2OfflineSetup.exe')
$taskOptions = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),
    '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
    '/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$taskSource = Join-Path $PSScriptRoot 'tools\WindowsSetup.cs'
& $taskCompiler $taskOptions ('/out:' + (Join-Path $taskApp 'Uninstall.exe')) $taskSource
if ($LASTEXITCODE -ne 0) { throw '卸载程序编译失败。' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskArchive = Join-Path $taskBuild 'payload.zip'
if (Test-Path -LiteralPath $taskArchive) { Remove-Item -LiteralPath $taskArchive }
[IO.Compression.ZipFile]::CreateFromDirectory($taskPayload,$taskArchive,[IO.Compression.CompressionLevel]::Optimal,$false)
$taskOutput = Join-Path $taskRelease 'WeTypeSkinStudio-Windows-x64.exe'
& $taskCompiler $taskOptions ('/out:' + $taskOutput) ('/win32icon:' + (Join-Path $taskBuild 'app.ico')) ('/resource:' + $taskArchive + ',WeTypeSkinStudio.SetupPayload') $taskSource
if ($LASTEXITCODE -ne 0) { throw 'Windows 安装器编译失败。' }
Get-FileHash -LiteralPath $taskOutput
