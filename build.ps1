[CmdletBinding()]
param([switch]$UpdateExecutable)
$ErrorActionPreference = 'Stop'
$projectDir = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')

function Assert-UnlinkedBuildPath([string]$Path) {
    $ancestor = [IO.Path]::GetFullPath($Path)
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw '빌드 경로에 연결된 파일이나 폴더를 사용할 수 없습니다.'
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
}
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkDir 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework C# 컴파일러를 찾지 못했습니다.' }
$sourceFiles = Get-ChildItem -LiteralPath $projectDir -Filter '*.cs' -File | ForEach-Object { $_.FullName }
$executablePath = Join-Path $projectDir 'PcInternetMonitor.exe'
$iconPath = Join-Path $projectDir 'assets\app-icon.ico'
if (-not (Test-Path -LiteralPath $iconPath)) { throw '앱 아이콘 파일을 찾지 못했습니다.' }
$licensePath = Join-Path $projectDir 'LICENSE'
if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) { throw 'MIT 라이선스 파일을 찾지 못했습니다.' }
$buildDirectory = Join-Path $projectDir '.build'
$temporaryOutput = Join-Path $buildDirectory ('PcInternetMonitor-' + [Guid]::NewGuid().ToString('N') + '.exe')
$createdBuildDirectory = $false
$ownsTemporaryOutput = $false
if (-not [IO.Path]::GetDirectoryName($buildDirectory).Equals($projectDir, [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Path]::GetDirectoryName($temporaryOutput).Equals($buildDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($temporaryOutput) -notmatch '^PcInternetMonitor-[a-f0-9]{32}\.exe$') {
    throw '빌드 임시 경로가 프로젝트 범위를 벗어났습니다.'
}
try {
    Assert-UnlinkedBuildPath $temporaryOutput
    if ($UpdateExecutable) { Assert-UnlinkedBuildPath $executablePath }
    if (-not (Test-Path -LiteralPath $buildDirectory)) {
        try {
            # Without -Force, only the process that creates this directory owns its cleanup.
            New-Item -ItemType Directory -Path $buildDirectory -ErrorAction Stop | Out-Null
            $createdBuildDirectory = $true
        } catch {
            # A concurrent build can create the directory between the check and New-Item.
            if (-not (Test-Path -LiteralPath $buildDirectory -PathType Container)) { throw }
        }
    }
    Assert-UnlinkedBuildPath $temporaryOutput
    if (Test-Path -LiteralPath $temporaryOutput) { throw '빌드 임시 파일이 이미 존재합니다.' }
    $ownsTemporaryOutput = $true
    $compilerArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output', '/main:PcInternetMonitor.App', ('/out:' + $temporaryOutput), '/r:System.dll', '/r:System.Core.dll', '/r:System.Xaml.dll', ('/r:' + (Join-Path $frameworkDir 'WPF\WindowsBase.dll')), ('/r:' + (Join-Path $frameworkDir 'WPF\PresentationCore.dll')), ('/r:' + (Join-Path $frameworkDir 'WPF\PresentationFramework.dll')))
    $compilerArgs += @(('/win32icon:' + $iconPath), ('/resource:' + $iconPath + ',PcInternetMonitor.AppIcon.ico'))
    $compilerArgs += ('/resource:' + $licensePath + ',PcInternetMonitor.LICENSE.txt')
    & $compilerPath @compilerArgs @sourceFiles
    if ($LASTEXITCODE -ne 0) { throw "컴파일 실패: $LASTEXITCODE" }
    if ($UpdateExecutable) {
        # Replace only after compilation succeeds. A running/locked file stays intact on failure.
        if (Test-Path -LiteralPath $executablePath) { [System.IO.File]::Replace($temporaryOutput, $executablePath, [NullString]::Value) }
        else { [System.IO.File]::Move($temporaryOutput, $executablePath) }
        Write-Output $executablePath
    } else {
        Write-Output '컴파일 확인 완료. 사용 중인 PcInternetMonitor.exe는 변경하지 않았습니다.'
    }
} finally {
    # Remove this build's exact temporary file only; never run it.
    try {
        Assert-UnlinkedBuildPath $temporaryOutput
        if ($ownsTemporaryOutput -and (Test-Path -LiteralPath $temporaryOutput -PathType Leaf)) {
            Remove-Item -LiteralPath $temporaryOutput -Force
        }
    } catch { Write-Warning ('빌드 임시 파일을 정리하지 못했습니다: ' + $_.Exception.Message) }
    if ($createdBuildDirectory) {
        try {
            Assert-UnlinkedBuildPath $buildDirectory
            if (Test-Path -LiteralPath $buildDirectory -PathType Container) {
                # Nonrecursive deletion is atomic: another build's files keep the directory alive.
                [IO.Directory]::Delete($buildDirectory, $false)
            }
        } catch [IO.IOException] {
            # A concurrent build or another file owns the nonempty directory; preserve it.
        } catch { Write-Warning ('빌드 임시 폴더를 정리하지 못했습니다: ' + $_.Exception.Message) }
    }
}
