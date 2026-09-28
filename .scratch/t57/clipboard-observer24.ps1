$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ClipboardObserver24 {
 [DllImport("user32.dll")] public static extern IntPtr GetOpenClipboardWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}
'@
$taskSeen = @{}
$taskWatch = [Diagnostics.Stopwatch]::StartNew()
while ($taskWatch.Elapsed.TotalSeconds -lt 35) {
    $taskWindow = [ClipboardObserver24]::GetOpenClipboardWindow()
    [uint32]$taskOwner = 0
    [void][ClipboardObserver24]::GetWindowThreadProcessId($taskWindow, [ref]$taskOwner)
    $taskKey = [string]$taskOwner + ':' + [string]$taskWindow.ToInt64()
    if (-not $taskSeen.ContainsKey($taskKey)) { $taskSeen[$taskKey] = [ordered]@{owner=$taskOwner;window=$taskWindow.ToInt64();count=0;name=if($taskOwner){(Get-Process -Id $taskOwner -ErrorAction SilentlyContinue).ProcessName}else{$null}} }
    $taskSeen[$taskKey].count++
    Start-Sleep -Milliseconds 2
}
@($taskSeen.Values) | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath 'D:\projects\SharpInspect.NET\.scratch\t57\clipboard-observer24.json'
