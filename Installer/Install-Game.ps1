param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Check', 'Install', 'Launch', 'Uninstall')]
    [string] $Action,
    [Parameter(Mandatory = $true)]
    [string] $PayloadPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$utf8 = [Text.UTF8Encoding]::new($false)
$status = [IO.StreamWriter]::new([Console]::OpenStandardOutput(), $utf8)
$status.AutoFlush = $true
$packageName = 'Hutch.SmashBanditsRacing'
$storeFamily = 'Hutch.SmashBanditsRacing_7jmw4s2ys8kej'
$modFamily = 'Hutch.SmashBanditsRacing_wv0sdxekm2bhy'
$modPublisher = 'CN=Smash Bandits Racing Community Patch'
$modTitle = 'Smash Bandits Racing (Community Patch)'
$storeVersion = [version]'1.1.1.10'
$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Smash Bandits Racing Community Patch'
$patchedFiles = @('Template.exe', 'UnityEngine.dll', 'Assembly-CSharp.dll')

function Write-Status([string] $Message) {
    $status.WriteLine($Message)
}

function Get-Package([string] $Family) {
    Get-AppxPackage -Name $packageName -PackageTypeFilter Main |
        Where-Object { $_.PackageFamilyName -eq $Family }
}

function Assert-NotLink([IO.FileSystemInfo] $Item) {
    if ($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "不支持包含符号链接或联接点的目录：$($Item.FullName)"
    }
}

function Assert-ManagedFolder([string] $Path) {
    if (!$Path -or [IO.Path]::GetDirectoryName($Path) -ne $installRoot -or
        [IO.Path]::GetFileName($Path) -notmatch '^game-[0-9a-f]{32}$') {
        throw '检测到同名应用注册在安装器管理范围之外。为保护现有文件，操作已停止。'
    }
    Assert-NotLink (Get-Item -LiteralPath $installRoot -Force)
    if (Test-Path -LiteralPath $Path) { Assert-NotLink (Get-Item -LiteralPath $Path -Force) }
}

function Get-ModPackage {
    $mod = Get-Package $modFamily
    if ($mod) {
        if (!$mod.IsDevelopmentMode -or $mod.Publisher -ne $modPublisher) {
            throw '检测到同名应用。为保护现有应用，操作已停止。'
        }
        Assert-ManagedFolder $mod.InstallLocation
        $mod
    }
}

function Get-StoreSource {
    $store = Get-Package $storeFamily
    if (!$store -or $store.IsDevelopmentMode) {
        throw '找不到 Microsoft Store 原版。请用当前 Windows 帐户安装原版游戏，再重试。'
    }
    if ($store.Version -ne $storeVersion -or $store.Architecture -ne 'X86') {
        throw "此补丁只支持商店版 $storeVersion（x86）。本机版本：$($store.Version)（$($store.Architecture)）。"
    }
    $framework = Get-AppxPackage -Name 'Microsoft.VCLibs.120.00' -PackageTypeFilter Framework |
        Where-Object { $_.PublisherId -eq '8wekyb3d8bbwe' -and $_.Architecture -eq 'X86' -and
            $_.Version -ge [version]'12.0.21005.1' }
    if (!$framework) {
        throw '缺少 Microsoft.VCLibs.120.00（x86）。请通过 Microsoft Store 修复原版游戏的安装。'
    }
    try {
        foreach ($name in $patchedFiles + 'AppxManifest.xml') {
            $stream = [IO.File]::OpenRead((Join-Path $store.InstallLocation $name))
            $stream.Dispose()
        }
    } catch {
        throw "Windows 未允许读取原版游戏，或文件不完整。安装器不会更改 WindowsApps 的权限。`n$($_.Exception.Message)"
    }
    $store
}

function Copy-GameTree([string] $Source, [string] $Destination, [string[]] $Excluded = @()) {
    [void][IO.Directory]::CreateDirectory($Destination)
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if ($item.Name -in $Excluded) { continue }
        Assert-NotLink $item
        $target = Join-Path $Destination $item.Name
        if ($item.PSIsContainer) {
            Copy-GameTree $item.FullName $target
        } else {
            # Copy the contents without copying Store permissions or read-only attributes.
            $inputFile = [IO.File]::OpenRead($item.FullName)
            try {
                $outputFile = [IO.File]::Open($target, [IO.FileMode]::CreateNew)
                try { $inputFile.CopyTo($outputFile) } finally { $outputFile.Dispose() }
            } finally {
                $inputFile.Dispose()
            }
        }
    }
}

function Set-ModManifest([string] $ManifestPath) {
    $manifest = [Xml.XmlDocument]::new()
    $manifest.XmlResolver = $null
    $manifest.Load($ManifestPath)
    $ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $ns.AddNamespace('appx', 'http://schemas.microsoft.com/appx/2010/manifest')
    $ns.AddNamespace('m2', 'http://schemas.microsoft.com/appx/2013/manifest')
    $identity = $manifest.SelectSingleNode('/appx:Package/appx:Identity', $ns)
    if ($identity.GetAttribute('Name') -ne $packageName -or $identity.GetAttribute('Version') -ne "$storeVersion" -or
        $identity.GetAttribute('Publisher') -ne 'CN=D0CFDD30-3D3F-48BE-A8BE-D13483951D60') {
        throw '原版的 Windows 应用清单不匹配，无法安装补丁。'
    }
    $identity.SetAttribute('Publisher', $modPublisher)
    $manifest.SelectSingleNode('/appx:Package/appx:Properties/appx:DisplayName', $ns).InnerText = $modTitle
    $manifest.SelectSingleNode('/appx:Package/appx:Properties/appx:PublisherDisplayName', $ns).InnerText = 'Unofficial community patch'
    $visuals = $manifest.SelectSingleNode('/appx:Package/appx:Applications/appx:Application[@Id="App"]/m2:VisualElements', $ns)
    $visuals.SetAttribute('DisplayName', $modTitle)
    $visuals.SelectSingleNode('m2:DefaultTile', $ns).SetAttribute('ShortName', 'Bandits Patch')
    $manifest.Save($ManifestPath)
}

function Invoke-PatchTool([string] $Name, [string[]] $ToolArguments) {
    $exe = Join-Path $PayloadPath $Name
    $arguments = ($ToolArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $start = [Diagnostics.ProcessStartInfo]::new($exe, $arguments)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $null = $stdout.GetAwaiter().GetResult()
        $details = $stderr.GetAwaiter().GetResult().Trim()
        if ($process.ExitCode -ne 0) {
            throw "$Name 无法应用补丁（退出代码 $($process.ExitCode)）。请确认原版未被修改，并尝试在商店中修复原版。`n$details"
        }
    } finally {
        $process.Dispose()
    }
}

function Remove-GameFolder([string] $Path) {
    Assert-ManagedFolder $Path
    if ([IO.Directory]::Exists($Path)) { [IO.Directory]::Delete($Path, $true) }
}

function Install-Mod {
    $store = Get-StoreSource
    $existing = Get-ModPackage
    if ($existing) {
        Write-Status '修改版已经安装。'
        return
    }

    [void][IO.Directory]::CreateDirectory($installRoot)
    $game = Join-Path $installRoot ('game-' + [guid]::NewGuid().ToString('N'))
    Assert-ManagedFolder $game
    $patchOutput = Join-Path $PayloadPath 'Patched'
    try {
        Write-Status '正在复制商店原版到独立目录，不会修改原版。'
        Copy-GameTree $store.InstallLocation $game @(
            'AppxBlockMap.xml', 'AppxSignature.p7x', 'AppxMetadata', 'microsoft.system.package.metadata'
        )

        Write-Status '正在应用画面居中、输入和车库补丁。'
        Invoke-PatchTool 'CenterGameViewport.exe' @($game, $patchOutput)
        Invoke-PatchTool 'PatchGarage.exe' @($game, (Join-Path $PayloadPath 'GarageViewport.dll'), $patchOutput)
        foreach ($name in $patchedFiles) {
            [IO.File]::Copy((Join-Path $patchOutput $name), (Join-Path $game $name), $true)
        }

        Write-Status '正在注册独立的 Windows 应用。'
        $manifest = Join-Path $game 'AppxManifest.xml'
        Set-ModManifest $manifest
        try {
            Add-AppxPackage -Register $manifest
        } catch {
            if ($_.Exception.Message -match '0x80073CFF|0x80073D01') {
                Write-Status '如果 Windows 策略不允许注册此应用，请检查「设置 → 开发者选项」。安装器不会更改系统设置。'
            }
            throw
        }
        $registered = Get-ModPackage
        if (!$registered -or $registered.InstallLocation -ne $game) {
            throw 'Windows 未确认修改版注册成功。'
        }
        Write-Status '安装完成。可以在开始菜单中找到 Smash Bandits Racing (Community Patch)。'
    } catch {
        # Keep the copy if Windows registered it before reporting an error.
        try {
            $registered = Get-Package $modFamily
            if (!$registered -or $registered.InstallLocation -ne $game) { Remove-GameFolder $game }
        } catch {
            Write-Status "无法清理未完成的安装：$($_.Exception.Message)"
        }
        throw
    }
}

$mutex = [Threading.Mutex]::new($false, 'Local\SmashBanditsRacingCommunityPatchInstaller')
$locked = $false
try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw '另一个补丁安装器正在运行，请稍后重试。' }
    switch ($Action) {
        'Check' {
            $store = Get-StoreSource
            Write-Status "已检测到商店原版 $($store.Version)（x86）。"
            if (Get-ModPackage) { Write-Status '修改版已经安装。' } else { Write-Status '修改版尚未安装。' }
        }
        'Install' { Install-Mod }
        'Launch' {
            $mod = Get-ModPackage
            if (!$mod -or ![IO.File]::Exists((Join-Path $mod.InstallLocation 'Template.exe'))) {
                throw '找不到可启动的修改版。请先安装；如果文件已损坏，请先卸载再重新安装。'
            }
            Write-Status "APPID=$modFamily!App"
        }
        'Uninstall' {
            $mod = Get-ModPackage
            if (!$mod) {
                Write-Status '修改版尚未安装。'
            } else {
                Remove-AppxPackage -Package $mod.PackageFullName
                if (Get-Package $modFamily) { throw 'Windows 未确认修改版已卸载。' }
                Remove-GameFolder $mod.InstallLocation
                Write-Status '已卸载修改版；商店原版未更改。'
            }
        }
    }
    exit 0
} catch {
    $errors = [IO.StreamWriter]::new([Console]::OpenStandardError(), $utf8)
    try { $errors.WriteLine($_.Exception.Message) } finally { $errors.Dispose() }
    exit 1
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
    $status.Dispose()
}
