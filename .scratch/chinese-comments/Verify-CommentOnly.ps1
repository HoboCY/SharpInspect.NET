$ErrorActionPreference = 'Stop'
$commentWorkspace = 'D:\projects\SharpInspect.NET'
$commentSdk = 'C:\Program Files\dotnet\sdk\8.0.319\Roslyn\bincore'
[void][Reflection.Assembly]::LoadFrom((Join-Path $commentSdk 'Microsoft.CodeAnalysis.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $commentSdk 'Microsoft.CodeAnalysis.CSharp.dll'))
$commentOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion(
    [Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp10)

function Get-CommentNeutralSignature([string] $source) {
    $normalized = $source.Replace("`r`n", "`n")
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($normalized, $commentOptions,
        '', [Text.Encoding]::UTF8, [Threading.CancellationToken]::None)
    $root = [Microsoft.CodeAnalysis.CSharp.CSharpExtensions]::GetCompilationUnitRoot(
        $tree, [Threading.CancellationToken]::None)
    $signature = [Text.StringBuilder]::new()
    $tokenCount = 0
    foreach ($token in $root.DescendantTokens($null, $false)) {
        [void]$signature.Append($token.RawKind).Append(':').Append($token.Text.Length).Append(':').Append($token.Text)
        $tokenCount++
    }
    $chineseComments = 0
    foreach ($trivia in $root.DescendantTrivia($null, $false)) {
        $kind = [Microsoft.CodeAnalysis.CSharp.SyntaxKind]$trivia.RawKind
        if ($kind -in @('SingleLineCommentTrivia', 'MultiLineCommentTrivia')) {
            if ($trivia.ToFullString() -match '[\u4e00-\u9fff]') { $chineseComments++ }
            continue
        }
        if ($kind -in @('WhitespaceTrivia', 'EndOfLineTrivia')) { continue }
        $text = $trivia.ToFullString()
        [void]$signature.Append($trivia.RawKind).Append(':').Append($text.Length).Append(':').Append($text)
    }
    $errors = @($tree.GetDiagnostics([Threading.CancellationToken]::None) |
        Where-Object { $_.Severity.ToString() -eq 'Error' })
    [pscustomobject]@{
        Hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($signature.ToString())))
        Tokens = $tokenCount
        ChineseComments = $chineseComments
        Errors = $errors.Count
    }
}

function Get-OriginalSource([string] $path) {
    $info = [Diagnostics.ProcessStartInfo]::new('git')
    $info.WorkingDirectory = $commentWorkspace
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    foreach ($argument in @('-c', 'safe.directory=D:/projects/SharpInspect.NET', 'show', "HEAD:$path")) {
        $info.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $source = $process.StandardOutput.ReadToEnd()
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Unable to read HEAD:$path : $errorText" }
        return $source
    } finally { $process.Dispose() }
}

$probeOriginal = Get-CommentNeutralSignature 'class Probe { string Value = "// value"; }'
$probeComment = Get-CommentNeutralSignature "class Probe { // 中文原因说明`n string Value = `"// value`"; }"
$probeChanged = Get-CommentNeutralSignature 'class Probe { string Value = "// changed"; }'
$probeDirective = Get-CommentNeutralSignature "#define CHANGED`nclass Probe { string Value = `"// value`"; }"
if ($probeOriginal.Hash -ne $probeComment.Hash -or $probeOriginal.Hash -eq $probeChanged.Hash -or
    $probeOriginal.Hash -eq $probeDirective.Hash) { throw 'Comment verifier self-check failed.' }

$commentPaths = @(git -c safe.directory=D:/projects/SharpInspect.NET -c core.safecrlf=false diff --name-only HEAD)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate tracked changes.' }
if ($commentPaths.Count -eq 0) { throw 'No changes found.' }
$commentResults = @()
foreach ($commentPath in $commentPaths) {
    if ($commentPath -notmatch '^src/.+\.cs$') { throw "Unexpected changed path: $commentPath" }
    $commentOriginal = Get-OriginalSource $commentPath
    $commentCurrent = [IO.File]::ReadAllText((Join-Path $commentWorkspace $commentPath))
    $before = Get-CommentNeutralSignature $commentOriginal
    $after = Get-CommentNeutralSignature $commentCurrent
    if ($before.Hash -ne $after.Hash -or $before.Tokens -ne $after.Tokens) {
        throw "Non-comment source change: $commentPath"
    }
    if ($before.Errors -ne 0 -or $after.Errors -ne 0) { throw "Syntax errors: $commentPath" }
    $commentResults += [pscustomobject]@{
        File = $commentPath
        TokenCount = $after.Tokens
        Signature = $after.Hash
        ChineseCommentsBefore = $before.ChineseComments
        ChineseCommentsAfter = $after.ChineseComments
        SourceSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [IO.File]::ReadAllBytes((Join-Path $commentWorkspace $commentPath))))
    }
}
git -c safe.directory=D:/projects/SharpInspect.NET -c core.safecrlf=false diff --check
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
$report = [pscustomobject]@{
    Result = 'PASS'
    VerifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    BaseCommit = (git -c safe.directory=D:/projects/SharpInspect.NET rev-parse HEAD)
    Files = $commentResults.Count
    TokensCompared = ($commentResults | Measure-Object TokenCount -Sum).Sum
    ChineseCommentsAdded = ($commentResults | ForEach-Object {
        $_.ChineseCommentsAfter - $_.ChineseCommentsBefore
    } | Measure-Object -Sum).Sum
    Details = $commentResults
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath '.scratch/chinese-comments/verification.json' -Encoding utf8
$report | Select-Object Result,Files,TokensCompared,ChineseCommentsAdded,BaseCommit | ConvertTo-Json -Compress
