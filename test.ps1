[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$executable = Join-Path $projectDirectory 'PcInternetMonitor.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw '먼저 build.ps1 -UpdateExecutable로 실행 파일을 빌드하세요.' }
$testDirectory = Join-Path $projectDirectory 'tests'
$originalSources = @{}
if (Test-Path -LiteralPath $testDirectory) {
    Get-ChildItem -LiteralPath $testDirectory -Recurse -File -Filter '*.cs' | ForEach-Object {
        $originalSources[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
}
function Invoke-MonitorTest([string]$Arguments, [int]$ExpectedExitCode = 0) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable, $Arguments)
    $start.WorkingDirectory = $projectDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            # This process is an isolated helper started above, never a user window.
            $process.Kill()
            $process.WaitForExit()
            throw "검사 제한 시간 초과: $Arguments"
        }
        $errorText = $errorTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $ExpectedExitCode) { throw "$Arguments 종료 코드 $($process.ExitCode): $errorText" }
        Write-Output "PASS $Arguments (exit $ExpectedExitCode)"
    } finally { $process.Dispose() }
}
foreach ($mode in @('--self-test', '--window-close-test', '--feedback-test', '--smoke-test', '--recording-test', '--lifecycle-test')) {
    Invoke-MonitorTest $mode
}
Invoke-MonitorTest ('--window-close-helper invalid ' + [Guid]::NewGuid().ToString('N')) 3
foreach ($source in $originalSources.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $source.Key -PathType Leaf) -or
        (Get-FileHash -LiteralPath $source.Key -Algorithm SHA256).Hash -ne $source.Value) {
        throw '기존 테스트 소스가 변경됐습니다.'
    }
}
# App-level scopes own cleanup. Never delete another run or old user data here.
if ($env:GITHUB_ACTIONS -eq 'true') {
    if (@(Get-ChildItem -LiteralPath $testDirectory -Force -Directory -Filter '.run-*').Count -ne 0 -or
        @(Get-ChildItem -LiteralPath $testDirectory -Force -Recurse -File | Where-Object Extension -ne '.cs').Count -ne 0) {
        throw 'CI 검사 임시 파일이 남았습니다.'
    }
}
Write-Output 'PASS: six regression modes, failed-helper cleanup and test source preservation.'
