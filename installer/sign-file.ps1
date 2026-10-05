[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][ValidateSet('CertificateStore', 'ArtifactSigning')][string]$Provider,
    [Parameter(Mandatory)][string]$SignTool,
    [string]$Thumbprint,
    [string]$Dlib,
    [string]$Metadata
)
$ErrorActionPreference = 'Stop'

function Test-AbsoluteWindowsPath([string]$Candidate) {
    return $Candidate -match '^(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+\\)'
}

if (-not (Test-AbsoluteWindowsPath $Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw 'The signing input must be an existing file at an absolute path.'
}
if (-not (Test-AbsoluteWindowsPath $SignTool) -or -not (Test-Path -LiteralPath $SignTool -PathType Leaf)) {
    throw 'SignTool must be an existing absolute path.'
}

$common = @('sign', '/fd', 'SHA256')
if ($Provider -eq 'CertificateStore') {
    if ($Thumbprint -notmatch '^(?i:[0-9a-f]{40})$') { throw 'A valid certificate SHA-1 thumbprint is required.' }
    $arguments = $common + @('/sha1', $Thumbprint, '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', $Path)
} else {
    if ([string]::IsNullOrWhiteSpace($Dlib) -or -not (Test-AbsoluteWindowsPath $Dlib) -or -not (Test-Path -LiteralPath $Dlib -PathType Leaf)) {
        throw 'Artifact Signing Dlib must be an existing absolute file path.'
    }
    if ([string]::IsNullOrWhiteSpace($Metadata) -or -not (Test-AbsoluteWindowsPath $Metadata) -or -not (Test-Path -LiteralPath $Metadata -PathType Leaf)) {
        throw 'Artifact Signing metadata must be an existing absolute file path.'
    }
    try { $metadataObject = Get-Content -LiteralPath $Metadata -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'Artifact Signing metadata must contain valid JSON.' }
    foreach ($field in @('Endpoint', 'CodeSigningAccountName', 'CertificateProfileName')) {
        if ([string]::IsNullOrWhiteSpace([string]$metadataObject.$field)) { throw "Artifact Signing metadata is missing required field $field." }
    }
    $endpoint = $null
    if (-not [Uri]::TryCreate([string]$metadataObject.Endpoint, [UriKind]::Absolute, [ref]$endpoint) -or $endpoint.Scheme -ne 'https') {
        throw 'Artifact Signing Endpoint must be an absolute HTTPS URI.'
    }
    $allowedSigningRegions = @('brs','cus','eus','jpe','krc','ncus','neu','plc','scus','swn','wcus','weu','wus','wus2','wus3')
    $allowedSigningHosts = $allowedSigningRegions | ForEach-Object { $_ + '.codesigning.azure.net' }
    if ($endpoint.Host -notin $allowedSigningHosts -or $endpoint.Port -ne 443 -or $endpoint.AbsolutePath -notin @('', '/') -or $endpoint.Query -or $endpoint.Fragment -or $endpoint.UserInfo) {
        throw 'Artifact Signing Endpoint must be a supported Microsoft regional codesigning.azure.net endpoint.'
    }
    $arguments = $common + @('/tr', 'http://timestamp.acs.microsoft.com', '/td', 'SHA256', '/dlib', $Dlib, '/dmdf', $Metadata, $Path)
}

& $SignTool @arguments
if ($LASTEXITCODE -ne 0) { throw "SignTool signing failed with exit code $LASTEXITCODE." }

& (Join-Path $PSScriptRoot 'verify-signature.ps1') -Path $Path -SignTool $SignTool
if ($LASTEXITCODE -ne 0) { throw 'Signature verification after signing failed.' }
