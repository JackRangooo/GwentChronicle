[CmdletBinding()]
param([string]$RepositoryRoot=(Split-Path -Parent $PSScriptRoot),[switch]$SkipUiSmoke)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($RepositoryRoot)
& (Join-Path $PSScriptRoot 'Test-PublicRelease.ps1') -RepositoryRoot $root
Push-Location $root
try {
    dotnet build GwentCompanion.sln -c Release --nologo
    if($LASTEXITCODE){throw 'Build failed.'}
    dotnet run --project tests/GwentCompanion.Tests/GwentCompanion.Tests.csproj -c Release --no-build -- --release-update-regression
    if($LASTEXITCODE){throw 'Update regression failed.'}
    $version=([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    $info=([xml](Get-Content -LiteralPath (Join-Path $root 'src/GwentCompanion.App/GwentCompanion.App.csproj') -Raw)).Project.PropertyGroup.InformationalVersion
    if($info -notlike "$version-*"){throw 'InformationalVersion and release Version disagree.'}
    if($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne "v$version"){throw 'Tag and source version disagree.'}
    $dist=Join-Path $root 'dist'
    $work=Join-Path $dist ('work-'+[Guid]::NewGuid().ToString('N'))
    $app=Join-Path $work 'GwentVision'
    New-Item -ItemType Directory -Path $app -Force | Out-Null
    dotnet publish src/GwentCompanion.App/GwentCompanion.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $app --nologo
    if($LASTEXITCODE){throw 'Publish failed.'}
    $defaults=Join-Path $app 'defaults/cache'
    New-Item -ItemType Directory -Path $defaults -Force | Out-Null
    foreach($name in @('gwent-one-cards.json','create-point-profiles.json','deck-library.json','data-service.json')) {
        Copy-Item -LiteralPath (Join-Path $root "cache/$name") -Destination $defaults -Force
    }
    foreach($folder in @('portraits','premium-frames','observed-art')) {
        $source=Join-Path $root "cache/$folder"
        if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination $defaults -Recurse}
    }
    Copy-Item -LiteralPath (Join-Path $root 'data') -Destination $app -Recurse
    foreach($name in @('README.md','LICENSE','THIRD_PARTY_NOTICES.md','UPSTREAM.md','CHANGELOG.md')){Copy-Item -LiteralPath (Join-Path $root $name) -Destination $work}
    Copy-Item -LiteralPath (Join-Path $root 'docs/使用说明.md') -Destination (Join-Path $work '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $root 'packaging/启动昆特战记.cmd') -Destination $work
    # Smoke on an isolated sibling. Never initialize runtime cache inside the package.
    if(!$SkipUiSmoke) {
        $smoke=Join-Path $dist ('smoke-'+[Guid]::NewGuid().ToString('N'))
        Copy-Item -LiteralPath $app -Destination $smoke -Recurse
        $process=Start-Process -FilePath (Join-Path $smoke 'GwentVision.exe') -WorkingDirectory $smoke -ArgumentList '--public-release-smoke' -PassThru -WindowStyle Hidden
        if(!$process.WaitForExit(60000)){throw "Isolated smoke still running (PID $($process.Id)); it was not killed."}
        if($process.ExitCode -ne 0){throw "Public release smoke failed. Inspect $smoke/diagnostics/public-release-smoke/result.json"}
        $report=Get-Content -LiteralPath (Join-Path $smoke 'diagnostics/public-release-smoke/result.json') -Raw | ConvertFrom-Json
        if(!$report.Passed){throw 'Public release smoke did not pass.'}
        Write-Host "PASS isolated public startup: $($report.Checks.Count) checks."
    }
    $zip=Join-Path $dist 'GwentChronicle-Windows-x64.zip'
    Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -CompressionLevel Optimal -Force
    & (Join-Path $PSScriptRoot 'Test-PublicRelease.ps1') -RepositoryRoot $root -PackagePath $zip
    $hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($zip+'.sha256',"$hash  GwentChronicle-Windows-x64.zip`n",[Text.UTF8Encoding]::new($false))
    Write-Host "READY v$version : $zip"
} finally {Pop-Location}
