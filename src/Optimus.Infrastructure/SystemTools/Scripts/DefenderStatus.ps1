# The host supplies a validated native system directory and a bounded, supervised process.
# This script only reads the local status. It does not accept caller command text or remote sessions.
$module = Join-Path -Path $optimusSystemDirectory -ChildPath 'WindowsPowerShell\v1.0\Modules\Defender\Defender.psd1'
Import-Module -Name $module -ErrorAction Stop
$status = Defender\Get-MpComputerStatus -ErrorAction Stop
if ($null -eq $status -or $status -is [array]) { throw 'Defender did not return one unambiguous local status object.' }

$definitions = @(
    @{ Name = 'AMServiceEnabled'; Kind = 'Boolean' },
    @{ Name = 'AntivirusEnabled'; Kind = 'Boolean' },
    @{ Name = 'AntispywareEnabled'; Kind = 'Boolean' },
    @{ Name = 'BehaviorMonitorEnabled'; Kind = 'Boolean' },
    @{ Name = 'IoavProtectionEnabled'; Kind = 'Boolean' },
    @{ Name = 'NISEnabled'; Kind = 'Boolean' },
    @{ Name = 'OnAccessProtectionEnabled'; Kind = 'Boolean' },
    @{ Name = 'RealTimeProtectionEnabled'; Kind = 'Boolean' },
    @{ Name = 'DefenderSignaturesOutOfDate'; Kind = 'Boolean' },
    @{ Name = 'IsTamperProtected'; Kind = 'Boolean' },
    @{ Name = 'AMEngineVersion'; Kind = 'Text' },
    @{ Name = 'AMProductVersion'; Kind = 'Text' },
    @{ Name = 'AMRunningMode'; Kind = 'Text' },
    @{ Name = 'AntivirusSignatureVersion'; Kind = 'Text' },
    @{ Name = 'AntivirusSignatureLastUpdated'; Kind = 'Date' },
    @{ Name = 'ComputerState'; Kind = 'UnsignedInteger' }
)
$properties = [ordered]@{}
$unavailable = [System.Collections.Generic.List[object]]::new()
foreach ($definition in $definitions) {
    $name = $definition.Name
    $reason = $null
    $sourceType = $null
    $reportedCharacters = $null
    $selected = $null
    try {
        $property = $status.PSObject.Properties[$name]
        if ($null -eq $property) { $reason = 'Missing' }
        elseif ($null -eq $property.Value) { $reason = 'Null' }
        else {
            $value = $property.Value
            $sourceType = $value.GetType().FullName
            $expected = switch ($definition.Kind) {
                'Boolean' { $value -is [bool] }
                'Text' { $value -is [string] }
                'Date' { $value -is [datetime] -or $value -is [datetimeoffset] }
                'UnsignedInteger' { $value -is [byte] -or $value -is [uint16] -or $value -is [uint32] }
            }
            if (-not $expected) { $reason = 'UnsupportedType' }
            if ($value -is [string]) {
                $reportedCharacters = $value.Length
                if ($value.Length -le 512) { $selected = $value }
                else { $reason = 'SizeLimit' }
            }
            elseif ($value -is [datetimeoffset]) { $selected = $value.ToString('O', [System.Globalization.CultureInfo]::InvariantCulture) }
            elseif ($value -is [datetime]) {
                $selected = $value.ToString('O', [System.Globalization.CultureInfo]::InvariantCulture)
                if ($definition.Kind -eq 'Date' -and $value.Kind -eq [System.DateTimeKind]::Unspecified) { $reason = 'UnspecifiedDateTime' }
            }
            elseif ($value -is [bool] -or $value -is [byte] -or $value -is [sbyte] -or $value -is [int16] -or $value -is [uint16] -or $value -is [int32] -or $value -is [uint32] -or $value -is [int64] -or $value -is [uint64] -or $value -is [decimal]) { $selected = $value }
            else { $reason = 'UnsupportedType' }
        }
    }
    catch { $reason = 'ReadFailed' }
    $properties[$name] = $selected
    if ($null -ne $reason) {
        $unavailable.Add([ordered]@{ property = $name; reason = $reason; sourceType = $sourceType; reportedCharacters = $reportedCharacters })
    }
}
[ordered]@{
    kind = 'optimus.defender-status'
    version = 1
    observedAt = [DateTimeOffset]::UtcNow.ToString('O', [System.Globalization.CultureInfo]::InvariantCulture)
    properties = $properties
    unavailable = @($unavailable.ToArray())
} | ConvertTo-Json -Depth 5 -Compress
