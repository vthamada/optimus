# Fixed local metadata queries only. No disk methods, SMART, sector reads or mutations.
Import-Module -Name (Join-Path $optimusSystemDirectory 'WindowsPowerShell\v1.0\Modules\CimCmdlets\CimCmdlets.psd1') -ErrorAction Stop
function Read-StorageQuery([string]$className, [string[]]$fields, [int]$maximum) {
    $records = [Collections.Generic.List[object]]::new()
    $issues = [Collections.Generic.List[object]]::new()
    $examined = 0
    $limited = $false
    $succeeded = $false
    $remaining = 110000
    $budgetStopped = $false
    try {
        CimCmdlets\Get-CimInstance -ClassName $className -Property $fields -OperationTimeoutSec 30 -ErrorAction Stop |
            Microsoft.PowerShell.Utility\Select-Object -First ($maximum + 1) |
            Microsoft.PowerShell.Core\ForEach-Object {
                $examined++
                if ($examined -gt $maximum) {
                    $limited = $true
                    $issues.Add([ordered]@{ code = 'LimitReached'; message = "$className returned more than $maximum records; only the first $maximum were eligible in provider order." })
                }
                elseif (-not $budgetStopped) {
                    $record = [ordered]@{}
                    $recordIssues = [Collections.Generic.List[object]]::new()
                    foreach ($field in $fields) {
                        $property = $_.CimInstanceProperties[$field]
                        if ($null -eq $property) {
                            $record[$field] = $null
                            $recordIssues.Add([ordered]@{ code = 'ReadFailed'; message = "$className record $examined did not report property $field." })
                            continue
                        }
                        $value = $property.Value
                        if ($value -is [string]) {
                            if ($value.Length -le 2048) { $record[$field] = $value }
                            else {
                                $record[$field] = $null
                                $recordIssues.Add([ordered]@{ code = 'LimitReached'; message = "$className record $examined property $field exceeded 2048 characters ($($value.Length)); complete content omitted, not truncated." })
                            }
                        }
                        elseif ($null -eq $value -or $value -is [bool] -or $value -is [byte] -or $value -is [sbyte] -or $value -is [int16] -or $value -is [uint16] -or $value -is [int32] -or $value -is [uint32] -or $value -is [int64] -or $value -is [uint64] -or $value -is [decimal]) { $record[$field] = $value }
                        else {
                            $record[$field] = [ordered]@{ sourceType = $value.GetType().FullName; omitted = $true }
                            $recordIssues.Add([ordered]@{ code = 'ReadFailed'; message = "$className record $examined property $field returned an unsupported scalar; its type was retained without text conversion." })
                        }
                    }
                    $candidate = [ordered]@{ record = $record; issues = @($recordIssues.ToArray()) }
                    $cost = [Text.Encoding]::UTF8.GetByteCount((Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $candidate -Depth 6 -Compress)) + 2
                    if ($cost -gt $remaining) {
                        $limited = $true
                        $budgetStopped = $true
                        $issues.Add([ordered]@{ code = 'LimitReached'; message = "$className reached its independent 110000-byte payload budget; later records were not retained." })
                    }
                    else {
                        $remaining -= $cost
                        $records.Add($record)
                        foreach ($issue in $recordIssues) { $issues.Add($issue); if ($issue.code -eq 'LimitReached') { $limited = $true } }
                    }
                }
            }
        $succeeded = $true
    }
    catch {
        $message = $_.Exception.Message
        if ($message.Length -gt 1024) { $message = 'The local CIM error exceeded 1024 characters; its complete message was not retained.' }
        $issues.Add([ordered]@{ code = 'ReadFailed'; message = "$className query failed: $message" })
    }
    return [ordered]@{ succeeded = $succeeded; examined = $examined; limitReached = $limited; records = @($records.ToArray()); issues = @($issues.ToArray()) }
}
$drives = Read-StorageQuery 'Win32_DiskDrive' @('DeviceID','Index','Model','SerialNumber','FirmwareRevision','InterfaceType','MediaType','Size','Partitions','Status') 32
$partitions = Read-StorageQuery 'Win32_DiskPartition' @('DeviceID','DiskIndex','Index','Size','StartingOffset','Type','BootPartition','PrimaryPartition') 128
$result = [ordered]@{ kind = 'optimus.storage-inventory'; version = 1; observedAt = [DateTimeOffset]::UtcNow.ToString('O',[Globalization.CultureInfo]::InvariantCulture); drives = $drives; partitions = $partitions }
Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $result -Depth 8 -Compress
