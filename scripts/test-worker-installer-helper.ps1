[CmdletBinding()]
param([string]$SourceRoot = (Join-Path $PSScriptRoot '..\deploy\Workers\win-x64'))

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path $projectRoot ('artifacts\worker-helper-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $testRoot 'Worker'
$receipt = Join-Path $testRoot 'receipt.json'
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    & (Join-Path $projectRoot 'setup\Install-CerebralWorker.ps1') -SourceRoot $SourceRoot -DestinationRoot $destination -ReceiptPath $receipt
    $expected = (Get-FileHash -LiteralPath (Join-Path $SourceRoot 'Cerebral.Worker.dll') -Algorithm SHA256).Hash
    [IO.File]::WriteAllText((Join-Path $destination 'Cerebral.Worker.dll'), 'stale')
    & (Join-Path $projectRoot 'setup\Install-CerebralWorker.ps1') -SourceRoot $SourceRoot -DestinationRoot $destination -ReceiptPath $receipt
    $actual = (Get-FileHash -LiteralPath (Join-Path $destination 'Cerebral.Worker.dll') -Algorithm SHA256).Hash
    $state = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if (-not $state.ok -or $expected -cne $actual) { throw 'Worker helper replacement acceptance failed.' }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $destination 'Cerebral.Worker.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--version')
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Published Worker version probe timed out.' }
        $version = $stdout.GetAwaiter().GetResult().Trim()
        if ($process.ExitCode -ne 0 -or $version -notmatch '^Pegasus Cerebral Worker ') {
            throw "Published Worker did not execute after helper replacement. Exit=$($process.ExitCode) Error=$($stderr.GetAwaiter().GetResult())"
        }
    }
    finally { $process.Dispose() }
    Write-Host "Standalone Worker helper passed first install, replacement, receipt, and executable checks: $version"
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        for ($attempt = 0; $attempt -lt 8; $attempt++) {
            try { Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction Stop; break }
            catch {
                if ($attempt -eq 7) { throw }
                Start-Sleep -Milliseconds (250 * ($attempt + 1))
            }
        }
    }
}
