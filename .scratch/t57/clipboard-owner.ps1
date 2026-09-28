$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ClipboardOwnerProbe {
 [DllImport("user32.dll")] public static extern IntPtr GetOpenClipboardWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
 [DllImport("user32.dll",SetLastError=true)] public static extern bool OpenClipboard(IntPtr window);
 [DllImport("user32.dll")] public static extern bool CloseClipboard();
}
'@
$taskRows = @()
for ($taskIndex=0; $taskIndex -lt 20; $taskIndex++) {
    $taskOwnerWindow = [ClipboardOwnerProbe]::GetOpenClipboardWindow()
    [uint32]$taskOwnerProcess = 0
    [void][ClipboardOwnerProbe]::GetWindowThreadProcessId($taskOwnerWindow, [ref]$taskOwnerProcess)
    $taskOpen = [ClipboardOwnerProbe]::OpenClipboard([IntPtr]::Zero)
    $taskError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    if ($taskOpen) { [void][ClipboardOwnerProbe]::CloseClipboard() }
    $taskRows += [ordered]@{atUtc=[DateTimeOffset]::UtcNow.ToString('o');ownerWindow=$taskOwnerWindow.ToInt64();ownerProcess=$taskOwnerProcess;ownerName=if($taskOwnerProcess){(Get-Process -Id $taskOwnerProcess -ErrorAction SilentlyContinue).ProcessName}else{$null};opened=$taskOpen;error=$taskError}
    Start-Sleep -Milliseconds 100
}
$taskRows | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath '.scratch/t57/clipboard-owner22.json'
$taskRows | Group-Object { [string]$_.opened + ':' + [string]$_.ownerName + ':' + [string]$_.error } | Select-Object Name,Count
