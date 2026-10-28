# Fixed read-only local CIM properties. No identifiers of keys/devices, hardware test or configuration changes.
Import-Module -Name (Join-Path $optimusSystemDirectory 'WindowsPowerShell\v1.0\Modules\CimCmdlets\CimCmdlets.psd1') -ErrorAction Stop
function Read-Hardware([string]$className,[string[]]$fields,[int]$maximum) {
    $records=[Collections.Generic.List[object]]::new()
    $issues=[Collections.Generic.List[object]]::new()
    $examined=0; $limited=$false; $succeeded=$false; $remaining=42000; $stopped=$false
    try {
        CimCmdlets\Get-CimInstance -Namespace 'root/cimv2' -ClassName $className -Property $fields -OperationTimeoutSec 20 -ErrorAction Stop |
            Microsoft.PowerShell.Utility\Select-Object -First ($maximum+1) |
            Microsoft.PowerShell.Core\ForEach-Object {
                $examined++
                if ($examined -gt $maximum) {
                    $limited=$true
                    $issues.Add([ordered]@{code='LimitReached';message="$className exceeded $maximum records; probe omitted."})
                } elseif (-not $stopped) {
                    $record=[ordered]@{}; $notes=[Collections.Generic.List[object]]::new()
                    foreach($field in $fields) {
                        $p=$_.CimInstanceProperties[$field]
                        if($null -eq $p) {
                            $record[$field]=$null
                            $notes.Add([ordered]@{code='ReadFailed';message="$className record $examined omitted $field."})
                            continue
                        }
                        $v=$p.Value
                        if($null -eq $v) { $record[$field]=$null }
                        elseif($v -is [string]) {
                            $max=512; if($field -eq 'Name'){$max=1024}
                            if($v.Length -le $max){$record[$field]=$v}
                            else {
                                $record[$field]=$null; $limited=$true
                                $notes.Add([ordered]@{code='LimitReached';message="$className record $examined $field exceeded $max characters; omitted without truncation."})
                            }
                        }
                        elseif($v -is [DateTime]) {$record[$field]=[ordered]@{sourceType='System.DateTime';value=$v.ToString('O',[Globalization.CultureInfo]::InvariantCulture);kind=$v.Kind.ToString()}}
                        elseif($v -is [DateTimeOffset]) {$record[$field]=[ordered]@{sourceType='System.DateTimeOffset';value=$v.ToString('O',[Globalization.CultureInfo]::InvariantCulture);kind='Offset'}}
                        elseif($v -is [byte] -or $v -is [sbyte] -or $v -is [int16] -or $v -is [uint16] -or $v -is [int32] -or $v -is [uint32] -or $v -is [int64] -or $v -is [uint64] -or $v -is [decimal] -or $v -is [bool]) {$record[$field]=$v}
                        else {
                            $record[$field]=[ordered]@{sourceType=$v.GetType().FullName;omitted=$true}
                            $notes.Add([ordered]@{code='ReadFailed';message="$className record $examined $field type unsupported; type retained without conversion."})
                        }
                    }
                    $candidate=[ordered]@{record=$record;issues=@($notes.ToArray())}
                    $cost=[Text.Encoding]::UTF8.GetByteCount((Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $candidate -Depth 7 -Compress))+2
                    if($cost -gt $remaining) {
                        $limited=$true; $stopped=$true
                        $issues.Add([ordered]@{code='LimitReached';message="$className reached its independent 42000-byte payload budget; later records omitted."})
                    } else {
                        $remaining-=$cost; $records.Add($record)
                        foreach($note in $notes){$issues.Add($note)}
                    }
                }
            }
        $succeeded=$true
    } catch {
        $message=$_.Exception.Message
        if($message.Length -gt 1024){$message='Native CIM error exceeded 1024 characters; message omitted.'}
        $code='ReadFailed'; if($_.Exception.HResult -eq -2147024891 -or $_.Exception.HResult -eq -2147217405){$code='PermissionDenied'}
        $issues.Add([ordered]@{code=$code;message="$className query failed: $message"})
    }
    return [ordered]@{succeeded=$succeeded;examined=$examined;limitReached=$limited;records=@($records.ToArray());issues=@($issues.ToArray())}
}
$boards=Read-Hardware 'Win32_BaseBoard' @('Manufacturer','Product','Version') 8
$systems=Read-Hardware 'Win32_ComputerSystem' @('Manufacturer','Model') 8
$bios=Read-Hardware 'Win32_BIOS' @('Manufacturer','SMBIOSBIOSVersion','ReleaseDate') 8
$cpu=Read-Hardware 'Win32_Processor' @('Name','Manufacturer','MaxClockSpeed') 8
$memory=Read-Hardware 'Win32_PhysicalMemory' @('DeviceLocator','BankLabel','Manufacturer','PartNumber','Capacity','Speed','ConfiguredClockSpeed','SMBIOSMemoryType') 32
$result=[ordered]@{kind='optimus.hardware-profile';version=1;observedAt=[DateTimeOffset]::UtcNow.ToString('O',[Globalization.CultureInfo]::InvariantCulture);baseboards=$boards;system=$systems;bios=$bios;processors=$cpu;memory=$memory}
Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $result -Depth 9 -Compress
