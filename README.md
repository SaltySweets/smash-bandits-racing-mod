# Smash Bandits Racing 本地修改项目

基于 Microsoft Store 版 `1.1.1.10` 的本地修改工作区。项目进度见 [开发日志](CHANGELOG.md) 和 Git 提交记录。

## 玩家安装（测试版）

1. 先用当前 Windows 帐户安装 Microsoft Store 原版 `1.1.1.10`（x86）。
2. 双击 `Smash Bandits Racing Patch Setup.exe`，点击「安装修改版」。
3. 点击「启动修改版」，或从开始菜单打开 **Smash Bandits Racing (Community Patch)**。

安装器从本机原版复制所需文件，再应用居中、输入和车库补丁，不自带原版游戏或素材。商店原版与修改版分开安装，存档也独立，不会自动迁移。卸载请使用安装器中的「卸载修改版」；这会移除修改版及其本地存档，不影响原版。

Windows 必须允许注册本地开发应用。若 Windows 拒绝注册，请在「设置」中搜索「开发者模式」，并仅在设备策略允许、你愿意开启时启用。安装器不会改系统设置或 `WindowsApps` 权限。如果系统不允许读取原版文件，安装器会停止。测试版尚未签名，只从项目的正式发行渠道获取，不要关闭 Windows 安全保护。

### 构建安装器（维护者）

准备本地未修改的商店版 `1.1.1.10`（默认在 `Game`）和 [Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6) 的 `lib/net40/Mono.Cecil.dll`。在项目目录的 Windows PowerShell 中运行：

```powershell
.\Installer\Build-Installer.ps1 -CecilDll 'C:\path\to\Mono.Cecil.dll'
```

如需指定原版目录，可加 `-GameDir`。`dist/` 会生成一个安装器及其 `.sha256` 文件；发行时只上传这两个文件，不上传 `Game`、`Editable Game` 或运行库。公开发布前，请在只有商店原版的 Windows 设备上用普通用户测试安装、进游戏、车库和卸载。

## 本地开发副本

在已完成 Windows 应用注册的工作区中，双击 `Start Smash Bandits Racing.exe` 启动本地副本。启动器源码在 `Launcher.cs`。

`Editable Game/Data/` 存放 Unity 游戏资源，`Editable Game/Assembly-CSharp.dll` 包含编译后的游戏逻辑。商店安装文件和运行库由本机提供。

`CenterGameViewport.cs` 和 `ViewportPointerPatch.cs` 使用 Mono.Cecil 0.11.6 为商店版 `1.1.1.10` 生成画面与输入补丁。补丁器接收原版的 `Game` 目录和补丁输出目录。

将生成的 `Template.exe` 和 `UnityEngine.dll` 一起放入 `Editable Game`，使文件继承该目录的访问权限。然后使用该目录中的 `AppxManifest.xml` 重新注册本地应用。

## 车库界面补丁

在项目目录中运行以下 PowerShell 命令。先将 Mono.Cecil 0.11.6 的 `lib/net40/Mono.Cecil.dll` 放入指定的构建目录。

```powershell
$buildDir = 'C:\path\to\garage-build'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runtime = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll'
$cecil = Join-Path $buildDir 'Mono.Cecil.dll'
$viewport = Join-Path $buildDir 'GarageViewport.dll'
$patcher = Join-Path $buildDir 'PatchGarage.exe'
$outputDir = Join-Path $buildDir 'patched'

& $csc /nologo /optimize+ /target:library "/out:$viewport" "/reference:$runtime" `
    '/reference:Game\Assembly-CSharp.dll' '/reference:Game\UnityEngine.dll' GarageViewport.cs
if ($LASTEXITCODE -ne 0) { throw '车库界面代码编译失败。' }
& $csc /nologo /optimize+ "/out:$patcher" "/reference:$cecil" PatchGarage.cs
if ($LASTEXITCODE -ne 0) { throw '车库补丁器编译失败。' }
& $patcher Game $viewport $outputDir
```

关闭本地游戏后，将补丁器输出的 `Assembly-CSharp.dll` 放入 `Editable Game`，使文件继承该目录的访问权限，再用其中的 `AppxManifest.xml` 重新注册。
