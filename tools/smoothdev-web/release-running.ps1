# Windows keeps the files of a running program locked, so `dotnet tool uninstall` fails while the
# installed smoothdev-web is running (the GUI, the TUI, `logs -f`). Stop those instances first.
# Components they started keep running and show up as orphaned; `smoothdev-web stop` ends them.
$tools = Join-Path $env:USERPROFILE '.dotnet\tools'
$running = Get-CimInstance Win32_Process | Where-Object {
  $_.ProcessId -ne $PID -and (
    $_.ExecutablePath -like (Join-Path $tools 'smoothdev-web.exe') -or
    $_.CommandLine -like "*$tools\.store\smoothdev.web*")
}

foreach ($p in $running) {
  Write-Host "stopping running smoothdev-web (pid $($p.ProcessId))"
  Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
}

if ($running) { Start-Sleep -Milliseconds 800 }
exit 0
