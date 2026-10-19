param(
    [Parameter(Mandatory)][string]$Data,
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$OutputDir,
    [string]$FFmpeg = '',
    [ValidateSet('libx264','h264_nvenc')][string]$Encoder = 'libx264',
    [ValidateRange(0,3599)][int]$StartFrame = 0,
    [ValidateRange(1,3600)][int]$RenderFrames = 3600,
    [string]$Frames = '',
    [switch]$VideoOnly,
    [string]$Audio = ''
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -lt 7) {
    throw 'The cinematic renderer requires Windows and PowerShell 7.'
}
if (-not $Frames) {
    if (-not $FFmpeg) { $FFmpeg = $env:FFMPEG_BIN }
    if (-not $FFmpeg) {
        $candidate = Get-Command ffmpeg -CommandType Application -ErrorAction SilentlyContinue
        if ($candidate) { $FFmpeg = $candidate.Source }
    }
    if (-not $FFmpeg) { throw 'Install FFmpeg or pass -FFmpeg (or set FFMPEG_BIN).' }
    $resolved = Get-Command $FFmpeg -CommandType Application -ErrorAction SilentlyContinue
    if (-not $resolved) { throw "FFmpeg executable not found: $FFmpeg" }
    $FFmpeg = $resolved.Source
    # An advertised encoder may still fail when a compatible driver or GPU is absent.
    if ($Encoder -eq 'h264_nvenc') {
        $probeArgs = @('-hide_banner','-loglevel','error','-f','lavfi','-i',
            'color=c=black:s=320x240:r=30','-frames:v','1','-c:v','h264_nvenc','-f','null','-')
        $probeLog = & $FFmpeg @probeArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "NVENC cannot encode on this machine. Use -Encoder libx264. $probeLog"
        }
    }
    if (-not $VideoOnly -and (-not $Audio -or -not (Test-Path -LiteralPath $Audio -PathType Leaf))) {
        throw 'Pass an existing -Audio file, or use -VideoOnly for silent video chunks.'
    }
}
Add-Type -ReferencedAssemblies @(
    'System.Drawing.Common','System.Drawing.Primitives','System.Text.Json',
    'System.Runtime','System.Linq','System.Collections','System.Console','System.Memory',
    'System.Runtime.InteropServices','System.Diagnostics.Process','System.ComponentModel.Primitives',
    'System.Threading','System.Threading.Tasks','System.Text.Encoding.Extensions',
    'System.Private.Windows.GdiPlus','System.Private.Windows.Core'
) -TypeDefinition (Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $PSScriptRoot 'cinematic_renderer.cs'))
$audioPath = if ($Audio) { [IO.Path]::GetFullPath($Audio) } else { '' }
[ReusableCinema]::Run([IO.Path]::GetFullPath($Data),[IO.Path]::GetFullPath($Project),
    [IO.Path]::GetFullPath($OutputDir),$FFmpeg,$audioPath,$Frames,$RenderFrames,
    $StartFrame,[bool]$VideoOnly,$Encoder)
