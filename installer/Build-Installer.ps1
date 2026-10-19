param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactsDirectory = Join-Path $projectRoot 'artifacts'
$publishDirectory = Join-Path $artifactsDirectory 'publish-win-x64'
$installerDirectory = Join-Path $artifactsDirectory 'installer'
$installerPath = Join-Path $installerDirectory 'TunnlSplitVPN-Setup-win-x64.exe'
$validationInstallDirectory = Join-Path $installerDirectory 'validation-install'
$setupScript = Join-Path $PSScriptRoot 'TunnlSplitVPN.iss'

if (-not $CompilerPath) {
    $compilerCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    $candidates = @(
        $compilerCommand.Source,
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )
    $CompilerPath = $candidates |
        Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
        Select-Object -First 1
}

if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath)) {
    throw 'Inno Setup 6 or 7 is required. Install it or pass -CompilerPath with the path to ISCC.exe.'
}

New-Item -ItemType Directory -Path $artifactsDirectory, $installerDirectory -Force | Out-Null
Remove-Item -LiteralPath $publishDirectory, $validationInstallDirectory `
    -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publishDirectory | Out-Null

dotnet publish (Join-Path $projectRoot 'TunnlSplitVPN.csproj') `
    --configuration $Configuration `
    --self-contained false `
    -p:PlatformTarget=x64 `
    -p:NuGetAudit=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$compilerArguments = @(
    '/Qp',
    "/O$installerDirectory",
    '/FTunnlSplitVPN-Setup-win-x64',
    "/DSourceDir=$publishDirectory",
    $setupScript
)
& $CompilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Inno Setup did not create '$installerPath'."
}

$installArguments = @(
    '/VERYSILENT',
    '/SUPPRESSMSGBOXES',
    '/NORESTART',
    '/CURRENTUSER',
    "/DIR=$validationInstallDirectory"
)
$installProcess = Start-Process -FilePath $installerPath `
    -ArgumentList $installArguments -Wait -PassThru
if ($installProcess.ExitCode -ne 0) {
    throw "The isolated installer test failed with exit code $($installProcess.ExitCode)."
}

$requiredFiles = @(
    'TunnlSplitVPN.exe',
    'TunnlSplitVPN.dll',
    'TunnlSplitVPN.runtimeconfig.json',
    'TunnlSplitVPN.deps.json'
)
foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $validationInstallDirectory $file))) {
        throw "The installer did not install required file '$file'."
    }
}

$uninstallerPath = Join-Path $validationInstallDirectory 'unins000.exe'
if (-not (Test-Path -LiteralPath $uninstallerPath)) {
    throw 'The isolated installation did not create its uninstaller.'
}

$uninstallProcess = Start-Process -FilePath $uninstallerPath `
    -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
if ($uninstallProcess.ExitCode -ne 0) {
    throw "The isolated uninstaller test failed with exit code $($uninstallProcess.ExitCode)."
}
if (Test-Path -LiteralPath (Join-Path $validationInstallDirectory 'TunnlSplitVPN.exe')) {
    throw 'The isolated uninstaller did not remove the application files.'
}
Remove-Item -LiteralPath $validationInstallDirectory -Force -ErrorAction SilentlyContinue

$version = (Get-Item -LiteralPath (Join-Path $publishDirectory 'TunnlSplitVPN.exe')).VersionInfo.ProductVersion
Write-Host ''
Write-Host "Installer created and install/uninstall verified: $installerPath"
Write-Host "Application version: $version"
Write-Host 'Target: Windows 10/11 x64; .NET 8 Desktop Runtime is required.'
Write-Host 'WireSock must be installed separately.'
