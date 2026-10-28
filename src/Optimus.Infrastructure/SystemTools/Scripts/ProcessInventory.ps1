# Fixed local process metadata; no command line, environment, memory reads or process methods.
Import-Module -Name (Join-Path $optimusSystemDirectory 'WindowsPowerShell\v1.0\Modules\CimCmdlets\CimCmdlets.psd1') -ErrorAction Stop
$fields = @('Name', 'ProcessId', 'ParentProcessId', 'CreationDate', 'WorkingSetSize', 'KernelModeTime', 'UserModeTime', 'ExecutablePath', 'ThreadCount')
$records = [Collections.Generic.List[object]]::new()
$issues = [Collections.Generic.List[object]]::new()
$examined = 0
$limited = $false
$succeeded = $false
$remaining = 220000
$budgetStopped = $false
try {
    CimCmdlets\Get-CimInstance -ClassName Win32_Process -Property $fields -OperationTimeoutSec 30 -ErrorAction Stop |
        Microsoft.PowerShell.Utility\Select-Object -First 257 |
        Microsoft.PowerShell.Core\ForEach-Object {
            $examined++
            if ($examined -gt 256) {
                $limited = $true
                $issues.Add([ordered]@{ code = 'LimitReached'; message = 'Win32_Process returned more than 256 records; only the first 256 were eligible in source order.' })
            }
            elseif (-not $budgetStopped) {
                $record = [ordered]@{}
                $recordIssues = [Collections.Generic.List[object]]::new()
                foreach ($field in $fields) {
                    $property = $_.CimInstanceProperties[$field]
                    if ($null -eq $property) {
                        $record[$field] = $null
                        $recordIssues.Add([ordered]@{ code = 'ReadFailed'; message = "Win32_Process record $examined did not report $field." })
                        continue
                    }
                    $value = $property.Value
                    if ($value -is [string]) {
                        if ($value.Length -le 2048) { $record[$field] = $value }
                        else {
                            $record[$field] = $null
                            $recordIssues.Add([ordered]@{ code = 'LimitReached'; message = "Win32_Process record $examined property $field exceeded 2048 characters ($($value.Length)); complete value omitted, not truncated." })
                        }
                    }
                    elseif ($value -is [datetime] -or $value -is [datetimeoffset]) {
                        $kind = if ($value -is [datetimeoffset]) { 'Offset' } else { $value.Kind.ToString() }
                        $record[$field] = [ordered]@{ sourceType = $value.GetType().FullName; value = $value.ToString('O', [Globalization.CultureInfo]::InvariantCulture); kind = $kind }
                    }
                    elseif ($null -eq $value -or $value -is [bool] -or $value -is [byte] -or $value -is [sbyte] -or $value -is [int16] -or $value -is [uint16] -or $value -is [int32] -or $value -is [uint32] -or $value -is [int64] -or $value -is [uint64] -or $value -is [decimal]) { $record[$field] = $value }
                    else {
                        $record[$field] = [ordered]@{ sourceType = $value.GetType().FullName; omitted = $true }
                        $recordIssues.Add([ordered]@{ code = 'ReadFailed'; message = "Win32_Process record $examined property $field had an unsupported scalar; its type was retained without string conversion." })
                    }
                }
                $candidate = [ordered]@{ record = $record; issues = @($recordIssues.ToArray()) }
                $json = Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $candidate -Depth 7 -Compress
                $cost = [Text.Encoding]::UTF8.GetByteCount($json) + 2
                if ($cost -gt $remaining) {
                    $limited = $true
                    $budgetStopped = $true
                    $issues.Add([ordered]@{ code = 'LimitReached'; message = 'Win32_Process reached its 220000-byte retained payload budget; remaining records were not retained.' })
                }
                else {
                    $remaining -= $cost
                    $records.Add($record)
                    foreach ($issue in $recordIssues) {
                        $issues.Add($issue)
                        if ($issue.code -eq 'LimitReached') { $limited = $true }
                    }
                }
            }
        }
    $succeeded = $true
}
catch {
    $message = $_.Exception.Message
    if ($message.Length -gt 1024) { $message = 'The local process query failed; its error message exceeded 1024 characters and was not retained.' }
    $issues.Add([ordered]@{ code = 'ReadFailed'; message = "Win32_Process query failed: $message" })
}
$result = [ordered]@{
    kind = 'optimus.process-inventory'
    version = 1
    observedAt = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    query = [ordered]@{ succeeded = $succeeded; examined = $examined; limitReached = $limited; records = @($records.ToArray()); issues = @($issues.ToArray()) }
}
Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $result -Depth 9 -Compress
