$ErrorActionPreference='Stop'
$taskOriginal=(Get-Content -LiteralPath tools/Test-Development.ps1 -Raw).Replace("`r`n","`n")
$taskBuild="    Invoke-TaskDotnet 'build.log' @('build','SharpInspect.NET.sln','-c','Release','-p:RestoreLockedMode=true','-m:1')"
$taskTests=@'
    Invoke-TaskDotnet 'tests.log' @('test','SharpInspect.NET.sln','-c','Release','--no-build','--no-restore','-m:1',
        '--logger','trx','--results-directory',(Join-Path $taskRun 'tests'))
'@
$taskTests=$taskTests.Replace("`r`n","`n")
if(-not $taskOriginal.Contains($taskBuild) -or -not $taskOriginal.Contains($taskTests)) { throw 'Expected build/test boundary missing.' }
$taskScript=$taskOriginal.Replace($taskBuild,"    Write-Output 'Build reused from fixed source candidate; solution tests are recorded separately.'").Replace($taskTests,"    Write-Output 'Solution test execution is not part of this post-test scope.'")
$taskScript=$taskScript.Replace('$PSScriptRoot',"'D:\projects\SharpInspect.NET\tools'")
$taskKind="kind = ('V1-{0:D2}-development-validation' -f `$Ticket)"
if(-not $taskScript.Contains($taskKind)) { throw 'Evidence kind marker missing.' }
$taskScript=$taskScript.Replace($taskKind,"kind = 'V1-53-post-test-validation'; executionMode = 'PostTestOnly'; solutionTests = 'RecordedSeparately'; reusedBuildRoot = 'D:\si53v\ticket53\20260914-235726-414'")
$taskScript=$taskScript.Replace('VALIDATION PASS:','POST-TEST SCOPE PASS:')
[IO.File]::WriteAllText((Join-Path (Get-Location) '.scratch/t53/post-test-validation.generated.ps1'),$taskScript,[Text.UTF8Encoding]::new($false))
$taskSource=[ordered]@{source='tools/Test-Development.ps1';sha256=(Get-FileHash -LiteralPath tools/Test-Development.ps1).Hash;
    generated='.scratch/t53/post-test-validation.generated.ps1';generatedSha256=(Get-FileHash -LiteralPath .scratch/t53/post-test-validation.generated.ps1).Hash;
    modifications='Only relocated script root, omitted already-built solution build and ongoing solution test invocation, and distinct post-test-only evidence identity. All package, consumer, migration and source-binding checks retained.'}
$taskSource | ConvertTo-Json | Set-Content -LiteralPath .scratch/t53/post-test-script-provenance.json -Encoding utf8
