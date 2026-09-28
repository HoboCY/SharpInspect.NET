$ErrorActionPreference = 'Stop'
$taskTemp = 'F:\temp\SharpInspect-T57'
New-Item -ItemType Directory -Path $taskTemp -Force | Out-Null
$env:TEMP = $taskTemp
$env:TMP = $taskTemp
$env:SHARPINSPECT_V157_EVIDENCE_ROOT = 'D:\projects\SharpInspect.NET\.scratch\t57\runtime-evidence-09'
dotnet test tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj -c Release --no-build --no-restore -p:Platform=x64 --filter 'FullyQualifiedName~V157_P|FullyQualifiedName~V142_R01_RealProductionEntry' --logger 'trx;LogFileName=runtime-focused-09.trx' --results-directory .scratch/t57/test-results -v:normal
exit $LASTEXITCODE
