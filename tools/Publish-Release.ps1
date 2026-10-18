[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if((git branch --show-current) -ne 'main'){throw 'Publish from main.'}
    if(git status --porcelain){throw 'Review and commit all intended source changes before publishing. Runtime data must never be staged.'}
    $version=([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version
    $tag="v$version"
    $existing=git tag --list $tag
    if($existing){throw "Tag already exists: $tag. Increment the version for a new release."}
    & (Join-Path $PSScriptRoot 'Build-WindowsRelease.ps1')
    if(git status --porcelain){throw 'Build changed tracked/unignored files; review them before publishing.'}
    git push origin main
    if($LASTEXITCODE){throw 'Source push failed.'}
    git tag -a $tag -m "Release $version"
    if($LASTEXITCODE){throw 'Tag creation failed.'}
    git push origin $tag
    if($LASTEXITCODE){throw 'Tag push failed; inspect the local tag before retrying.'}
    Write-Host "Pushed $tag. GitHub Actions will build and publish the release; check its status before announcing availability."
} finally {Pop-Location}
