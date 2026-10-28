# Local WUA history and its reboot flag only; no update search or mutation.
$cleanup = @{ pending = [Collections.Generic.List[string]]::new(); refs = [Collections.Generic.List[object]]::new(); issues = [Collections.Generic.List[object]]::new(); omitted = 0 }
function Update-Issue($list, [string]$code, [string]$message) {
    if ($message.Length -gt 1024) { $message = 'COM message exceeded 1024 characters; content was not retained.'; $code = 'LimitReached' }
    $list.Add([ordered]@{ code = $code; message = $message })
}
function Release-UpdateCom($value, [string]$name) {
    if ($null -eq $value) { return }
    try { if ([Runtime.InteropServices.Marshal]::IsComObject($value)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value) } }
    catch {
        $cleanup.pending.Add($name); $cleanup.refs.Add($value)
        if ($cleanup.issues.Count -lt 64) { Update-Issue $cleanup.issues 'ReadFailed' "COM release not confirmed: $name; HRESULT $($_.Exception.HResult)." }
        else { $cleanup.omitted++ }
    }
}
function Update-Scalar($value, [string]$field, $issues) {
    if ($null -eq $value) { return $null }
    if ($value -is [string]) {
        if ($value.Length -le 2048) { return $value }
        Update-Issue $issues 'LimitReached' "$field exceeded 2048 characters ($($value.Length)); value omitted."
        return $null
    }
    if ($value -is [datetime] -or $value -is [datetimeoffset]) {
        $kind = if ($value -is [datetimeoffset]) { 'Offset' } else { $value.Kind.ToString() }
        return [ordered]@{ sourceType = $value.GetType().FullName; value = $value.ToString('O', [Globalization.CultureInfo]::InvariantCulture); kind = $kind }
    }
    if ($value -is [bool] -or $value -is [int32] -or $value -is [uint32] -or $value -is [int64] -or $value -is [uint64]) { return $value }
    Update-Issue $issues 'ReadFailed' "$field returned unsupported scalar type $($value.GetType().FullName); value omitted."
    return $null
}
$entries = [Collections.Generic.List[object]]::new()
$historyIssues = [Collections.Generic.List[object]]::new()
$history = [ordered]@{ succeeded = $false; totalCount = $null; confirmedTotalCount = $null; requested = 0; examined = 0; limitReached = $false; entries = $entries; issues = $historyIssues }
$session = $searcher = $collection = $null
try {
    $session = Microsoft.PowerShell.Utility\New-Object -ComObject 'Microsoft.Update.Session'
    $searcher = $session.CreateUpdateSearcher()
    $total = $searcher.GetTotalHistoryCount()
    if ($total -isnot [int] -or $total -lt 0) { throw 'WUA returned an invalid history count.' }
    $history.totalCount = $total
    $history.requested = [Math]::Min($total, 128)
    if ($total -gt 128) { $history.limitReached = $true; Update-Issue $historyIssues 'LimitReached' 'Only the first 128 most recent WUA history entries were selected.' }
    $countMatches = $true
    $remaining = 180000
    if ($history.requested -gt 0) {
        $collection = $searcher.QueryHistory(0, $history.requested)
        $count = $collection.Count
        if ($count -ne $history.requested) { $countMatches = $false; Update-Issue $historyIssues 'ReadFailed' 'The returned history count differs from the requested slice.' }
        $take = [Math]::Min([Math]::Max($count, 0), $history.requested)
        for ($index = 0; $index -lt $take; $index++) {
            $entry = $identity = $null
            $recordIssues = [Collections.Generic.List[object]]::new()
            $record = [ordered]@{ updateId = $null; revisionNumber = $null; title = $null; date = $null; operation = $null; resultCode = $null; hResult = $null; clientApplicationId = $null }
            $history.examined++
            try {
                $entry = $collection.Item($index)
                foreach ($pair in @(@('title','Title'), @('date','Date'), @('operation','Operation'), @('resultCode','ResultCode'), @('hResult','HResult'), @('clientApplicationId','ClientApplicationID'))) {
                    try { $record[$pair[0]] = Update-Scalar $entry.($pair[1]) "history[$index].$($pair[0])" $recordIssues }
                    catch { Update-Issue $recordIssues 'ReadFailed' "history[$index].$($pair[0]): $($_.Exception.Message)" }
                }
                try {
                    $identity = $entry.UpdateIdentity
                    $record.updateId = Update-Scalar $identity.UpdateID "history[$index].updateId" $recordIssues
                    $record.revisionNumber = Update-Scalar $identity.RevisionNumber "history[$index].revisionNumber" $recordIssues
                }
                catch { Update-Issue $recordIssues 'ReadFailed' "history[$index].identity: $($_.Exception.Message)" }
            }
            catch { Update-Issue $recordIssues 'ReadFailed' "history[$index]: $($_.Exception.Message)" }
            finally { Release-UpdateCom $identity "history[$index].identity"; Release-UpdateCom $entry "history[$index]" }
            $candidate = [ordered]@{ entry = $record; issues = @($recordIssues.ToArray()) }
            $size = [Text.Encoding]::UTF8.GetByteCount((Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $candidate -Depth 8 -Compress)) + 2
            if ($size -gt $remaining) { $history.limitReached = $true; Update-Issue $historyIssues 'LimitReached' 'The 180000-byte history JSON budget was reached; later entries were not retained.'; break }
            $remaining -= $size; $entries.Add($record)
            foreach ($issue in $recordIssues) { $historyIssues.Add($issue); if ($issue.code -eq 'LimitReached') { $history.limitReached = $true } }
        }
    }
    $after = $searcher.GetTotalHistoryCount()
    if ($after -isnot [int] -or $after -lt 0) { throw 'WUA returned an invalid confirmation count.' }
    $history.confirmedTotalCount = $after
    if ($after -ne $total) { Update-Issue $historyIssues 'ReadFailed' 'The WUA history count changed during the query; the slice is not a stable catalog snapshot.' }
    $history.succeeded = $countMatches -and ($after -eq $total)
}
catch { Update-Issue $historyIssues 'ReadFailed' $_.Exception.Message }
finally { Release-UpdateCom $collection 'history.collection'; Release-UpdateCom $searcher 'history.searcher'; Release-UpdateCom $session 'history.session' }
$rebootIssues = [Collections.Generic.List[object]]::new()
$reboot = [ordered]@{ succeeded = $false; required = $null; issues = $rebootIssues }
$systemInfo = $null
try {
    $systemInfo = Microsoft.PowerShell.Utility\New-Object -ComObject 'Microsoft.Update.SystemInfo'
    $reboot.required = Update-Scalar $systemInfo.RebootRequired 'WUA.RebootRequired' $rebootIssues
    $reboot.succeeded = $true
}
catch { Update-Issue $rebootIssues 'ReadFailed' $_.Exception.Message }
finally { Release-UpdateCom $systemInfo 'reboot.systemInfo' }
if ($cleanup.omitted -gt 0) { Update-Issue $cleanup.issues 'LimitReached' "$($cleanup.omitted) additional COM release details omitted; every pending COM name was retained." }
$result = [ordered]@{ kind = 'optimus.update-history'; version = 1; observedAt = [DateTimeOffset]::UtcNow.ToString('O'); history = $history; reboot = $reboot; cleanup = [ordered]@{ succeeded = ($cleanup.pending.Count -eq 0); pending = @($cleanup.pending.ToArray()); issues = @($cleanup.issues.ToArray()) } }
Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $result -Depth 10 -Compress
