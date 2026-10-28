# Fixed local endpoint queries. No DNS, traffic, remote session or mutation is requested.
Import-Module -Name (Join-Path $optimusSystemDirectory 'WindowsPowerShell\v1.0\Modules\NetTCPIP\NetTCPIP.psd1') -ErrorAction Stop
function Get-EndpointRows([string]$protocol) {
    if ($protocol -eq 'tcp') { NetTCPIP\Get-NetTCPConnection -ErrorAction Stop }
    else { NetTCPIP\Get-NetUDPEndpoint -ErrorAction Stop }
}
function Read-Endpoints([string]$protocol, [string[]]$fields, [int]$maximum) {
    $records = [Collections.Generic.List[object]]::new()
    $issues = [Collections.Generic.List[object]]::new()
    $examined = 0
    $limited = $false
    $succeeded = $false
    $remaining = 110000
    $budgetStopped = $false
    try {
        Get-EndpointRows $protocol |
            Microsoft.PowerShell.Utility\Select-Object -First ($maximum + 1) |
            Microsoft.PowerShell.Core\ForEach-Object {
                $examined++
                if ($examined -gt $maximum) {
                    $limited = $true
                    $issues.Add([ordered]@{ code = 'LimitReached'; message = "$protocol returned more than $maximum endpoints; only the first $maximum were eligible in source order." })
                }
                elseif (-not $budgetStopped) {
                    $record = [ordered]@{}
                    $recordIssues = [Collections.Generic.List[object]]::new()
                    foreach ($field in $fields) {
                        $property = $_.PSObject.Properties[$field]
                        if ($null -eq $property) {
                            $record[$field] = $null
                            $recordIssues.Add([ordered]@{ code = 'ReadFailed'; message = "$protocol record $examined did not report property $field." })
                            continue
                        }
                        $value = $property.Value
                        if ($value -is [string]) {
                            if ($value.Length -le 512) { $record[$field] = $value }
                            else {
                                $record[$field] = $null
                                $recordIssues.Add([ordered]@{ code = 'LimitReached'; message = "$protocol record $examined property $field exceeded 512 characters ($($value.Length)); complete content omitted, not truncated." })
                            }
                        }
                        elseif ($value -is [Enum]) {
                            $numeric = [Convert]::ChangeType($value, [Enum]::GetUnderlyingType($value.GetType()), [Globalization.CultureInfo]::InvariantCulture)
                            $record[$field] = [ordered]@{ sourceType = $value.GetType().FullName; value = $numeric; name = $value.ToString() }
                        }
                        elseif ($null -eq $value -or $value -is [bool] -or $value -is [byte] -or $value -is [sbyte] -or $value -is [int16] -or $value -is [uint16] -or $value -is [int32] -or $value -is [uint32] -or $value -is [int64] -or $value -is [uint64] -or $value -is [decimal]) { $record[$field] = $value }
                        else {
                            $record[$field] = [ordered]@{ sourceType = $value.GetType().FullName; omitted = $true }
                            $recordIssues.Add([ordered]@{ code = 'ReadFailed'; message = "$protocol record $examined property $field returned an unsupported scalar; type retained without text conversion." })
                        }
                    }
                    $candidate = [ordered]@{ record = $record; issues = @($recordIssues.ToArray()) }
                    $cost = [Text.Encoding]::UTF8.GetByteCount((Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $candidate -Depth 7 -Compress)) + 2
                    if ($cost -gt $remaining) {
                        $limited = $true
                        $budgetStopped = $true
                        $issues.Add([ordered]@{ code = 'LimitReached'; message = "$protocol reached its independent 110000-byte payload budget; later endpoints were not retained." })
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
        if ($message.Length -gt 1024) { $message = 'Local endpoint query error exceeded 1024 characters; its complete message was not retained.' }
        $issues.Add([ordered]@{ code = 'ReadFailed'; message = "$protocol query failed: $message" })
    }
    return [ordered]@{ succeeded = $succeeded; examined = $examined; limitReached = $limited; records = @($records.ToArray()); issues = @($issues.ToArray()) }
}
$tcp = Read-Endpoints 'tcp' @('LocalAddress','LocalPort','RemoteAddress','RemotePort','State','OwningProcess') 256
$udp = Read-Endpoints 'udp' @('LocalAddress','LocalPort','OwningProcess') 128
$result = [ordered]@{ kind = 'optimus.network-connections'; version = 1; observedAt = [DateTimeOffset]::UtcNow.ToString('O',[Globalization.CultureInfo]::InvariantCulture); tcp = $tcp; udp = $udp }
Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $result -Depth 9 -Compress
