$ErrorActionPreference='Stop'
Set-Location -LiteralPath $PSScriptRoot
$config=Join-Path $PSScriptRoot 'config.env'
if(Test-Path -LiteralPath $config){
    foreach($line in Get-Content -LiteralPath $config -Encoding UTF8){
        $text=$line.Trim()
        if(!$text -or $text.StartsWith('#')){continue}
        if($text -notmatch '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$'){throw 'config.env 中存在无效配置行。'}
        $key=$Matches[1];$value=$Matches[2].Trim()
        if($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'")))){$value=$value.Substring(1,$value.Length-2)}
        [Environment]::SetEnvironmentVariable($key,$value,'Process')
    }
}
$port=0
$nightstudPortValue=if($env:SERVER_PORT){$env:SERVER_PORT}else{$env:NIGHTSTUD_PORT}
if(![int]::TryParse($nightstudPortValue,[ref]$port) -or $port -lt 1 -or $port -gt 65535){throw '平台 SERVER_PORT 或备用 NIGHTSTUD_PORT 需要有效端口，范围 1–65535。'}
if($env:NIGHTSTUD_ALLOW_LEGACY_LOGIN -eq '1' -and (!$env:NIGHTSTUD_ACCESS_KEY -or $env:NIGHTSTUD_ACCESS_KEY.Length -lt 16 -or $env:NIGHTSTUD_ACCESS_KEY.StartsWith('replace-'))){throw '旧版测试入口需要至少 16 个字符的 NIGHTSTUD_ACCESS_KEY。'}
$scheme=if($env:NIGHTSTUD_SCHEME){$env:NIGHTSTUD_SCHEME}else{'http'}
if($scheme -notin @('http','https')){throw 'NIGHTSTUD_SCHEME 请选择 http 或 https。'}
$env:ASPNETCORE_ENVIRONMENT='Production'
$env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT='1'
$env:DOTNET_EnableDiagnostics='0'
Write-Output "可能是赌的独立联机服务 / TCP $port"
Set-Location -LiteralPath (Join-Path $PSScriptRoot 'app')
& ./NightStud.Server.exe
exit $LASTEXITCODE
