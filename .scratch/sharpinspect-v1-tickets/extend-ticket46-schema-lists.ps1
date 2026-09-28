$ErrorActionPreference = 'Stop'
$taskRoot = 'D:\projects\SharpInspect.NET'
$taskExcluded = @('IdentityAuditEvent.cs', 'RecipeActivations.cs')
$taskChanged = [System.Collections.Generic.List[string]]::new()
foreach ($taskRelative in (& rg --files "$taskRoot\src\SharpInspect.Runtime" -g '*.cs')) {
    if ([System.IO.Path]::GetFileName($taskRelative) -in $taskExcluded) { continue }
    $taskBefore = [System.IO.File]::ReadAllText($taskRelative)
    # These are closed schema alternatives, not enum values or capability grants.
    $taskAfter = $taskBefore.Replace('or ProductionRecoveryStoreOptions.SchemaVersion',
        'or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion')
    $taskAfter = $taskAfter.Replace('or 29 or 30', 'or 29 or 30 or 31')
    if ($taskAfter -ne $taskBefore) {
        [System.IO.File]::WriteAllText($taskRelative, $taskAfter, [System.Text.UTF8Encoding]::new($false))
        $taskChanged.Add([System.IO.Path]::GetRelativePath($taskRoot, $taskRelative))
    }
}
$taskChanged | ConvertTo-Json | Set-Content -LiteralPath "$taskRoot\.scratch\sharpinspect-v1-tickets\ticket46-schema-list-edits.json" -Encoding utf8
Write-Output ('Extended closed schema alternatives in {0} files; IdentityAuditEvent remains worker-owned.' -f $taskChanged.Count)
