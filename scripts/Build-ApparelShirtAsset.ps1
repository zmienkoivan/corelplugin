[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BlenderPath,
    [string]$Python = 'python',
    [string]$StagingDirectory = (Join-Path $PSScriptRoot '..\release\apparel-shirt-build')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$blender = (Resolve-Path -LiteralPath $BlenderPath).Path
$staging = [IO.Path]::GetFullPath($StagingDirectory)
$scripts = Join-Path $PSScriptRoot 'apparel3d'
& $Python (Join-Path $scripts 'Prepare-Shirt.py') $staging
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $blender --background --factory-startup --python (Join-Path $scripts 'Bake-Shirt.py') -- $staging
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $Python (Join-Path $scripts 'Finish-Shirt.py') $staging (Join-Path $repo 'native\VanyaTools.Native\Assets\Apparel')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
