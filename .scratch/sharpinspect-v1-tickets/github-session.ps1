function Invoke-TicketGithubSession {
    param([Parameter(Mandatory = $true)][scriptblock]$Action)

    $taskGh = Join-Path $env:TEMP 'sharpinspect-to-spec-tools-20260907\gh-2.100.0\bin\gh.exe'
    if (-not (Test-Path -LiteralPath $taskGh)) {
        $taskGh = 'F:\temp\SharpInspect.NET-task-tools\gh-2.100.0\extracted\bin\gh.exe'
    }
    if (-not (Test-Path -LiteralPath $taskGh)) {
        $taskGh = (Get-Command gh -ErrorAction Stop).Source
    }
    $priorToken = $env:GH_TOKEN
    $priorPrompt = $env:GIT_TERMINAL_PROMPT
    $priorInteractive = $env:GCM_INTERACTIVE
    try {
        $env:GIT_TERMINAL_PROMPT = '0'
        $env:GCM_INTERACTIVE = 'never'
        $credentialLines = @("protocol=https`nhost=github.com`n`n" | git -c credential.interactive=never credential fill 2>$null)
        if ($LASTEXITCODE -ne 0) { throw 'Existing GitHub authentication unavailable.' }
        $passwordLine = $credentialLines | Where-Object { $_.StartsWith('password=') } | Select-Object -First 1
        if (-not $passwordLine) { throw 'Existing GitHub authentication unavailable.' }
        $env:GH_TOKEN = $passwordLine.Substring(9)
        $credentialLines = $null
        $passwordLine = $null
        & $Action $taskGh
    }
    finally {
        $env:GH_TOKEN = $priorToken
        $env:GIT_TERMINAL_PROMPT = $priorPrompt
        $env:GCM_INTERACTIVE = $priorInteractive
    }
}
