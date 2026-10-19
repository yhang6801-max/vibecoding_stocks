param(
    [Parameter(Mandatory)][string]$Data,
    [string]$Output = (Join-Path $PSScriptRoot 'output/classic_video.mp4'),
    [string]$FrameList = '',
    [string]$SampleDir = (Join-Path $PSScriptRoot 'output/classic_samples'),
    [string]$CoverOutput = '',
    [switch]$CoverOnly,
    [ValidateRange(0.1, 1.0)][double]$ContentScale = 1.0,
    [ValidateRange(0, 6599)][int]$StartFrame = 0,
    [Alias('SmokeFrames')][ValidateRange(1, 6600)][int]$RenderFrames = 6600,
    [string]$FFmpeg = '',
    [ValidateSet('libx264', 'h264_nvenc')][string]$Encoder = 'libx264'
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -lt 7) { throw 'The classic renderer requires Windows and PowerShell 7.' }

if ($CoverOnly -and -not $CoverOutput) { throw '-CoverOnly requires -CoverOutput.' }

if ($StartFrame + $RenderFrames -gt 6600) { throw 'Requested frame range exceeds the classic220 preset.' }
if (-not $FrameList -and -not $CoverOnly) {
    if (-not $FFmpeg) { $FFmpeg = $env:FFMPEG_BIN }
    if (-not $FFmpeg) {
        $command = Get-Command ffmpeg -ErrorAction SilentlyContinue
        if ($command) { $FFmpeg = $command.Source }
    }
    if (-not $FFmpeg) { throw 'FFmpeg not found; pass -FFmpeg, set FFMPEG_BIN, or add it to PATH.' }
    if (-not (Test-Path -LiteralPath $FFmpeg -PathType Leaf)) {
        $command = Get-Command $FFmpeg -ErrorAction SilentlyContinue
        if ($command) { $FFmpeg = $command.Source }
        else { throw "FFmpeg executable was not found: $FFmpeg" }
    }
}
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

$source = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $PSScriptRoot 'classic_renderer.cs')

Add-Type -ReferencedAssemblies @(
    'System.Drawing.Common',
    'System.Drawing.Primitives',
    'System.Text.Json',
    'System.Runtime',
    'System.Linq',
    'System.Collections',
    'System.Console',
    'System.Memory',
    'System.Diagnostics.Process',
    'System.ComponentModel.Primitives',
    'System.Threading',
    'System.Text.Encoding.Extensions',
    'System.Private.Windows.GdiPlus',
    'System.Private.Windows.Core'
) -TypeDefinition $source

$dataPath = (Resolve-Path -LiteralPath $Data).Path
$ffmpegPath = if ($FrameList -or $CoverOnly) { '' } else { (Resolve-Path -LiteralPath $FFmpeg).Path }
[ReusableClassicRenderer]::Run($dataPath, [System.IO.Path]::GetFullPath($Output), $FrameList, $SampleDir, $CoverOutput, [bool]$CoverOnly, $StartFrame, $RenderFrames, $ffmpegPath, $Encoder, $ContentScale)
