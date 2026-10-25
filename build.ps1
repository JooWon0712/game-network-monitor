[CmdletBinding()]
param([switch]$UpdateExecutable)
$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkDir 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework C# 컴파일러를 찾지 못했습니다.' }
$sourceFiles = Get-ChildItem -LiteralPath $projectDir -Filter '*.cs' -File | ForEach-Object { $_.FullName }
$executablePath = Join-Path $projectDir 'GameNetworkMonitor.exe'
$buildDirectory = Join-Path $projectDir '.build'
New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
$temporaryOutput = Join-Path $buildDirectory ('GameNetworkMonitor-' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    $compilerArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output', '/main:GameNetworkMonitor.App', ('/out:' + $temporaryOutput), '/r:System.dll', '/r:System.Core.dll', '/r:System.Xaml.dll', ('/r:' + (Join-Path $frameworkDir 'WPF\WindowsBase.dll')), ('/r:' + (Join-Path $frameworkDir 'WPF\PresentationCore.dll')), ('/r:' + (Join-Path $frameworkDir 'WPF\PresentationFramework.dll')))
    & $compilerPath @compilerArgs @sourceFiles
    if ($LASTEXITCODE -ne 0) { throw "컴파일 실패: $LASTEXITCODE" }
    if ($UpdateExecutable) {
        # Replace only after compilation succeeds. A running/locked file stays intact on failure.
        if (Test-Path -LiteralPath $executablePath) { [System.IO.File]::Replace($temporaryOutput, $executablePath, [NullString]::Value) }
        else { [System.IO.File]::Move($temporaryOutput, $executablePath) }
        Write-Output $executablePath
    } else {
        Write-Output '컴파일 확인 완료. 사용 중인 GameNetworkMonitor.exe는 변경하지 않았습니다.'
    }
} finally {
    # Remove this build's exact temporary file only; never run it.
    if (Test-Path -LiteralPath $temporaryOutput) { Remove-Item -LiteralPath $temporaryOutput -Force }
}
