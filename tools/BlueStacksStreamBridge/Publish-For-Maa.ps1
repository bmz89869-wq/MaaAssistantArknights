param(
    [Parameter(Mandatory = $true)]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$publishDir = Join-Path $projectDir 'artifacts'
$tempDir = Join-Path $projectDir '.tmp\publish'
$destinationDir = [IO.Path]::GetFullPath($Destination)

New-Item -ItemType Directory -Path $publishDir, $tempDir, $destinationDir -Force | Out-Null
$env:TEMP = $tempDir
$env:TMP = $tempDir
$env:MSBUILDDISABLENODEREUSE = '1'

dotnet publish (Join-Path $projectDir 'BlueStacksStreamBridge.csproj') -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "BlueStacksStreamBridge publish failed with exit code $LASTEXITCODE."
}

Get-ChildItem -LiteralPath $publishDir -File | Copy-Item -Destination $destinationDir -Force
Copy-Item -LiteralPath (Join-Path $projectDir 'vendor\scrcpy-server-v4.1') -Destination $destinationDir -Force

Write-Output "BlueStacksStreamBridge deployed to $destinationDir"
