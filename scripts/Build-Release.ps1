param(
    [string]$Version,
    [string]$RepositoryUrl = 'https://github.com/michailr1/Model-Manager-LMM-',
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../artifacts')
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $projectRoot 'src/LocalModelManager.App/LocalModelManager.App.csproj'
if (!$Version) { $Version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have the form 0.6.0 (stable releases only).' }
if ($RepositoryUrl -notmatch '^https://github\.com/[A-Za-z0-9_-][A-Za-z0-9_.-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*$') { throw 'Use a public https://github.com/owner/repository URL.' }
$artifactRoot = [IO.Path]::GetFullPath($OutputRoot)
# Separate directories for each version; never mix feed assets from older builds.
$publishDir = Join-Path $artifactRoot "$Version/publish"
$releaseDir = Join-Path $artifactRoot "$Version/releases"
if (Test-Path -LiteralPath $releaseDir) {
    throw "Release directory already exists: $releaseDir. Use a new OutputRoot to rebuild without mixing packages."
}
$toolsDir = Join-Path $projectRoot '.tools'
$vpk = Join-Path $toolsDir 'vpk.exe'
if (!(Test-Path -LiteralPath $vpk)) {
    dotnet tool install vpk --version 1.2.0 --tool-path $toolsDir
    if ($LASTEXITCODE -ne 0) { throw 'Could not install vpk.' }
}
# A self-contained package runs without a separately installed .NET runtime.
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir "-p:Version=$Version" "-p:GitHubRepositoryUrl=$RepositoryUrl" -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
$packArgs = @('pack', '--packId', 'LocalModelManager', '--packVersion', $Version,
    '--packDir', $publishDir, '--mainExe', 'LocalModelManager.App.exe', '--runtime', 'win-x64',
    '--channel', 'win-x64', '--packTitle', 'Local Model Manager', '--packAuthors', 'michailr1',
    '--outputDir', $releaseDir, '--delta', 'None')
$notes = Join-Path $projectRoot "docs/releases/$Version.md"
if (Test-Path -LiteralPath $notes) { $packArgs += @('--releaseNotes', $notes) }
& $vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }
Get-ChildItem -LiteralPath $releaseDir -File | Select-Object Name,Length
