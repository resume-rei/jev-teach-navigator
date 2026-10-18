$ErrorActionPreference='Stop'
$record=Join-Path $PSScriptRoot 'artifacts\runtime.json'
if(-not (Test-Path -LiteralPath $record)){throw 'この作業で背景起動したサーバーの情報がありません。'}
$runtime=Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
if($runtime.project -ne $PSScriptRoot){throw '別のプロジェクトの起動情報です。'}
$running=Get-Process -Id $runtime.pid -ErrorAction SilentlyContinue
if(-not $running){Write-Output '試作サーバーは停止済みです。';exit}
if($running.StartTime.ToUniversalTime().Ticks -ne $runtime.startTicks -or $running.Path -ne $runtime.executable){throw '同じPIDの別プロセスなので停止しません。'}
$listener=Get-NetTCPConnection -LocalPort $runtime.port -State Listen -ErrorAction Stop
if($listener.OwningProcess -notcontains $running.Id){throw '対象ポートの所有プロセスが違います。'}
Stop-Process -Id $running.Id
Write-Output 'この作業で背景起動した試作サーバーを停止しました。'
