[CmdletBinding()]
param(
    [ValidateSet('restore', 'build', 'test', 'run', 'diagnose', 'inventory', 'defender-status', 'device-inventory', 'services', 'update-history', 'folder-space', 'processes', 'scheduled-tasks', 'storage', 'connections', 'firewall', 'application-failures', 'pending-restart', 'restore-points', 'bitlocker-status', 'restart-history', 'memory-results', 'hardware-profile', 'investigate', 'memory-import', 'dump-analyze', 'firmware-notes', 'history', 'preview', 'publish', 'format')]
    [string]$Task = 'build',
    [string]$OutputPath,
    [string]$DataDirectory,
    [string]$ProfilePath,
    [string]$FolderPath,
    [ValidateRange(1,365)][int]$Days = 30,
    [ValidateRange(1,4)][int]$MaximumSteps = 4,
    [string]$Question,
    [string]$InputPath,
    [string]$FirmwareUrl,
    [string]$Manufacturer,
    [string]$DumpPath,
    [switch]$UseInvestigationAi,
    [ValidateRange(0,7)][int]$PreviewTab = 0,
    [switch]$AnalyzePreview,
    [switch]$InventoryPreview,
    [switch]$MaintenancePreview,
    [switch]$MaintenanceToolsPreview,
    [switch]$SystemToolsPreview,
    [switch]$SupportToolsPreview,
    [switch]$RecoveryToolsPreview,
    [switch]$InstabilityPreview,
    [string]$MemoryReportPreview,
    [string]$FolderSpacePreview,
    [ValidateSet('sfc-verify','dism-check','dism-scan')][string]$IntegrityPreview
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $projectRoot '.tools'
$dotnet = Join-Path $toolRoot 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $installed) { throw 'SDK ausente. Leia scripts/bootstrap-dotnet.ps1; execute primeiro sem -Apply para revisar os caminhos.' }
    $dotnet = $installed.Source
}
$environmentKeys = @('DOTNET_ROOT','DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','MSBUILDDISABLENODEREUSE','TEMP','TMP')
$originalEnvironment = @{}
foreach ($key in $environmentKeys) { $originalEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
Push-Location $projectRoot
try {
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    $env:DOTNET_CLI_HOME = Join-Path $toolRoot 'dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $toolRoot 'nuget\packages'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:TEMP = Join-Path $toolRoot 'tmp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
    switch ($Task) {
        'restore' { & $dotnet restore Optimus.slnx }
        'build' { & $dotnet build Optimus.slnx --no-restore --disable-build-servers }
        'test' { & $dotnet test tests/Optimus.Tests/Optimus.Tests.csproj --no-restore --disable-build-servers --logger 'console;verbosity=normal' }
        'format' { & $dotnet format Optimus.slnx --no-restore --verify-no-changes }
        'publish' { & $dotnet publish src/Optimus.Desktop/Optimus.Desktop.csproj --no-restore --configuration Release --runtime win-x64 --self-contained true --output artifacts/app --disable-build-servers }
        default {
            $launchArguments = [System.Collections.Generic.List[string]]::new()
            $launchArguments.Add('run')
            $launchArguments.Add('--project')
            $launchArguments.Add($(if ($Task -in @('run', 'preview')) { 'src/Optimus.Desktop/Optimus.Desktop.csproj' } else { 'src/Optimus.Cli/Optimus.Cli.csproj' }))
            $launchArguments.Add('--no-build')
            $launchArguments.Add('--')
            if ($Task -in @('diagnose', 'inventory', 'defender-status', 'device-inventory', 'services', 'update-history', 'folder-space', 'processes', 'scheduled-tasks', 'storage', 'connections', 'firewall', 'application-failures', 'pending-restart', 'restore-points', 'bitlocker-status', 'restart-history', 'memory-results', 'hardware-profile', 'investigate', 'memory-import', 'dump-analyze', 'firmware-notes', 'history')) { $launchArguments.Add($Task) }
            if ($Task -eq 'folder-space') {
                if (-not $FolderPath) { throw 'Informe -FolderPath com uma pasta local explícita.' }
                $launchArguments.Add('--path'); $launchArguments.Add($FolderPath)
            } elseif ($FolderPath) { throw '-FolderPath aplica-se somente a folder-space.' }
            if ($DataDirectory) { $launchArguments.Add('--data-dir'); $launchArguments.Add($DataDirectory) }
            if ($Task -in @('restart-history','memory-results','investigate')) { $launchArguments.Add('--days'); $launchArguments.Add($Days.ToString()) }
            elseif ($PSBoundParameters.ContainsKey('Days')) { throw '-Days aplica-se a restart-history, memory-results ou investigate.' }
            if ($Task -eq 'investigate') {
                $launchArguments.Add('--steps'); $launchArguments.Add($MaximumSteps.ToString())
                if ($Question) { $launchArguments.Add('--question'); $launchArguments.Add($Question) }
                if ($UseInvestigationAi) { $launchArguments.Add('--ai'); $launchArguments.Add('true') }
                if ($DumpPath) { $launchArguments.Add('--dump'); $launchArguments.Add($DumpPath) }
                if ($InputPath) { $launchArguments.Add('--memory-report'); $launchArguments.Add($InputPath) }
            } elseif ($Task -eq 'memory-import') {
                if (-not $InputPath) { throw 'Informe -InputPath com um relatório HTML local.' }
                $launchArguments.Add('--input'); $launchArguments.Add($InputPath)
            } elseif ($Task -eq 'dump-analyze') {
                if (-not $DumpPath) { throw 'Informe -DumpPath com um dump local explícito.' }
                $launchArguments.Add('--dump'); $launchArguments.Add($DumpPath)
            } elseif ($Task -eq 'firmware-notes') {
                if (-not $FirmwareUrl -or -not $Manufacturer) { throw 'Informe -FirmwareUrl e -Manufacturer explicitamente.' }
                $launchArguments.Add('--url'); $launchArguments.Add($FirmwareUrl)
                $launchArguments.Add('--manufacturer'); $launchArguments.Add($Manufacturer)
            }
            if ($ProfilePath) {
                if ($Task -notin @('run', 'preview', 'investigate', 'dump-analyze')) { throw '-ProfilePath neste script aplica-se a run, preview, investigate ou dump-analyze; outros módulos CLI usam --profile diretamente.' }
                $launchArguments.Add('--profile'); $launchArguments.Add($ProfilePath)
            }
            if ($Task -eq 'preview') {
                if (-not $OutputPath) { throw 'Informe -OutputPath para uma imagem PNG nova.' }
                $launchArguments.Add('--render-preview'); $launchArguments.Add($OutputPath)
                $launchArguments.Add('--preview-tab'); $launchArguments.Add($PreviewTab.ToString())
                if ($AnalyzePreview) { $launchArguments.Add('--analyze-preview'); $launchArguments.Add('true') }
                if ($InventoryPreview) { $launchArguments.Add('--inventory-preview'); $launchArguments.Add('true') }
                if ($MaintenancePreview) { $launchArguments.Add('--maintenance-preview'); $launchArguments.Add('true') }
                if ($MaintenanceToolsPreview) { $launchArguments.Add('--maintenance-tools-preview'); $launchArguments.Add('true') }
                if ($SystemToolsPreview) { $launchArguments.Add('--system-tools-preview'); $launchArguments.Add('true') }
                if ($SupportToolsPreview) { $launchArguments.Add('--support-tools-preview'); $launchArguments.Add('true') }
                if ($RecoveryToolsPreview) { $launchArguments.Add('--recovery-tools-preview'); $launchArguments.Add('true') }
                if ($InstabilityPreview) { $launchArguments.Add('--instability-preview'); $launchArguments.Add('true') }
                if ($MemoryReportPreview) { $launchArguments.Add('--memory-report-preview'); $launchArguments.Add($MemoryReportPreview) }
                if ($DumpPath) { $launchArguments.Add('--dump-preview'); $launchArguments.Add($DumpPath) }
                if ($FolderSpacePreview) { $launchArguments.Add('--folder-space-preview'); $launchArguments.Add($FolderSpacePreview) }
                if ($IntegrityPreview) { $launchArguments.Add('--integrity-preview'); $launchArguments.Add($IntegrityPreview) }
            } elseif ($OutputPath) {
                if ($Task -notin @('diagnose','inventory','defender-status','device-inventory','services','update-history','folder-space','processes','scheduled-tasks','storage','connections','firewall','application-failures', 'pending-restart', 'restore-points', 'bitlocker-status', 'restart-history', 'memory-results', 'hardware-profile', 'investigate', 'memory-import', 'dump-analyze', 'firmware-notes')) { throw '-OutputPath se aplica a consultas ou preview.' }
                $launchArguments.Add('--report'); $launchArguments.Add($OutputPath)
            }
            & $dotnet @launchArguments
        }
    }
    $taskExit = $LASTEXITCODE
} finally {
    Pop-Location
    foreach ($key in $environmentKeys) {
        if ($null -eq $originalEnvironment[$key]) { Remove-Item -LiteralPath "Env:\$key" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($key, $originalEnvironment[$key], 'Process') }
    }
}
exit $taskExit
