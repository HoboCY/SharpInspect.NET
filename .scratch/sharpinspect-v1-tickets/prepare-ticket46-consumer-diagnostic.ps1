$ErrorActionPreference = 'Stop'
$taskSource = Get-Content -LiteralPath '.scratch/sharpinspect-v1-tickets/diagnose-ticket45-consumer.ps1' -Raw
$taskInjection = Get-Content -LiteralPath '.scratch/sharpinspect-v1-tickets/ticket46-consumer-instrumentation.ps1.txt' -Raw
$taskWrite = '[IO.File]::WriteAllText($taskProgramPath, $taskProgram)'
if ($taskSource.Split($taskWrite,[StringSplitOptions]::None).Count -ne 2) { throw 'Diagnostic write shape changed.' }
$taskSource = $taskSource.Replace($taskWrite,$taskInjection + "`n" + $taskWrite)
$taskDestination = Join-Path (Get-Location) '.scratch/sharpinspect-v1-tickets/diagnose-ticket46-consumer.ps1'
[IO.File]::WriteAllText($taskDestination,$taskSource,[Text.UTF8Encoding]::new($false))
$taskErrors=$null; $taskTokens=$null
[void][Management.Automation.Language.Parser]::ParseFile($taskDestination,[ref]$taskTokens,[ref]$taskErrors)
if ($taskErrors.Count) { throw ($taskErrors | Out-String) }
