[CmdletBinding()]
param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $projectRoot '.tools'
$sdkRoot = Join-Path $toolRoot 'dotnet'
$version = '10.0.403'
$url = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$version/dotnet-sdk-$version-win-x64.zip"
$sha512 = '7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48'
$archive = Join-Path $toolRoot "dotnet-sdk-$version-win-x64.zip"
Write-Output "SDK local: $version"
Write-Output "Download: $url"
Write-Output "Arquivo: $archive"
Write-Output "Extracao: $sdkRoot"
Write-Output "Dados locais do SDK: $(Join-Path $toolRoot 'dotnet-home')"
Write-Output "SHA-512: $sha512"
Write-Output 'Nao altera PATH global, Registro, software do sistema ou configuracoes de outros projetos.'
if (-not $Apply) { Write-Output 'Plano somente. Use -Apply para autorizar estas gravacoes.'; exit 0 }
$environmentKeys = @('DOTNET_CLI_HOME','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_SKIP_FIRST_TIME_EXPERIENCE')
$originalEnvironment = @{}
foreach ($key in $environmentKeys) { $originalEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
try {
$env:DOTNET_CLI_HOME = Join-Path $toolRoot 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$executable = Join-Path $sdkRoot 'dotnet.exe'
if (Test-Path -LiteralPath $executable) {
    $installedVersion = & $executable --version
    if ($LASTEXITCODE -ne 0 -or $installedVersion -ne $version) { throw 'Ja existe SDK diferente ou invalido. Nenhum arquivo sera substituido.' }
    Write-Output 'SDK correto ja disponivel; nenhuma instalacao necessaria.'
    exit 0
}
if (Test-Path -LiteralPath $sdkRoot) { throw 'A pasta de destino ja existe sem SDK reconhecido. Inspecione antes de continuar.' }
New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $url -OutFile $archive -TimeoutSec 240 }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ine $sha512) { throw 'SHA-512 diverge. Arquivo preservado; SDK nao extraido.' }
Expand-Archive -LiteralPath $archive -DestinationPath $sdkRoot
& $executable --version
$bootstrapExit = $LASTEXITCODE
} finally {
    foreach ($key in $environmentKeys) {
        if ($null -eq $originalEnvironment[$key]) { Remove-Item -LiteralPath "Env:\$key" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($key, $originalEnvironment[$key], 'Process') }
    }
}
exit $bootstrapExit
