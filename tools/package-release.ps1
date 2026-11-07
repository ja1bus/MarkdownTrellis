# Release用のZIPとSHA-256を publish\release\ に作成する。
# 例: pwsh -File tools\package-release.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$project = Get-Content -LiteralPath (Join-Path $root 'MarkdownTrellis.csproj') -Raw
if ($project -notmatch '<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>') { throw 'Version not found in MarkdownTrellis.csproj' }
$version = $Matches[1]

& (Join-Path $PSScriptRoot 'publish-plugin.ps1')

# LICENSEなどは公開リポジトリならルート、開発リポジトリなら public-repo にある
$docs = if (Test-Path -LiteralPath (Join-Path $root 'LICENSE')) { $root } else { Join-Path $root 'public-repo' }
$files = @(
    (Join-Path $root 'publish\MarkdownTrellis.dll')
    (Join-Path $docs 'LICENSE')
    (Join-Path $docs 'THIRD-PARTY-NOTICES.md')
)

$dllVersion = (Get-Item -LiteralPath $files[0]).VersionInfo.FileVersion
if ($dllVersion -ne "$version.0") { throw "DLL version $dllVersion does not match $version" }

$dllText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($files[0]))
if ($dllText -match '(?i)[A-Z]:\\Users\\') { throw 'The DLL contains a path of the build PC' }

$output = Join-Path $root 'publish\release'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$zipName = "MarkdownTrellis-$version-x64.zip"
$zip = Join-Path $output $zipName
Compress-Archive -LiteralPath $files -DestinationPath $zip -Force

$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $zipName" -Encoding ascii -NoNewline
Write-Output "Created: $zip"
Write-Output "SHA-256: $hash"
