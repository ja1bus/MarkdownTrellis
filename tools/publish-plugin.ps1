# プラグインDLLを発行し、指定があればNotepad++のpluginsフォルダへ配置する。
# 例: pwsh -File tools\publish-plugin.ps1 -NppDir 'C:\path\to\npp-portable'
[CmdletBinding()]
param(
    # 配置先のNotepad++のフォルダ（notepad++.exeがある場所）。省略時は発行のみ。
    [string]$NppDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root

# ILCompilerのリンク処理がvswhere.exeを必要とする。システムのPATHは変更しない。
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path -LiteralPath (Join-Path $installer 'vswhere.exe')) {
    $env:Path = "$env:Path;$installer"
}

dotnet publish MarkdownTrellis.csproj -c Release -o publish --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

$dll = Join-Path $root 'publish\MarkdownTrellis.dll'
Write-Output "Published: $dll ($((Get-Item -LiteralPath $dll).Length) bytes)"

if ($NppDir) {
    if (-not (Test-Path -LiteralPath (Join-Path $NppDir 'notepad++.exe'))) {
        throw "notepad++.exe not found: $NppDir"
    }

    if (Get-Process notepad++ -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path (Resolve-Path -LiteralPath $NppDir) 'notepad++.exe') }) {
        throw "Close Notepad++ first: $NppDir"
    }

    $target = Join-Path $NppDir 'plugins\MarkdownTrellis'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -LiteralPath $dll -Destination $target -Force
    Write-Output "Deployed: $target"
}
