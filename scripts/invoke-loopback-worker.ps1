[CmdletBinding()]
param(
    [Parameter()] [string] $Url = 'http://127.0.0.1:2667',
    [Parameter(Mandatory)] [ValidatePattern('^[^\s\p{C}]{1,128}$')] [string] $Operation,
    [Parameter()] [string] $ArgumentsJson = '{}',
    [Parameter()] [switch] $Summary
)

$ErrorActionPreference = 'Stop'
$uri = [Uri]$Url
if ($uri.Scheme -ne 'http' -or -not $uri.IsLoopback -or $uri.UserInfo.Length -ne 0) {
    throw 'Url must be an unauthenticated HTTP loopback address.'
}
$arguments = $ArgumentsJson | ConvertFrom-Json -AsHashtable
if ($arguments -isnot [Collections.IDictionary]) { throw 'ArgumentsJson must contain one JSON object.' }
$body = [ordered]@{ operation = $Operation; arguments = $arguments; profileId = $null } | ConvertTo-Json -Compress -Depth 32
$response = Invoke-RestMethod -Method Post -Uri ([Uri]::new($uri, '/api/command')) -ContentType 'application/json' -Body $body -TimeoutSec 900
if ($Summary) {
    $data = $response.data
    $projection = [ordered]@{ success = [bool]$response.success; error = $response.error }
    foreach ($name in @('id', 'name', 'path', 'state', 'visionProjectorPath', 'instanceId', 'adapterId', 'adapterVersion', 'inputTokens', 'verifiedAt')) {
        if ($null -ne $data -and $null -ne $data.PSObject.Properties[$name]) { $projection[$name] = $data.$name }
    }
    $projection | ConvertTo-Json -Compress -Depth 8
}
else { $response | ConvertTo-Json -Compress -Depth 32 }
if (-not $response.success) { exit 1 }
