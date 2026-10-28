# Fixed local ActiveStore profile query; no rules, remote sessions or policy changes.
Import-Module -Name (Join-Path $optimusSystemDirectory 'WindowsPowerShell\v1.0\Modules\NetSecurity\NetSecurity.psd1') -ErrorAction Stop
$fields = @('Name','Enabled','DefaultInboundAction','DefaultOutboundAction','AllowInboundRules','AllowLocalFirewallRules','AllowLocalIPsecRules','NotifyOnListen')
function Read-FirewallField($item, [string]$field, $notes) {
    $rawProperty = $item.CimInstanceProperties[$field]
    if ($null -eq $rawProperty) {
        $notes.Add([ordered]@{ code = 'ReadFailed'; message = "$field was absent from the selected CIM properties." })
        return $null
    }
    $cimType = $rawProperty.CimType.ToString()
    $raw = $rawProperty.Value
    $result = [ordered]@{ kind = 'null'; sourceType = $null; underlyingType = $null; value = $null; name = $null; cimType = $cimType; cimValue = $null }
    # NetSecurity's adapted GpoBoolean getter adds zero to CIM data. Do not turn CIM null into False.
    if ($null -eq $raw) { return $result }
    if ($raw -is [string] -and $raw.Length -gt 512) {
        $result.kind = 'omitted'; $result.sourceType = $raw.GetType().FullName
        $notes.Add([ordered]@{ code = 'LimitReached'; message = "$field CIM text exceeded 512 characters ($($raw.Length)); value omitted without truncation." })
        return $result
    }
    if ($raw -isnot [string] -and $raw -isnot [bool] -and $raw -isnot [byte] -and $raw -isnot [sbyte] -and $raw -isnot [uint16] -and $raw -isnot [int16] -and $raw -isnot [uint32] -and $raw -isnot [int32] -and $raw -isnot [uint64] -and $raw -isnot [int64]) {
        $result.kind = 'omitted'; $result.sourceType = $raw.GetType().FullName
        $notes.Add([ordered]@{ code = 'ReadFailed'; message = "$field CIM type $($result.sourceType) is outside the selected scalar contract; content omitted." })
        return $result
    }
    $result.cimValue = $raw
    $representationLimited = $false
    try {
        $property = $item.PSObject.Properties[$field]
        if ($null -eq $property) { throw 'The adapted property was absent.' }
        $value = $property.Value
        if ($null -eq $value) { throw 'The adapted property returned null despite non-null CIM data.' }
        $type = $value.GetType()
        $result.sourceType = $type.FullName
        if ($type.IsEnum) {
            $underlying = [Enum]::GetUnderlyingType($type)
            $result.kind = 'enum'; $result.underlyingType = $underlying.FullName
            $result.value = [Convert]::ChangeType($value, $underlying, [Globalization.CultureInfo]::InvariantCulture)
            $result.name = $value.ToString()
        }
        elseif ($value -is [bool]) { $result.kind = 'boolean'; $result.value = $value }
        elseif ($value -is [string]) { $result.kind = 'text'; $result.value = $value }
        elseif ($value -is [byte] -or $value -is [sbyte] -or $value -is [uint16] -or $value -is [int16] -or $value -is [uint32] -or $value -is [int32] -or $value -is [uint64] -or $value -is [int64]) { $result.kind = 'integer'; $result.value = $value }
        else {
            $result.kind = 'unsupported'
            $notes.Add([ordered]@{ code = 'ReadFailed'; message = "$field adapted type $($result.sourceType) was not converted to text or Boolean." })
        }
        foreach ($text in @($result.sourceType,$result.underlyingType,$result.name)) { if ($null -ne $text -and $text.Length -gt 512) { $representationLimited = $true; throw 'Adapted type or enum name exceeded 512 characters.' } }
        if ($result.value -is [string] -and $result.value.Length -gt 512) { $representationLimited = $true; throw 'Adapted text exceeded 512 characters.' }
    }
    catch {
        $code = if ($representationLimited) { 'LimitReached' } else { 'ReadFailed' }
        $notes.Add([ordered]@{ code = $code; message = "$field adapted value was unavailable or outside its bounded representation; CIM scalar retained." })
        $result.kind = 'unavailable'; $result.sourceType = $null; $result.underlyingType = $null; $result.value = $null; $result.name = $null
    }
    return $result
}
$records = [Collections.Generic.List[object]]::new()
$issues = [Collections.Generic.List[object]]::new()
$examined = 0; $limited = $false; $succeeded = $false; $remaining = 48000; $budgetStopped = $false
try {
    NetSecurity\Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop |
        Microsoft.PowerShell.Utility\Select-Object -First 9 |
        Microsoft.PowerShell.Core\ForEach-Object {
            $examined++
            if ($examined -gt 8) { $limited = $true; $issues.Add([ordered]@{ code = 'LimitReached'; message = 'More than eight profiles were returned; only the first eight were eligible in source order.' }) }
            elseif (-not $budgetStopped) {
                $record = [ordered]@{}
                $notes = [Collections.Generic.List[object]]::new()
                foreach ($field in $fields) { $record[$field] = Read-FirewallField $_ $field $notes }
                $candidate = [ordered]@{ record = $record; issues = @($notes.ToArray()) }
                $cost = [Text.Encoding]::UTF8.GetByteCount((Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $candidate -Depth 8 -Compress)) + 2
                if ($cost -gt $remaining) { $limited = $true; $budgetStopped = $true; $issues.Add([ordered]@{ code = 'LimitReached'; message = 'The independent 48000-byte metadata payload budget was reached; later records were not retained.' }) }
                else {
                    $remaining -= $cost; $records.Add($record)
                    foreach ($note in $notes) { $issues.Add($note); if ($note.code -eq 'LimitReached') { $limited = $true } }
                }
            }
        }
    $succeeded = $true
}
catch {
    $message = $_.Exception.Message
    if ($message.Length -gt 1024) { $message = 'The original query error exceeded 1024 characters and was not retained.' }
    $issues.Add([ordered]@{ code = 'ReadFailed'; message = $message })
}
$result = [ordered]@{ kind = 'optimus.firewall-profiles'; version = 1; observedAt = [DateTimeOffset]::UtcNow.ToString('O',[Globalization.CultureInfo]::InvariantCulture); policyStore = 'ActiveStore'; query = [ordered]@{ succeeded = $succeeded; examined = $examined; limitReached = $limited; records = @($records.ToArray()); issues = @($issues.ToArray()) } }
Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $result -Depth 10 -Compress
