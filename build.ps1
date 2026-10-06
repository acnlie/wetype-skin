param([string]$OutputDirectory = "dist")
$ErrorActionPreference = 'Stop'
$taskProjectDir = $PSScriptRoot
$taskOutputDir = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $taskProjectDir $OutputDirectory }
$taskProjectFullPath = [IO.Path]::GetFullPath($taskProjectDir).TrimEnd('\\')
$taskOutputFullPath = [IO.Path]::GetFullPath($taskOutputDir).TrimEnd('\\')
if ($taskProjectFullPath -eq $taskOutputFullPath) { throw '输出目录不能是项目根目录。' }
New-Item -ItemType Directory -Path $taskOutputDir -Force | Out-Null
$taskCompilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$taskCompiler = $taskCompilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $taskCompiler) { throw '找不到 Windows 自带的 .NET Framework 4.x csc.exe。请安装 .NET Framework 4.8 Developer Pack 或在 Windows 开发环境中运行。' }

Add-Type -AssemblyName System.Drawing
$taskIconBitmap = New-Object -TypeName System.Drawing.Bitmap -ArgumentList 64,64
$taskGraphics = [System.Drawing.Graphics]::FromImage($taskIconBitmap)
$taskGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$taskBrush = New-Object -TypeName System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(34,111,76))
$taskGraphics.FillEllipse($taskBrush,2,2,60,60)
$taskFont = New-Object -TypeName System.Drawing.Font -ArgumentList 'Segoe UI',29,([System.Drawing.FontStyle]::Bold)
$taskGraphics.DrawString('W',$taskFont,[System.Drawing.Brushes]::White,5,6)
$taskPng = New-Object -TypeName System.IO.MemoryStream
$taskIconBitmap.Save($taskPng,[System.Drawing.Imaging.ImageFormat]::Png)
$taskIconPath = Join-Path $taskOutputDir 'app.ico'
$taskIconStream = [System.IO.File]::Create($taskIconPath)
$taskWriter = New-Object -TypeName System.IO.BinaryWriter -ArgumentList $taskIconStream
try {
    $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]1)
    $taskWriter.Write([byte]64); $taskWriter.Write([byte]64); $taskWriter.Write([byte]0); $taskWriter.Write([byte]0)
    $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32); $taskWriter.Write([uint32]$taskPng.Length); $taskWriter.Write([uint32]22)
    $taskWriter.Write($taskPng.ToArray())
} finally { $taskWriter.Dispose(); $taskPng.Dispose(); $taskFont.Dispose(); $taskBrush.Dispose(); $taskGraphics.Dispose(); $taskIconBitmap.Dispose() }

$taskSources = @(Get-ChildItem -LiteralPath (Join-Path $taskProjectDir 'src') -Filter '*.cs' | Where-Object { $_.Name -ne 'PetRuntimeProgram.cs' } | ForEach-Object { $_.FullName })
$taskReferences = @()
$taskResources = @()
foreach ($taskLibrary in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll')) {
    $taskLibraryPath = Join-Path $taskProjectDir ('vendor\webview2\' + $taskLibrary)
    if (-not (Test-Path -LiteralPath $taskLibraryPath)) { throw ('缺少 WebView2 依赖：' + $taskLibraryPath) }
    $taskResources += '/resource:' + $taskLibraryPath + ',WeTypeSkinStudio.Dependencies.' + $taskLibrary
    if ($taskLibrary -ne 'WebView2Loader.dll') { $taskReferences += '/r:' + $taskLibraryPath }
}
foreach ($taskAsset in @('index.html', 'studio.css', 'studio.js', 'lucide.min.js')) {
    $taskResources += '/resource:' + (Join-Path $taskProjectDir ('frontend\' + $taskAsset)) + ',WeTypeSkinStudio.Frontend.' + $taskAsset
}
$taskPetResources = @()
foreach ($taskPetAsset in Get-ChildItem -LiteralPath (Join-Path $taskProjectDir 'assets\pet') -File) {
    $taskPetResources += '/resource:' + $taskPetAsset.FullName + ',WeTypeSkinStudio.Pet.' + $taskPetAsset.Name
}
$taskPetExecutable = Join-Path $taskOutputDir 'DeepSeekChan.InputPet.exe'
$taskPetSources = @('PetRuntimeProgram.cs', 'PetRuntimeProtocol.cs', 'WhalePet.cs', 'PetToolbarProbe.cs', 'Theme.cs', 'Native.cs', 'WeTypeInstallation.cs', 'EmbeddedResources.cs', 'JsonFile.cs') | ForEach-Object { Join-Path $taskProjectDir ('src\' + $_) }
& $taskCompiler /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /utf8output "/out:$taskPetExecutable" "/win32manifest:$(Join-Path $taskProjectDir 'pet-runtime.manifest')" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll $taskPetResources $taskPetSources
if ($LASTEXITCODE -ne 0) { throw '独立桌宠组件编译失败。' }
$taskResources += $taskPetResources
$taskResources += '/resource:' + $taskPetExecutable + ',WeTypeSkinStudio.Dependencies.DeepSeekChan.InputPet.exe'
$taskExecutable = Join-Path $taskOutputDir 'WeTypeSkinStudio.exe'
& $taskCompiler /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /utf8output "/out:$taskExecutable" "/win32manifest:$(Join-Path $taskProjectDir 'app.manifest')" "/win32icon:$taskIconPath" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll /r:System.ServiceProcess.dll $taskReferences $taskResources $taskSources
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Write-Output "构建完成：$taskExecutable"
