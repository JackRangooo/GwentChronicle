[CmdletBinding()]
param([string]$RepositoryRoot=(Split-Path -Parent $PSScriptRoot),[string]$PackagePath)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($RepositoryRoot)
$forbiddenFolders='(?i)(^|/)(sessions|diagnostics|match-data|snapshots|pinned-views|deck-scans|android-signing|\.git)(/|$)'
$forbiddenFiles='(?i)(^|/)(settings|installation|upload-identity|appearance|workspace-appearance|match-reviews|local-player-profiles|deck-display-names|deck-editor-drafts|workbook-index)\.json$|\.(gvm|pfx|p12|keystore|bak|part)$|(^|/)\.env($|\.)'
$textExtensions=@('.cs','.xaml','.csproj','.props','.targets','.md','.json','.yml','.yaml','.ps1','.cmd','.config','.sln')
$files=@(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|dist|\.git)[\\/]' })
foreach($file in $files) {
    $relative=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
    if($relative -match $forbiddenFolders -or $relative -match $forbiddenFiles){throw "Private file is not allowed in a public tree: $relative"}
    if($file.Length -ge 100MB){throw "Unexpected large source file: $relative"}
    if($file.Extension.ToLowerInvariant() -in $textExtensions) {
        $text=Get-Content -LiteralPath $file.FullName -Raw
        if($text -match '(?i)\b[A-Z]:[\\/]+(?:Users|xwechat_files)[\\/]+[A-Za-z0-9_.-]+' -or
            $text -match 'gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|sb_secret_[A-Za-z0-9_-]{20,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----')
            {throw "A personal path or plausible secret is present in $relative (content omitted)."}
    }
    if($file.Extension -in '.jpg','.png') {
        $stream=[IO.File]::OpenRead($file.FullName);try{$buffer=[byte[]]::new(64);$read=$stream.Read($buffer,0,64)}finally{$stream.Dispose()}
        if([Text.Encoding]::ASCII.GetString($buffer,0,$read).StartsWith('version https://git-lfs.github.com/spec/')){throw "Unresolved LFS pointer: $relative"}
    }
}
$service=Get-Content -LiteralPath (Join-Path $root 'cache/data-service.json') -Raw | ConvertFrom-Json
if($service.Enabled -or $service.ApiBaseUrl){throw 'The public distribution must not enable a private contribution service.'}
$library=Get-Content -LiteralPath (Join-Path $root 'cache/deck-library.json') -Raw | ConvertFrom-Json
foreach($record in $library.Records) {
    if(@($record.Deck.Occurrences | Where-Object {$_.Kind -in 'Opponent','Observed','Encounter'}).Count){throw 'The default library contains private match occurrences.'}
}
if($PackagePath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))
    try {
        $names=@($archive.Entries | ForEach-Object {$_.FullName.Replace('\','/')})
        foreach($name in $names) {
            if($name -match $forbiddenFolders -or $name -match $forbiddenFiles -or $name.StartsWith('GwentVision/cache/',[StringComparison]::OrdinalIgnoreCase) -or
                $name.StartsWith('/') -or $name.Split('/') -contains '..' -or $name.Contains(':')){throw "Forbidden package entry: $name"}
        }
        foreach($required in @('启动昆特战记.cmd','使用说明.md','LICENSE','UPSTREAM.md','GwentVision/GwentVision.exe',
            'GwentVision/defaults/cache/deck-library.json','GwentVision/defaults/cache/gwent-one-cards.json','GwentVision/vision-assets/score-digits.json')) {
            if($required -notin $names){throw "Missing package entry: $required"}
        }
    } finally {$archive.Dispose()}
}
Write-Host "PASS public source/package privacy: $($files.Count) files; no runtime records, private paths, credentials or unresolved LFS assets."
