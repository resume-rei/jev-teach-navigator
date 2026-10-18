param([switch]$SelfTest)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler がありません。' }
$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Path $bin -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','WPF\WindowsBase.dll','WPF\UIAutomationClient.dll','WPF\UIAutomationTypes.dll','WPF\System.Speech.dll')
$arguments = @('/nologo','/target:winexe','/platform:x64','/utf8output',('/out:' + (Join-Path $bin 'JevTeachWindows.exe')))
foreach ($reference in $references) { $file=Join-Path $framework $reference; if (-not (Test-Path -LiteralPath $file)) { throw "参照がありません: $reference" }; $arguments += ('/reference:' + $file) }
foreach ($source in @('Navigator.cs','GuideEngine.cs','WindowsDesktop.cs','DesktopNavigator.cs','PracticeApps.cs','DesktopEngineTests.cs')) { $arguments += (Join-Path $root $source) }
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw 'Windows拡張のコンパイルに失敗しました。' }
if ($SelfTest) {
  $testArguments = $arguments | ForEach-Object { if ($_ -eq '/target:winexe') { '/target:exe' } elseif ($_ -like '/out:*') { '/out:' + (Join-Path $bin 'JevTeachTests.exe') } else { $_ } }
  & $compiler $testArguments
  if ($LASTEXITCODE -ne 0) { throw '自己テスト用のコンパイルに失敗しました。' }
  & (Join-Path $bin 'JevTeachTests.exe') --self-test
  if ($LASTEXITCODE -ne 0) { throw '自己テストに失敗しました。' }
}
Write-Output ('Build: ' + (Join-Path $bin 'JevTeachWindows.exe'))
