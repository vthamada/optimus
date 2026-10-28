# Fixed local enumeration only. No SystemRestore methods are invoked.
Import-Module -Name (Join-Path $optimusSystemDirectory 'WindowsPowerShell\v1.0\Modules\CimCmdlets\CimCmdlets.psd1') -ErrorAction Stop
$fields = @('Description','RestorePointType','EventType','SequenceNumber','CreationTime')
$records = [Collections.Generic.List[object]]::new()
$issues = [Collections.Generic.List[object]]::new()
$examined = 0
$limited = $false
$succeeded = $false
$remaining = 180000
$budgetStopped = $false
try {
    CimCmdlets\Get-CimInstance -Namespace 'root/default' -ClassName SystemRestore -Property $fields -OperationTimeoutSec 30 -ErrorAction Stop |
        Microsoft.PowerShell.Utility\Select-Object -First 65 |
        Microsoft.PowerShell.Core\ForEach-Object {
            $examined++
            if ($examined -gt 64) {
                $limited = $true
                $issues.Add([ordered]@{ code = 'LimitReached'; message = 'SystemRestore returned more than 64 entries; only the first 64 in provider order were eligible.' })
            }
            elseif (-not $budgetStopped) {
                $record = [ordered]@{}
                $notes = [Collections.Generic.List[object]]::new()
                foreach ($field in $fields) {
                    $property = $_.CimInstanceProperties[$field]
                    if ($null -eq $property) {
                        $record[$field] = $null
                        $notes.Add([ordered]@{ code = 'ReadFailed'; message = "SystemRestore record $examined did not provide $field." })
                        continue
                    }
                    $value = $property.Value
                    if ($value -is [string]) {
                        if ($value.Length -le 1024) { $record[$field] = $value }
                        else { $record[$field] = $null; $notes.Add([ordered]@{ code = 'LimitReached'; message = "SystemRestore record $examined $field exceeded 1024 characters ($($value.Length)); full value omitted without truncation." }) }
                    }
                    elseif ($value -is [datetime] -or $value -is [datetimeoffset]) {
                        $kind = if ($value -is [datetimeoffset]) { 'Offset' } else { $value.Kind.ToString() }
                        $record[$field] = [ordered]@{ sourceType = $value.GetType().FullName; value = $value.ToString('O',[Globalization.CultureInfo]::InvariantCulture); kind = $kind }
                    }
                    elseif ($null -eq $value -or $value -is [bool] -or $value -is [byte] -or $value -is [sbyte] -or $value -is [int16] -or $value -is [uint16] -or $value -is [int32] -or $value -is [uint32] -or $value -is [int64] -or $value -is [uint64]) { $record[$field] = $value }
                    else {
                        $record[$field] = [ordered]@{ sourceType = $value.GetType().FullName; omitted = $true }
                        $notes.Add([ordered]@{ code = 'ReadFailed'; message = "SystemRestore record $examined $field returned an unsupported object; source type retained without conversion." })
                    }
                }
                $candidate = [ordered]@{ record = $record; issues = @($notes.ToArray()) }
                $cost = [Text.Encoding]::UTF8.GetByteCount((Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $candidate -Depth 6 -Compress)) + 2
                if ($cost -gt $remaining) {
                    $limited = $true; $budgetStopped = $true
                    $issues.Add([ordered]@{ code = 'LimitReached'; message = 'SystemRestore reached its 180000-byte retained payload budget; further records were not retained.' })
                }
                else {
                    $remaining -= $cost; $records.Add($record)
                    foreach ($issue in $notes) { $issues.Add($issue); if ($issue.code -eq 'LimitReached') { $limited = $true } }
                }
            }
        }
    $succeeded = $true
}
catch {
    $code = if ($_.CategoryInfo.Category -eq [Management.Automation.ErrorCategory]::PermissionDenied -or $_.Exception -is [UnauthorizedAccessException]) { 'PermissionDenied' } else { 'ReadFailed' }
    $message = $_.Exception.Message
    if ($message.Length -gt 1024) { $message = 'SystemRestore failed; the original error exceeded 1024 characters and was not retained.'; $limited = $true }
    $issues.Add([ordered]@{ code = $code; message = $message })
}
$result = [ordered]@{ kind = 'optimus.restore-points'; version = 1; observedAt = [DateTimeOffset]::UtcNow.ToString('O',[Globalization.CultureInfo]::InvariantCulture); query = [ordered]@{ succeeded = $succeeded; examined = $examined; limitReached = $limited; records = @($records.ToArray()); issues = @($issues.ToArray()) } }
Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $result -Depth 8 -Compress
