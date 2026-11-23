[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $DiscoveryPath,
    [Parameter(Mandatory)] [ValidatePattern('^[^\s\p{C}]{1,128}$')] [string] $Operation,
    [Parameter()] [string] $ArgumentsJson = '{}',
    [Parameter()] [switch] $Summary,
    [Parameter()] [switch] $WorkerTopology,
    [Parameter()] [switch] $BudgetSummary
)

$ErrorActionPreference = 'Stop'
$maximumFrameBytes = 4MB
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)

function Read-Exact {
    param([IO.Stream] $Stream, [int] $Length)
    $buffer = [byte[]]::new($Length)
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Stream.Read($buffer, $offset, $Length - $offset)
        if ($read -eq 0) { throw 'The source Worker disconnected before the frame was complete.' }
        $offset += $read
    }
    return $buffer
}

function Write-Frame {
    param([IO.Stream] $Stream, [object] $Value)
    $json = $Value | ConvertTo-Json -Compress -Depth 32
    $payload = $strictUtf8.GetBytes($json)
    if ($payload.Length -lt 1 -or $payload.Length -gt $maximumFrameBytes) { throw 'The request exceeds the local Worker frame bound.' }
    $header = [BitConverter]::GetBytes([int]$payload.Length)
    if (-not [BitConverter]::IsLittleEndian) { [Array]::Reverse($header) }
    $Stream.Write($header, 0, $header.Length)
    $Stream.Write($payload, 0, $payload.Length)
    $Stream.Flush()
}

function Read-Frame {
    param([IO.Stream] $Stream)
    $header = Read-Exact $Stream 4
    if (-not [BitConverter]::IsLittleEndian) { [Array]::Reverse($header) }
    $length = [BitConverter]::ToInt32($header, 0)
    if ($length -lt 1 -or $length -gt $maximumFrameBytes) { throw 'The source Worker returned an invalid frame length.' }
    $payload = Read-Exact $Stream $length
    return $strictUtf8.GetString($payload) | ConvertFrom-Json
}

$exactDiscovery = [IO.Path]::GetFullPath($DiscoveryPath)
if (-not [IO.Path]::IsPathFullyQualified($DiscoveryPath) -or -not [IO.File]::Exists($exactDiscovery)) {
    throw 'DiscoveryPath must identify an existing absolute source Worker discovery file.'
}
$discovery = Get-Content -LiteralPath $exactDiscovery -Raw | ConvertFrom-Json
if ($discovery.product -ne 'Pegasus Cerebral' -or $discovery.version -ne 1) { throw 'The discovery file is not a compatible Pegasus Cerebral source Worker.' }
$pipeName = [string]$discovery.endpoint.pipeName
$instanceId = [string]$discovery.endpoint.instanceId
if ($pipeName -notmatch '^[A-Za-z0-9._-]{1,160}$') { throw 'The source Worker pipe name is invalid.' }
$instance = [Guid]::Empty
if (-not [Guid]::TryParseExact($instanceId, 'N', [ref]$instance) -or $instance -eq [Guid]::Empty) { throw 'The source Worker instance ID is invalid.' }

if ($WorkerTopology -and ($Summary -or $Operation -cne 'settings.get')) {
    throw 'WorkerTopology is a fixed credential-free projection for settings.get only.'
}
if ($BudgetSummary -and ($Summary -or $WorkerTopology -or $Operation -cnotin @('agents.list', 'models.list'))) {
    throw 'BudgetSummary is a fixed prompt-free projection for agents.list or models.list only.'
}
$arguments = $ArgumentsJson | ConvertFrom-Json -AsHashtable
if ($arguments -isnot [Collections.IDictionary]) { throw 'ArgumentsJson must contain one JSON object.' }
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut,
    [IO.Pipes.PipeOptions]::Asynchronous, [Security.Principal.TokenImpersonationLevel]::Identification)
try {
    $pipe.Connect(10000)
    Write-Frame $pipe ([ordered]@{ product = 'Pegasus Cerebral'; protocol = 1; instanceId = $instanceId })
    $hello = Read-Frame $pipe
    if ($hello.product -ne 'Pegasus Cerebral' -or $hello.protocol -ne 1 -or $hello.instanceId -ne $instanceId) {
        throw 'The connected pipe did not return the expected source Worker identity.'
    }
    Write-Frame $pipe ([ordered]@{
        kind = 'command'
        command = [ordered]@{ operation = $Operation; arguments = $arguments; profileId = $null }
    })
    $response = Read-Frame $pipe
    if ($WorkerTopology) {
        # Project in memory before any serialization: settings also contain owner and peer credentials.
        if (-not $response.success) { throw 'The native settings read failed; no settings body was emitted.' }
        $data = $response.data
        if ($null -eq $data -or $null -eq $data.PSObject.Properties['workers']) {
            throw 'The native settings response has no worker topology.'
        }
        $projection = [ordered]@{ success = $true; workers = @($data.workers | ForEach-Object {
            [ordered]@{ id = $_.id; name = $_.name; requiresConfiguration = [bool]$_.requiresConfiguration;
                hasPeerToken = -not [string]::IsNullOrEmpty($_.peerToken);
                hasLegacyKey = -not [string]::IsNullOrEmpty($_.accessKey) }
        }); maxConcurrentJobs = $data.maxConcurrentJobs; reservedOwnerSlots = $data.reservedOwnerSlots;
            reservedMemoryMb = $data.reservedMemoryMb; llamaServerPath = $data.llamaServerPath; defaultModelId = $data.defaultModelId;
            defaultModelWorkerId = $data.defaultModelWorkerId }
        $projection | ConvertTo-Json -Compress -Depth 8
    }
    elseif ($BudgetSummary) {
        if (-not $response.success) { throw 'The native budget read failed; no response body was emitted.' }
        $data = @($response.data)
        if ($data.Count -gt 256) { throw 'The budget catalog exceeded the bounded projection.' }
        $rows = @($data | ForEach-Object {
            if ($Operation -ceq 'agents.list') {
                [ordered]@{ id = $_.id; role = $_.role; modelId = $_.modelId; workerId = $_.workerId;
                    modelWorkerId = $_.modelWorkerId; contextTokens = $_.contextTokens; responseTokens = $_.responseTokens;
                    followsDefaultModel = $_.followsDefaultModel; followsModelBudgets = $_.followsModelBudgets;
                    automaticCompaction = $_.automaticCompaction; compactionTriggerPercent = $_.compactionTriggerPercent }
            }
            else {
                [ordered]@{ id = $_.id; contextTokens = $_.contextTokens; recommendedContextTokens = $_.recommendedContextTokens;
                    maximumContextTokens = $_.maximumContextTokens; parallelRequests = $_.parallelRequests;
                    automaticContextSizing = $_.automaticContextSizing; hasVisionProjector = -not [string]::IsNullOrEmpty($_.visionProjectorPath) }
            }
        })
        [ordered]@{ success = $true; operation = $Operation; budgets = $rows } | ConvertTo-Json -Compress -Depth 8
    }
    elseif ($Summary) {
        $data = $response.data
        $projection = [ordered]@{ success = [bool]$response.success; error = $response.error }
        foreach ($name in @('id', 'status', 'revision', 'cancelRequested', 'instanceId', 'adapterId', 'adapterVersion', 'inputTokens', 'verifiedAt')) {
            if ($null -ne $data -and $null -ne $data.PSObject.Properties[$name]) { $projection[$name] = $data.$name }
        }
        if ($null -ne $data -and $null -ne $data.PSObject.Properties['effect']) { $projection.effectState = $data.effect.state }
        $projection | ConvertTo-Json -Compress -Depth 8
    }
    else { $response | ConvertTo-Json -Compress -Depth 32 }
    if (-not $response.success) { exit 1 }
}
finally { $pipe.Dispose() }
