[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CecilDll,

    [string] $GameDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Game')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$runtime = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll'
$cecil = (Resolve-Path -LiteralPath $CecilDll).ProviderPath
$game = (Resolve-Path -LiteralPath $GameDir).ProviderPath
$cecilName = [Reflection.AssemblyName]::GetAssemblyName($cecil)
if ($cecilName.Name -ne 'Mono.Cecil' -or $cecilName.Version -ne [version]'0.11.6.0') {
    throw 'Use lib/net40/Mono.Cecil.dll from the Mono.Cecil 0.11.6 NuGet package.'
}

$originals = @{
    'Template.exe' = '2B42848BD6A8119A3FF26F80B9534D0D09115BADE521FCAE1515400751560BA0'
    'UnityEngine.dll' = '60E50F5336A56A6388294C23A2588A1A4D397BF5900B624CD50803C62FF66F56'
    'Assembly-CSharp.dll' = '72E4E56D8C167FBCBBD7653CA954BAAC19737A2ECCB583F2EBBE943FCEA2ED74'
}
foreach ($name in $originals.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $game $name)).Hash -ne $originals[$name]) {
        throw "$name must come from the unmodified Microsoft Store release 1.1.1.10."
    }
}

function Invoke-Compiler {
    & $csc /nologo /optimize+ /codepage:65001 @args
    if ($LASTEXITCODE -ne 0) { throw "C# compilation failed ($LASTEXITCODE)." }
}

$build = Join-Path ([IO.Path]::GetTempPath()) ('sbr-build-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($build)
try {
    $viewportTool = Join-Path $build 'CenterGameViewport.exe'
    $garageHelper = Join-Path $build 'GarageViewport.dll'
    $garageTool = Join-Path $build 'PatchGarage.exe'
    $installer = Join-Path $build 'Smash Bandits Racing Patch Setup.exe'

    Invoke-Compiler "/out:$viewportTool" "/reference:$cecil" `
        (Join-Path $repo 'CenterGameViewport.cs') (Join-Path $repo 'ViewportPointerPatch.cs')
    Invoke-Compiler /target:library "/out:$garageHelper" "/reference:$runtime" `
        "/reference:$(Join-Path $game 'Assembly-CSharp.dll')" "/reference:$(Join-Path $game 'UnityEngine.dll')" `
        (Join-Path $repo 'GarageViewport.cs')
    Invoke-Compiler "/out:$garageTool" "/reference:$cecil" (Join-Path $repo 'PatchGarage.cs')

    Invoke-Compiler /target:winexe /main:PatchInstaller "/out:$installer" `
        /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
        "/resource:$(Join-Path $PSScriptRoot 'Install-Game.ps1'),Install-Game.ps1" "/resource:$viewportTool,CenterGameViewport.exe" `
        "/resource:$garageHelper,GarageViewport.dll" "/resource:$garageTool,PatchGarage.exe" `
        "/resource:$cecil,Mono.Cecil.dll" `
        "/resource:$(Join-Path $PSScriptRoot 'ThirdPartyNotices.txt'),ThirdPartyNotices.txt" `
        (Join-Path $PSScriptRoot 'PatchInstaller.cs') (Join-Path $repo 'Launcher.cs')

    $dist = Join-Path $repo 'dist'
    [void][IO.Directory]::CreateDirectory($dist)
    $output = Join-Path $dist ([IO.Path]::GetFileName($installer))
    [IO.File]::Copy($installer, $output, $true)
    $hash = (Get-FileHash -LiteralPath $output).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$output.sha256", "$hash  $([IO.Path]::GetFileName($output))`r`n")
    Write-Output $output
    Write-Output "SHA-256: $hash"
} finally {
    [IO.Directory]::Delete($build, $true)
}
