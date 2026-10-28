# Fixed local queries only. The host supplies optimusSystemDirectory and a UTF-8 console.
Import-Module -Name (Join-Path $optimusSystemDirectory 'WindowsPowerShell\v1.0\Modules\CimCmdlets\CimCmdlets.psd1') -ErrorAction Stop

function Read-DeviceQuery([string]$className, [string[]]$fields) {
    $records = [Collections.Generic.List[object]]::new()
    $issues = [Collections.Generic.List[object]]::new()
    $examined = 0
    $limited = $false
    $succeeded = $false
    $remaining = 126000
    $budgetStopped = $false
    try {
        CimCmdlets\Get-CimInstance -ClassName $className -Property $fields -OperationTimeoutSec 30 -ErrorAction Stop |
            Microsoft.PowerShell.Utility\Select-Object -First 257 |
            Microsoft.PowerShell.Core\ForEach-Object {
                $examined++
                if ($examined -gt 256) {
                    $limited = $true
                    $issues.Add([ordered]@{ code = 'LimitReached'; message = "$className returned more than 256 records; only the first 256 were eligible." })
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
                            if ($value.Length -gt 2048) {
                                $record[$field] = $null
                                $recordIssues.Add([ordered]@{ code = 'LimitReached'; message = "$className record $examined property $field exceeded 2048 characters ($($value.Length)); content omitted." })
                            }
                            else { $record[$field] = $value }
                        }
                        elseif ($null -eq $value -or $value -is [bool] -or $value -is [byte] -or $value -is [sbyte] -or $value -is [int16] -or $value -is [uint16] -or $value -is [int32] -or $value -is [uint32] -or $value -is [int64] -or $value -is [uint64] -or $value -is [decimal] -or $value -is [double] -or $value -is [single]) {
                            $record[$field] = $value
                        }
                        elseif ($value -is [datetime] -or $value -is [datetimeoffset]) {
                            $dateKind = if ($value -is [datetimeoffset]) { 'Offset' } else { $value.Kind.ToString() }
                            $record[$field] = [ordered]@{ sourceType = $value.GetType().FullName; value = $value.ToString('O', [Globalization.CultureInfo]::InvariantCulture); kind = $dateKind }
                            if ($field -ne 'DriverDate') {
                                $recordIssues.Add([ordered]@{ code = 'ReadFailed'; message = "$className record $examined property $field returned an unexpected datetime; its typed representation was retained without conversion." })
                            }
                        }
                        else {
                            $record[$field] = [ordered]@{ sourceType = $value.GetType().FullName; omitted = $true }
                            $recordIssues.Add([ordered]@{ code = 'ReadFailed'; message = "$className record $examined property $field returned an unsupported object type; no string conversion was applied." })
                        }
                    }
                    $candidate = [ordered]@{ record = $record; issues = @($recordIssues.ToArray()) }
                    $json = Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $candidate -Depth 8 -Compress
                    $cost = [Text.Encoding]::UTF8.GetByteCount($json) + 2
                    if ($cost -gt $remaining) {
                        $budgetStopped = $true
                        $limited = $true
                        $issues.Add([ordered]@{ code = 'LimitReached'; message = "$className reached its conservative 128000-byte JSON budget; later records were not retained." })
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
        if ($message.Length -gt 1024) { $message = 'The CIM query failed; its error message exceeded the retained text budget.' }
        $issues.Add([ordered]@{ code = 'ReadFailed'; message = "$className query failed: $message" })
    }
    return [ordered]@{ succeeded = $succeeded; examined = $examined; limitReached = $limited; records = @($records.ToArray()); issues = @($issues.ToArray()) }
}

$deviceFields = @('DeviceID', 'Name', 'PNPClass', 'Manufacturer', 'Status', 'ConfigManagerErrorCode', 'Present')
$driverFields = @('DeviceID', 'DeviceName', 'DriverVersion', 'DriverProviderName', 'DriverDate', 'IsSigned', 'Signer', 'InfName')
$result = [ordered]@{
    kind = 'optimus.device-inventory'
    version = 1
    devices = (Read-DeviceQuery 'Win32_PnPEntity' $deviceFields)
    drivers = (Read-DeviceQuery 'Win32_PnPSignedDriver' $driverFields)
}
Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $result -Depth 10 -Compress
