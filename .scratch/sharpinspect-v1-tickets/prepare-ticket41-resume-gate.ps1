$ErrorActionPreference = 'Stop'
$t41OriginalPath = Join-Path (Get-Location).Path 'tools\Test-Development.ps1'
$t41Script = Get-Content -LiteralPath $t41OriginalPath -Raw
$t41Needle = @'
    Invoke-TaskDotnet 'tests.log' @('test','SharpInspect.NET.sln','-c','Release','--no-build','--no-restore','-m:1',
        '--logger','trx','--results-directory',(Join-Path $taskRun 'tests'))
'@
$t41Replacement = @'
    & (Join-Path $taskRepo '.scratch\sharpinspect-v1-tickets\reuse-ticket41-passing-suites.ps1') -Run $taskRun
    $taskEvidence.validationMode = 'CompositeWithVerifiedReuseOfSixUnchangedPassingSuites'
    $taskEvidence.originalDevelopmentScriptSha256 = (Get-FileHash -LiteralPath (Join-Path $taskRepo 'tools\Test-Development.ps1')).Hash
    Invoke-TaskDotnet 'tests.log' @('test','tests/SharpInspect.Wpf.Tests/SharpInspect.Wpf.Tests.csproj','-c','Release','--no-build','--no-restore','-m:1',
        '--logger','trx','--results-directory',(Join-Path $taskRun 'tests'))
'@
$t41Script = $t41Script.Replace("`r`n", "`n")
$t41Needle = $t41Needle.Replace("`r`n", "`n")
if (($t41Script.Split($t41Needle).Count - 1) -ne 1) { throw 'Expected the exact full solution test invocation once.' }
$t41Script = $t41Script.Replace($t41Needle, $t41Replacement.Replace("`r`n", "`n"))
$t41Script = $t41Script.Replace('$PSScriptRoot', '$taskToolsRoot')
$t41FirstLine = $t41Script.IndexOf("`n")
$t41Script = $t41Script.Insert($t41FirstLine + 1, '$taskToolsRoot = ''D:\projects\SharpInspect.NET\tools''' + "`n")
$t41Out = Join-Path $PSScriptRoot 'run-ticket41-resumed-development.ps1'
[IO.File]::WriteAllText($t41Out, $t41Script, [Text.UTF8Encoding]::new($false))
[ordered]@{method='Exact development script with only verified suite reuse and WPF rerun replacing solution test invocation';
    originalPath=$t41OriginalPath;originalSha256=(Get-FileHash -LiteralPath $t41OriginalPath).Hash;
    derivedPath=$t41Out;derivedSha256=(Get-FileHash -LiteralPath $t41Out).Hash} |
    ConvertTo-Json | Set-Content -LiteralPath 'F:\temp\SharpInspect.NET-validation-artifacts\ticket41\34-resumed-gate-provenance.json' -Encoding utf8
