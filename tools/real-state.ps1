# Reports WinDock's real per-user state. Run it from a normal Windows context: a process started
# from inside a packaged app (such as the Claude desktop app) sees a private, redirected copy of
# %APPDATA% and of the registry instead.
param([string]$Out = (Join-Path $PSScriptRoot 'real-state.txt'))

$lines = @()
$lines += "checked: $(Get-Date)"
$folder = Join-Path $env:APPDATA 'WinDock'
$lines += "settings folder: $folder exists=$(Test-Path $folder)"
if (Test-Path $folder) {
    Get-ChildItem $folder -Force | ForEach-Object { $lines += "  $($_.Name)  $($_.Length) bytes  $($_.LastWriteTime)" }
    $file = Join-Path $folder 'settings.json'
    if (Test-Path $file) {
        $s = Get-Content $file -Raw | ConvertFrom-Json
        $lines += "  pins ($($s.Pinned.Count)): $(($s.Pinned | ForEach-Object Name) -join ', ')"
        $lines += "  seeded=$($s.Seeded) dock=$($s.DockEnabled) iconButton=$($s.IconButton) roundness=$($s.CornerRadius)"
    }
    $log = Join-Path $folder 'log.txt'
    if (Test-Path $log) { $lines += '  --- log tail ---'; Get-Content $log -Tail 12 | ForEach-Object { $lines += "  $_" } }
}
$lines += "run entry: $((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').WinDock)"
$lines += "installed-apps entry: $((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinDock' -ErrorAction SilentlyContinue).InstallLocation)"
$lines | Set-Content $Out -Encoding utf8
