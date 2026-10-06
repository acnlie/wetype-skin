param(
    [string[]]$Path = @('build', 'dist', 'artifacts', 'WeTypeSkinStudio-win-x64', 'WeTypeSkinStudio-win-x64.zip', 'research\results', 'research\__pycache__')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
foreach ($relative in $Path) {
    $target = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if ($target -eq [IO.Path]::GetFullPath($root) -or -not $target.StartsWith([IO.Path]::GetFullPath($root).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理项目根目录之外的路径：$target"
    }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}
