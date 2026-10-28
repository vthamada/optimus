# Read-only local Task Scheduler metadata. No task definitions, actions or credentials.
$q = @{ connected = $false; traversalComplete = $false; foldersVisited = 0; folderAttempts = 0; foldersFailed = 0; tasksExamined = 0; limitReached = $false; issuesOmitted = 0; records = [Collections.Generic.List[object]]::new(); issues = [Collections.Generic.List[object]]::new(); remaining = 180000; stop = $false }
$cleanup = @{ pending = [Collections.Generic.List[string]]::new(); refs = [Collections.Generic.List[object]]::new(); issues = [Collections.Generic.List[object]]::new(); omitted = 0 }
function Task-Issue([string]$code, [string]$message) {
    if ($message.Length -gt 1024) { $message = 'Task metadata error exceeded 1024 characters; original message was omitted.'; $code = 'LimitReached' }
    $item = [ordered]@{ code = $code; message = $message }
    $cost = [Text.Encoding]::UTF8.GetByteCount((Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $item -Compress)) + 2
    if ($q.issues.Count -lt 128 -and $cost -le $q.remaining) { $q.issues.Add($item); $q.remaining -= $cost }
    else { $q.issuesOmitted++; $q.limitReached = $true }
    if ($code -eq 'LimitReached') { $q.limitReached = $true }
}
function Release-TaskCom($value, [string]$name) {
    if ($null -eq $value) { return }
    try { if ([Runtime.InteropServices.Marshal]::IsComObject($value)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value) } }
    catch {
        $cleanup.pending.Add($name); $cleanup.refs.Add($value)
        if ($cleanup.issues.Count -lt 32) { $cleanup.issues.Add([ordered]@{ code = 'ReadFailed'; message = "COM release not confirmed: $name; HRESULT $($_.Exception.HResult)." }) }
        else { $cleanup.omitted++ }
    }
}
function Task-Scalar($value, [string]$field) {
    if ($null -eq $value) { return $null }
    if ($value -is [string]) {
        if ($value.Length -le 2048) { return $value }
        Task-Issue 'LimitReached' "$field exceeded 2048 characters ($($value.Length)); value omitted without truncation."
        return $null
    }
    if ($value -is [datetime] -or $value -is [datetimeoffset]) {
        $kind = if ($value -is [datetimeoffset]) { 'Offset' } else { $value.Kind.ToString() }
        return [ordered]@{ sourceType = $value.GetType().FullName; value = $value.ToString('O', [Globalization.CultureInfo]::InvariantCulture); kind = $kind }
    }
    if ($value -is [bool] -or $value -is [int32] -or $value -is [uint32] -or $value -is [int64] -or $value -is [uint64]) { return $value }
    Task-Issue 'ReadFailed' "$field returned unsupported type $($value.GetType().FullName); value omitted."
    return $null
}
function Visit-TaskFolder($folder, [int]$depth) {
    if ($q.stop) { return }
    $q.foldersVisited++
    $folderId = $q.foldersVisited
    $tasks = $children = $null
    try {
        try {
            $tasks = $folder.GetTasks(1)
            $count = $tasks.Count
            if ($count -isnot [int] -or $count -lt 0) { throw 'Invalid task collection count.' }
            for ($index = 1; $index -le $count; $index++) {
                if ($q.tasksExamined -ge 128) { $q.stop = $true; Task-Issue 'LimitReached' 'The 128-task metadata limit was reached; remaining tasks/folders were not examined.'; break }
                $task = $null
                $q.tasksExamined++
                $record = [ordered]@{ Name = $null; Path = $null; Enabled = $null; State = $null; LastRunTime = $null; NextRunTime = $null; LastTaskResult = $null; NumberOfMissedRuns = $null }
                try {
                    $task = $tasks.Item($index)
                    foreach ($field in @('Name','Path','Enabled','State','LastRunTime','NextRunTime','LastTaskResult','NumberOfMissedRuns')) {
                        try { $record[$field] = Task-Scalar $task.$field "folder#$folderId.task#$index.$field" }
                        catch { Task-Issue 'ReadFailed' "folder#$folderId.task#$index.$field`: $($_.Exception.Message)" }
                    }
                }
                catch { Task-Issue 'ReadFailed' "Task item $index could not be read: $($_.Exception.Message)" }
                finally { Release-TaskCom $task "task#$($q.tasksExamined)" }
                $cost = [Text.Encoding]::UTF8.GetByteCount((Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $record -Depth 6 -Compress)) + 2
                if ($cost -gt $q.remaining) { $q.stop = $true; Task-Issue 'LimitReached' 'The 180000-byte retained metadata budget was reached; this and later records were omitted.'; break }
                $q.remaining -= $cost; $q.records.Add($record)
            }
        }
        catch { $q.foldersFailed++; Task-Issue 'ReadFailed' "Folder task enumeration failed: $($_.Exception.Message)" }
        if ($q.stop) { return }
        try {
            $children = $folder.GetFolders(0)
            $count = $children.Count
            if ($count -isnot [int] -or $count -lt 0) { throw 'Invalid folder collection count.' }
            if ($depth -ge 16 -and $count -gt 0) { Task-Issue 'LimitReached' 'The folder depth limit of 16 was reached; descendants were not examined.'; return }
            for ($index = 1; $index -le $count; $index++) {
                if ($q.stop) { break }
                if ($q.folderAttempts -ge 32) { $q.stop = $true; Task-Issue 'LimitReached' 'The 32-folder limit was reached; remaining folders were not examined.'; break }
                $child = $null
                $q.folderAttempts++
                try { $child = $children.Item($index); Visit-TaskFolder $child ($depth + 1) }
                catch { $q.foldersFailed++; Task-Issue 'ReadFailed' "Child folder $index could not be read: $($_.Exception.Message)" }
                finally { Release-TaskCom $child "folder#$folderId-child#$index-depth#$depth" }
            }
        }
        catch { $q.foldersFailed++; Task-Issue 'ReadFailed' "Child folder enumeration failed: $($_.Exception.Message)" }
    }
    finally { Release-TaskCom $children "folders#$folderId-depth#$depth"; Release-TaskCom $tasks "tasks#$folderId-depth#$depth" }
}
$service = $root = $null
try {
    $service = Microsoft.PowerShell.Utility\New-Object -ComObject 'Schedule.Service'
    $service.Connect()
    $q.folderAttempts = 1
    $root = $service.GetFolder('\')
    $q.connected = $true
    Visit-TaskFolder $root 1
    $q.traversalComplete = -not $q.stop -and $q.foldersFailed -eq 0 -and -not $q.limitReached
}
catch { Task-Issue 'ReadFailed' "Local scheduler query failed: $($_.Exception.Message)" }
finally { Release-TaskCom $root 'root-folder'; Release-TaskCom $service 'scheduler-service' }
if ($cleanup.omitted -gt 0) { $cleanup.issues.Add([ordered]@{ code = 'LimitReached'; message = "$($cleanup.omitted) release details omitted; every pending COM name remains retained." }) }
$result = [ordered]@{ kind = 'optimus.task-inventory'; version = 1; observedAt = [DateTimeOffset]::UtcNow.ToString('O'); query = [ordered]@{ connected = $q.connected; traversalComplete = $q.traversalComplete; foldersVisited = $q.foldersVisited; foldersFailed = $q.foldersFailed; tasksExamined = $q.tasksExamined; limitReached = $q.limitReached; issuesOmitted = $q.issuesOmitted; records = @($q.records.ToArray()); issues = @($q.issues.ToArray()) }; cleanup = [ordered]@{ succeeded = ($cleanup.pending.Count -eq 0); pending = @($cleanup.pending.ToArray()); issues = @($cleanup.issues.ToArray()) } }
Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $result -Depth 10 -Compress
