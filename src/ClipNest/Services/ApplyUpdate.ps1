param([Parameter(Mandatory=$true)][string]$ConfigurationPath)
$ErrorActionPreference = 'Stop'
$configuration = Get-Content -LiteralPath $ConfigurationPath -Raw -Encoding UTF8 | ConvertFrom-Json
$sourceRoot = [IO.Path]::GetFullPath($configuration.Source).TrimEnd('\')
$destinationRoot = [IO.Path]::GetFullPath($configuration.Destination).TrimEnd('\')
$workRoot = [IO.Path]::GetFullPath($configuration.Work).TrimEnd('\')
$backupRoot = Join-Path $workRoot 'backup'
$changed = [Collections.Generic.List[object]]::new()
$canRestart = $false

try {
    if ($destinationRoot -eq [IO.Path]::GetPathRoot($destinationRoot).TrimEnd('\') -or
        -not $sourceRoot.StartsWith($workRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $sourceRoot.StartsWith($destinationRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $destinationRoot.StartsWith($workRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Invalid update paths.'
    }
    $applicationPath = Join-Path $destinationRoot 'ClipNest.exe'
    if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) { throw 'Installed ClipNest.exe was not found.' }
    $files = @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse)
    if ($files.Count -eq 0) { throw 'Update is empty.' }
    $applicationProcess = Get-Process -Id $configuration.ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $applicationProcess -and $applicationProcess.StartTime.ToUniversalTime().Ticks.ToString() -ne $configuration.ProcessStart) {
        throw 'Application process changed.'
    }
    [IO.File]::WriteAllText($configuration.Ready, 'ready')
    if ($null -ne $applicationProcess -and -not $applicationProcess.WaitForExit(120000)) { throw 'ClipNest did not exit in time.' }
    $canRestart = $true

    foreach ($file in $files) {
        $relative = $file.FullName.Substring($sourceRoot.Length + 1)
        $target = [IO.Path]::GetFullPath((Join-Path $destinationRoot $relative))
        if (-not $target.StartsWith($destinationRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid destination path.' }
        $backup = Join-Path $backupRoot $relative
        $existed = Test-Path -LiteralPath $target -PathType Leaf
        if ($existed) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
            Copy-Item -LiteralPath $target -Destination $backup
        }
        $changed.Add([pscustomobject]@{ Target = $target; Backup = $backup; Existed = $existed })
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        for ($attempt = 0; ; $attempt++) {
            try {
                Copy-Item -LiteralPath $file.FullName -Destination $target -Force
                break
            } catch {
                if ($attempt -ge 19) { throw }
                Start-Sleep -Milliseconds 250
            }
        }
    }

    Start-Process -FilePath $applicationPath -ArgumentList '--background' -WorkingDirectory $destinationRoot -WindowStyle Hidden | Out-Null
    [IO.File]::WriteAllText((Join-Path $workRoot 'success'), 'success')
} catch {
    $failure = $_.Exception.Message
    $rollbackFailed = $false
    for ($index = $changed.Count - 1; $index -ge 0; $index--) {
        $item = $changed[$index]
        try {
            if ($item.Existed) { Copy-Item -LiteralPath $item.Backup -Destination $item.Target -Force }
            elseif (Test-Path -LiteralPath $item.Target -PathType Leaf) { Remove-Item -LiteralPath $item.Target -Force }
        } catch { $rollbackFailed = $true; $failure += "`nRollback: $($_.Exception.Message)" }
    }
    [IO.File]::WriteAllText((Join-Path $workRoot 'error.txt'), $failure)
    if ($canRestart -and -not $rollbackFailed) {
        Start-Process -FilePath $applicationPath -ArgumentList '--background' -WorkingDirectory $destinationRoot -WindowStyle Hidden | Out-Null
    }
    Add-Type -AssemblyName PresentationFramework
    [Windows.MessageBox]::Show("Не удалось обновить ClipNest. Настройки сохранены.`n$failure`nПодробности: $workRoot", 'Обновление ClipNest') | Out-Null
    exit 1
}
