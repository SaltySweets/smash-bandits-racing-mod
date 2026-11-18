# Smash Bandits Racing 本地修改项目

基于 Microsoft Store 版 `1.1.1.10` 的本地修改工作区。项目进度见 [开发日志](CHANGELOG.md) 和 Git 提交记录。

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
