$ErrorActionPreference = 'Stop'
$readerFiles = @(Get-ChildItem -LiteralPath 'src/SharpInspect.Runtime/Storage' -Filter 'Sqlite*Query.cs' |
    Where-Object Name -ne 'SqliteStationQualificationHistoryQuery.cs')
$readerFiles += Get-Item -LiteralPath 'src/SharpInspect.Runtime/Integrity/SqliteAuditIntegrityQuery.cs'
foreach ($readerFile in $readerFiles) {
    $readerText = [IO.File]::ReadAllText($readerFile.FullName)
    if ($readerText.Contains('StationQualificationReadGuard.')) { throw 'Reader patch already applied.' }
    $readerText = [regex]::Replace($readerText, '\bor(\s+)ProductionAdmissionStoreOptions\.SchemaVersion',
        'or${1}ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion')
    $readerText = $readerText.Replace('or 21 or 22', 'or 21 or 22 or 23')
    $readerText = [regex]::Replace($readerText,
        '(productionAdmissionOptions:\s*)schema == ProductionAdmissionStoreOptions\.SchemaVersion',
        '${1}schema is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion')
    $readerText = [regex]::Replace($readerText,
        '(SqliteNative\.ConfigureSqliteLimit\((database|db), (_?options), (schemaVersion|schema)\);)',
        '$1' + "`r`n" + '        StationQualificationReadGuard.RequireConfiguration($4, $3);')
    $readerOptionsName = if ($readerFile.Name -eq 'SqliteCommandTraceQuery.cs') { 'options' } else { '_options' }
    $readerDatabaseName = if ($readerFile.Name -eq 'SqliteAuditIntegrityQuery.cs') { 'db' } else { 'database' }
    $readerReportName = if ($readerFile.Name -in @('SqliteAuditIntegrityQuery.cs', 'SqliteCommandTraceQuery.cs')) { 'report' } else { 'verification' }
    $readerReplacement = 'productionAdmissionOptions: $1,' + "`r`n" +
        '                stationQualificationOptions: ' + $readerOptionsName + '.StationQualifications);' + "`r`n" +
        '            StationQualificationReadGuard.RequireVerified(' + $readerDatabaseName + ', ' + $readerReportName + ', deadline, ' + $readerOptionsName + ');'
    $readerText = [regex]::Replace($readerText, 'productionAdmissionOptions:\s*([^;]+?)\);', $readerReplacement)
    [IO.File]::WriteAllText($readerFile.FullName, $readerText, [Text.UTF8Encoding]::new($false))
    Write-Output $readerFile.Name
}
