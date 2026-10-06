param([string]$BuildDirectory = 'dist', [string]$ArchivePath = 'WeTypeSkinStudio-win-x64.zip')
$ErrorActionPreference = 'Stop'
$taskProjectDir = $PSScriptRoot
$taskDist = if ([IO.Path]::IsPathRooted($BuildDirectory)) { $BuildDirectory } else { Join-Path $taskProjectDir $BuildDirectory }
$taskProjectFullPath = [IO.Path]::GetFullPath($taskProjectDir).TrimEnd('\')
$taskDistFullPath = [IO.Path]::GetFullPath($taskDist).TrimEnd('\')
if ($taskProjectFullPath -eq $taskDistFullPath -or -not $taskDistFullPath.StartsWith($taskProjectFullPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '发布构建目录必须是项目根目录下的独立目录。'
}
if (Test-Path -LiteralPath $taskDist) { Remove-Item -LiteralPath $taskDist -Recurse -Force }
& (Join-Path $taskProjectDir 'build.ps1') -OutputDirectory $taskDist
$taskExe = Join-Path $taskDist 'WeTypeSkinStudio.exe'
if (-not (Test-Path -LiteralPath $taskExe)) { throw '未生成可执行文件。' }
Copy-Item -LiteralPath (Join-Path $taskProjectDir 'README.md') -Destination (Join-Path $taskDist 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $taskProjectDir 'LICENSE') -Destination (Join-Path $taskDist 'LICENSE') -Force
$taskPetNotices = Join-Path $taskDist 'licenses\whalechan'
New-Item -ItemType Directory -Path $taskPetNotices -Force | Out-Null
foreach ($taskNotice in @('LICENSE.txt', 'ATTRIBUTION.md', 'clips.json')) {
    Copy-Item -LiteralPath (Join-Path $taskProjectDir ('assets\pet\' + $taskNotice)) -Destination (Join-Path $taskPetNotices $taskNotice) -Force
}
$taskSkins = Join-Path $taskDist 'skins'
New-Item -ItemType Directory -Path $taskSkins -Force | Out-Null
$taskAssembly = [Reflection.Assembly]::LoadFile($taskExe)
$taskThemeType = $taskAssembly.GetType('WeTypeSkinStudio.SkinTheme')
$taskStoreType = $taskAssembly.GetType('WeTypeSkinStudio.ThemeStore')
$taskPresets = $taskThemeType.GetMethod('Presets').Invoke($null, $null)
foreach ($taskTheme in $taskPresets) {
    $taskPresetPath = [string](Join-Path $taskSkins ($taskTheme.Name + '.wtskin.json'))
    $taskSaveArguments = [object[]]@($taskTheme.PSObject.BaseObject, $taskPresetPath)
    [void]$taskStoreType.GetMethod('Save').Invoke($null, $taskSaveArguments)
}
$taskSourceFiles = @('build.ps1', 'package.ps1', 'app.manifest', 'pet-runtime.manifest', 'LICENSE')
$taskSourceFiles += @(Get-ChildItem -LiteralPath (Join-Path $taskProjectDir 'src') -Filter '*.cs' | ForEach-Object { 'src/' + $_.Name })
$taskSourceFiles += @('frontend/index.html', 'frontend/studio.css', 'frontend/studio.js', 'frontend/lucide.min.js')
$taskSourceFiles += @(Get-ChildItem -LiteralPath (Join-Path $taskProjectDir 'assets\pet') -File | ForEach-Object { 'assets/pet/' + $_.Name })
$taskSourceHashes = [ordered]@{}
foreach ($taskSourceFile in $taskSourceFiles) { $taskSourceHashes[$taskSourceFile] = (Get-FileHash -LiteralPath (Join-Path $taskProjectDir $taskSourceFile)).Hash }
$taskManifest = [ordered]@{ BuiltAt = [DateTime]::UtcNow.ToString('o'); SupportedWeType = '2.1.4.6';
    ExecutableSha256 = (Get-FileHash -LiteralPath $taskExe).Hash;
    PetRuntimeSha256 = (Get-FileHash -LiteralPath (Join-Path $taskDist 'DeepSeekChan.InputPet.exe')).Hash; Sources = $taskSourceHashes;
    Acceptance = 'Build success does not establish real input-method visual acceptance. See docs/VERIFICATION.md in the source repository.' }
[IO.File]::WriteAllText((Join-Path $taskDist 'build-manifest.json'), ($taskManifest | ConvertTo-Json -Depth 4))
$taskArchive = if ([IO.Path]::IsPathRooted($ArchivePath)) { $ArchivePath } else { Join-Path $taskProjectDir $ArchivePath }
New-Item -ItemType Directory -Path (Split-Path -Parent $taskArchive) -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression
$taskStream = [IO.File]::Open($taskArchive, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$taskZip = [IO.Compression.ZipArchive]::new($taskStream, [IO.Compression.ZipArchiveMode]::Create)
try {
$taskFiles = @('WeTypeSkinStudio.exe', 'DeepSeekChan.InputPet.exe', 'README.md', 'LICENSE', 'build-manifest.json')
$taskFiles += @('licenses/whalechan/LICENSE.txt', 'licenses/whalechan/ATTRIBUTION.md', 'licenses/whalechan/clips.json')
foreach ($taskTheme in $taskPresets) {
    $taskFiles += 'skins/' + $taskTheme.Name + '.wtskin.json'
}
    foreach ($taskRelative in $taskFiles) {
        $taskEntry = $taskZip.CreateEntry($taskRelative, [IO.Compression.CompressionLevel]::Optimal)
        $taskEntryStream = $taskEntry.Open()
        $taskSource = [IO.File]::OpenRead((Join-Path $taskDist $taskRelative))
        try { $taskSource.CopyTo($taskEntryStream) } finally { $taskSource.Dispose(); $taskEntryStream.Dispose() }
    }
} finally { $taskZip.Dispose(); $taskStream.Dispose() }
Get-FileHash -LiteralPath $taskArchive | Format-List
