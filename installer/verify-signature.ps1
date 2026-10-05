[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$SignTool
)
$ErrorActionPreference = 'Stop'

function Test-AbsoluteWindowsPath([string]$Candidate) {
    return $Candidate -match '^(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+\\)'
}

if (-not (Test-AbsoluteWindowsPath $Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw 'The verification input must be an existing file at an absolute path.'
}
if (-not (Test-AbsoluteWindowsPath $SignTool) -or -not (Test-Path -LiteralPath $SignTool -PathType Leaf)) {
    throw 'SignTool must be an existing absolute path.'
}

$verification = & $SignTool verify /pa /all /v $Path 2>&1
$verifyExitCode = $LASTEXITCODE
if ($verifyExitCode -ne 0) { throw "SignTool verification failed with exit code $verifyExitCode. $($verification -join [Environment]::NewLine)" }

$authenticode = Get-AuthenticodeSignature -LiteralPath $Path
if ($authenticode.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "Authenticode status is $($authenticode.Status); expected Valid."
}
if ($null -eq $authenticode.TimeStamperCertificate) {
    throw 'The Authenticode signature has no verified timestamp certificate.'
}
Write-Host "Verified signed file with timestamp: $Path"
