# Fixed local metadata and four read-only status getters. No protector/key/recovery methods.
Import-Module -Name (Join-Path $optimusSystemDirectory 'WindowsPowerShell\v1.0\Modules\CimCmdlets\CimCmdlets.psd1') -ErrorAction Stop
$namespace = 'root/CIMV2/Security/MicrosoftVolumeEncryption'
$specs = [ordered]@{ GetConversionStatus = @('ConversionStatus','EncryptionPercentage','EncryptionFlags'); GetProtectionStatus = @('ProtectionStatus'); GetEncryptionMethod = @('EncryptionMethod'); GetLockStatus = @('LockStatus') }
$clock = [Diagnostics.Stopwatch]::StartNew()
$records = [Collections.Generic.List[object]]::new()
$issues = [Collections.Generic.List[object]]::new()
$examined = 0; $limited = $false; $succeeded = $false; $remaining = 210000; $budgetStopped = $false
function BitLocker-Issue($list, $errorRecord, [string]$context) {
    $message = $errorRecord.Exception.Message
    if ($message.Length -gt 768) { $message = 'The original provider error exceeded 768 characters; complete content omitted.' }
    $hresult = $errorRecord.Exception.HResult
    $code = if ($errorRecord.CategoryInfo.Category -eq [Management.Automation.ErrorCategory]::PermissionDenied -or $errorRecord.Exception -is [UnauthorizedAccessException] -or $hresult -eq -2147024891 -or $hresult -eq -2147217405) { 'PermissionDenied' } elseif ($hresult -eq -2147217394 -or $hresult -eq -2147217392) { 'NotSupported' } else { 'ReadFailed' }
    $list.Add([ordered]@{ code = $code; message = "$context failed (HRESULT $hresult). Provider/edition/permissions may be unavailable; no disabled state was inferred. $message" })
}
function BitLocker-Scalar($value, [string]$field, $notes) {
    if ($null -eq $value) { return $null }
    if ($value -is [string]) {
        if ($value.Length -le 2048) { return $value }
        $notes.Add([ordered]@{ code = 'LimitReached'; message = "$field exceeded 2048 characters ($($value.Length)); complete value omitted, not truncated." })
        return $null
    }
    if ($value -is [byte] -or $value -is [sbyte] -or $value -is [uint16] -or $value -is [int16] -or $value -is [uint32] -or $value -is [int32] -or $value -is [uint64] -or $value -is [int64] -or $value -is [bool]) { return $value }
    $notes.Add([ordered]@{ code = 'ReadFailed'; message = "$field returned an unsupported scalar type; no text, zero or Boolean conversion was performed." })
    return $null
}
function Read-BitLockerGetter($volume, [string]$name, [string[]]$fields) {
    $notes = [Collections.Generic.List[object]]::new()
    $values = [ordered]@{}
    foreach ($field in $fields) { $values[$field] = $null }
    $result = [ordered]@{ invoked = $false; ReturnValue = $null; values = $values; issues = $notes }
    if ($clock.Elapsed.TotalSeconds -ge 90) {
        $notes.Add([ordered]@{ code = 'LimitReached'; message = "$name was not called because the cooperative 90-second deadline was reached." })
        return $result
    }
    try {
        # The switch is a second fixed allow-list. No caller can supply a method or argument.
        $response = switch ($name) {
            'GetConversionStatus' { CimCmdlets\Invoke-CimMethod -InputObject $volume -MethodName GetConversionStatus -Arguments @{ PrecisionFactor = [uint32]0 } -OperationTimeoutSec 3 -ErrorAction Stop; break }
            'GetProtectionStatus' { CimCmdlets\Invoke-CimMethod -InputObject $volume -MethodName GetProtectionStatus -OperationTimeoutSec 3 -ErrorAction Stop; break }
            'GetEncryptionMethod' { CimCmdlets\Invoke-CimMethod -InputObject $volume -MethodName GetEncryptionMethod -OperationTimeoutSec 3 -ErrorAction Stop; break }
            'GetLockStatus' { CimCmdlets\Invoke-CimMethod -InputObject $volume -MethodName GetLockStatus -OperationTimeoutSec 3 -ErrorAction Stop; break }
            default { throw 'Unknown status getter; no method selected.' }
        }
        if ($null -eq $response) { throw 'The getter did not return a response.' }
        $result.invoked = $true
        $result.ReturnValue = BitLocker-Scalar $response.ReturnValue "$name.ReturnValue" $notes
        foreach ($field in $fields) {
            $property = $response.PSObject.Properties[$field]
            if ($null -eq $property) { $notes.Add([ordered]@{ code = 'ReadFailed'; message = "$name did not return selected field $field." }) }
            else { $values[$field] = BitLocker-Scalar $property.Value "$name.$field" $notes }
        }
    }
    catch { BitLocker-Issue $notes $_ $name }
    return $result
}
try {
    CimCmdlets\Get-CimInstance -Namespace $namespace -ClassName Win32_EncryptableVolume -Property @('DeviceID','DriveLetter','PersistentVolumeID') -OperationTimeoutSec 20 -ErrorAction Stop |
        Microsoft.PowerShell.Utility\Select-Object -First 33 |
        Microsoft.PowerShell.Core\ForEach-Object {
            $volume = $_
            $examined++
            if ($examined -gt 32) { $limited = $true; $issues.Add([ordered]@{ code = 'LimitReached'; message = 'More than 32 volumes were returned; only the first 32 were eligible in source order.' }) }
            elseif (-not $budgetStopped) {
                $notes = [Collections.Generic.List[object]]::new()
                $record = [ordered]@{ DeviceID = $null; DriveLetter = $null; PersistentVolumeID = $null; getters = [ordered]@{}; issues = $notes }
                foreach ($field in @('DeviceID','DriveLetter','PersistentVolumeID')) {
                    $property = $volume.CimInstanceProperties[$field]
                    if ($null -eq $property) { $notes.Add([ordered]@{ code = 'ReadFailed'; message = "Volume $examined did not report $field." }) }
                    else { $record[$field] = BitLocker-Scalar $property.Value "Volume $examined.$field" $notes }
                }
                foreach ($spec in $specs.GetEnumerator()) {
                    $getter = Read-BitLockerGetter $volume $spec.Key $spec.Value
                    $record.getters[$spec.Key] = $getter
                    foreach ($note in $getter.issues) { if ($note.code -eq 'LimitReached') { $limited = $true } }
                }
                $cost = [Text.Encoding]::UTF8.GetByteCount((Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $record -Depth 8 -Compress)) + 2
                if ($cost -gt $remaining) { $limited = $true; $budgetStopped = $true; $issues.Add([ordered]@{ code = 'LimitReached'; message = 'The 210000-byte retained payload budget was reached; this and later volumes were omitted.' }) }
                else {
                    $remaining -= $cost; $records.Add($record)
                    foreach ($note in $notes) { if ($note.code -eq 'LimitReached') { $limited = $true } }
                }
                if ($clock.Elapsed.TotalSeconds -ge 90) { $limited = $true; $budgetStopped = $true; $issues.Add([ordered]@{ code = 'LimitReached'; message = 'The cooperative deadline was reached; later volumes were not interrogated.' }) }
            }
        }
    $succeeded = $true
}
catch { BitLocker-Issue $issues $_ 'Win32_EncryptableVolume discovery' }
$result = [ordered]@{ kind = 'optimus.bitlocker-status'; version = 1; observedAt = [DateTimeOffset]::UtcNow.ToString('O',[Globalization.CultureInfo]::InvariantCulture); sourceNamespace = $namespace; query = [ordered]@{ succeeded = $succeeded; examined = $examined; limitReached = $limited; records = @($records.ToArray()); issues = @($issues.ToArray()) } }
Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $result -Depth 10 -Compress
