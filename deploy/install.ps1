[CmdletBinding()]
param(
    [string]$InstallDir=(Join-Path (Get-Location) 'ProbablyStolenServer'),
    [ValidateRange(1,65535)][int]$Port=17266,
    [switch]$GuestTest,
    [switch]$NoStart,
    [string]$ReleaseBaseUrl='https://github.com/sx050907-png/probably-stolen-server/releases/latest/download'
)
$ErrorActionPreference='Stop'
if($env:OS -ne 'Windows_NT' -or ![Environment]::Is64BitOperatingSystem){throw 'This installer requires Windows x64.'}
$installRoot=[IO.Path]::GetFullPath($InstallDir)
if($installRoot.TrimEnd('\') -eq [IO.Path]::GetPathRoot($installRoot).TrimEnd('\')){throw 'Choose a dedicated installation folder.'}
$binary=Join-Path $installRoot 'app/NightStud.Server.exe'
if(Get-Process | Where-Object {$_.Path -eq $binary}){throw 'Stop this server before updating. Player data has not been changed.'}
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
$stage=Join-Path $installRoot ('.install-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try{
    $asset='nightstud-server-win-x64.zip'
    Invoke-WebRequest -UseBasicParsing -Uri ($ReleaseBaseUrl.TrimEnd('/')+'/SHA256SUMS') -OutFile (Join-Path $stage 'SHA256SUMS')
    Invoke-WebRequest -UseBasicParsing -Uri ($ReleaseBaseUrl.TrimEnd('/')+'/'+$asset) -OutFile (Join-Path $stage $asset)
    $pattern='^([a-fA-F0-9]{64})\s+\*?'+[Regex]::Escape($asset)+'$'
    $matchesFound=@(Get-Content -LiteralPath (Join-Path $stage 'SHA256SUMS') | Where-Object {$_ -match $pattern})
    if($matchesFound.Count -ne 1){throw 'Missing or ambiguous release checksum.'}
    [void]($matchesFound[0] -match $pattern);$expected=$Matches[1]
    $sha256=[Security.Cryptography.SHA256]::Create();$download=[IO.File]::OpenRead((Join-Path $stage $asset))
    try{$actual=[BitConverter]::ToString($sha256.ComputeHash($download)).Replace('-','')}finally{$download.Dispose();$sha256.Dispose()}
    if($actual -ne $expected){throw 'Download checksum mismatch. Existing installation was preserved.'}
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $stage $asset))
    try{foreach($entry in $archive.Entries){if($entry.FullName -match '(^[/\\]|(^|[/\\])\.\.([/\\]|$)|:)'){throw 'Invalid archive path.'}}}finally{$archive.Dispose()}
    $unpacked=Join-Path $stage 'unpacked';[IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $stage $asset),$unpacked)
    if(!(Test-Path -LiteralPath (Join-Path $unpacked 'app/NightStud.Server.exe'))){throw 'Server executable missing from release.'}
    if(Test-Path -LiteralPath (Join-Path $installRoot 'app')){
        $backup=Join-Path $installRoot ('backups/install-'+(Get-Date -Format yyyyMMdd-HHmmss)+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6))
        New-Item -ItemType Directory -Path $backup -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $installRoot 'app') -Destination $backup -Recurse
        foreach($name in @('start.cmd','start.ps1','start.sh')){if(Test-Path -LiteralPath (Join-Path $installRoot $name)){Copy-Item -LiteralPath (Join-Path $installRoot $name) -Destination $backup}}
    }
    foreach($name in @('app','THIRD-PARTY','start.cmd','start.ps1','start.sh','config.env.example','README.md','LICENSE')){
        $source=Join-Path $unpacked $name;if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination $installRoot -Recurse -Force}
    }
    $config=Join-Path $installRoot 'config.env'
    if(!(Test-Path -LiteralPath $config)){
        $guestValue=if($GuestTest){'1'}else{'0'}
        @("NIGHTSTUD_PORT=$Port",'NIGHTSTUD_SCHEME=http',"NIGHTSTUD_ALLOW_GUEST=$guestValue",'NIGHTSTUD_ALLOW_LEGACY_LOGIN=0','NIGHTSTUD_RECONNECT_SECONDS=90') | Set-Content -LiteralPath $config -Encoding ASCII
    }
    Write-Output "Installed: $installRoot"
    Write-Output 'Existing config.env, data and certificates were preserved. Public account login requires HTTPS; GuestTest is an explicit private testing option.'
}finally{
    $checkedStage=[IO.Path]::GetFullPath($stage)
    if(!$checkedStage.StartsWith($installRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($checkedStage) -notlike '.install-*'){throw 'Refusing to remove an unexpected staging path.'}
    if(Test-Path -LiteralPath $checkedStage){Remove-Item -LiteralPath $checkedStage -Recurse -Force}
}
if(!$NoStart){& (Join-Path $installRoot 'start.ps1')}
