param(
    [Parameter(Mandatory)][string]$Data,
    [Parameter(Mandatory)][string]$Project,
    [string]$OutputDirectory = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
Add-Type -ReferencedAssemblies @(
    'System.Drawing.Common', 'System.Drawing.Primitives', 'System.Text.Json', 'System.Text.Encodings.Web',
    'System.Runtime', 'System.Linq', 'System.Collections', 'System.Console',
    'System.Memory', 'System.Runtime.InteropServices', 'System.Security.Cryptography',
    'System.Text.Encoding.Extensions', 'System.Private.Windows.GdiPlus',
    'System.Private.Windows.Core'
) -TypeDefinition (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'cover_renderer.cs') -Raw -Encoding utf8)
[KospiPublishCovers]::Render([IO.Path]::GetFullPath($Data), [IO.Path]::GetFullPath($OutputDirectory), [IO.Path]::GetFullPath($Project))
