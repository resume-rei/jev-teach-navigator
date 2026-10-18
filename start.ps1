param([ValidateRange(1024,65535)][int]$Port=4327)
$ErrorActionPreference='Stop'
Set-Location -LiteralPath $PSScriptRoot
$node=Get-Command node -ErrorAction Stop
$major=[int]((& $node.Source --version).TrimStart('v').Split('.')[0])
if($major -lt 22){throw 'Node.js 22以上が必要です。'}
$previousPort=$env:PORT
try {$env:PORT=[string]$Port;& $node.Source server.mjs} finally {$env:PORT=$previousPort}
