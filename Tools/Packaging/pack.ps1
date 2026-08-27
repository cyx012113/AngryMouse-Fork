<#
.SYNOPSIS
    AngryMouse 多目标 .NET 打包脚本
.DESCRIPTION
    对每个目标框架（net472 / net6.0-windows / net8.0-windows / net10.0-windows）执行
    `dotnet publish`，并把发布目录压缩为 dist/AngryMouse-2.12.1-<tfm>.zip。
.PARAMETER SelfContained
    可选。加此开关则发布为自包含（win-x64）版本，无需目标机器安装 .NET 运行时。
.PARAMETER Configuration
    构建配置，默认 Release。
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools/Packaging/pack.ps1
    powershell -ExecutionPolicy Bypass -File Tools/Packaging/pack.ps1 -SelfContained
#>

[CmdletBinding()]
param(
    [switch] $SelfContained,
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$Project  = Join-Path $RepoRoot "AngryMouse\AngryMouse.csproj"
$DistRoot = Join-Path $RepoRoot "dist"
$Version  = "2.12.1"

$TargetFrameworks = @("net472", "net6.0-windows", "net8.0-windows", "net10.0-windows")

if (-not (Test-Path $Project)) {
    throw "找不到项目文件: $Project"
}

if (Test-Path $DistRoot) {
    Write-Host "清理旧 dist 目录: $DistRoot"
    Remove-Item $DistRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $DistRoot | Out-Null

# 随包附带的简短发布说明
$ReleaseNotes = @"
AngryMouse $Version (Fork) - 多 .NET 版本发布包
================================================
本包由 Tools/Packaging/pack.ps1 生成。

运行方式：
  - 解压后直接运行 AngryMouse.exe
  - 首次运行会复制内置光标套建（Adwaita / Xfce4 / DMZ / Nord）到
    %APPDATA%\AngryMouse\CursorCollections\

运行环境要求：
  - net472           : 需系统已安装 .NET Framework 4.7.2 或更高
  - net6/net8/net10  : 需安装对应 .NET 运行时
                       （使用 -SelfContained 发布的版本免运行时，但体积更大）

智能热点识别（自动识别焦点按钮）：
  优先调用同目录 HotspotDetector\hotspot_detector.py（需要 Python + numpy + Pillow），
  缺失 Python 时自动回退内置 C# 启发式，功能不受影响。

许可证：MIT（与上游 Jamir-boop/AngryMouse 一致）
"@

foreach ($tfm in $TargetFrameworks) {
    $label = $tfm -replace "0-windows", "0"
    $publishDir = Join-Path $DistRoot $tfm
    $zipName    = "AngryMouse-$Version-$label.zip"
    $zipPath    = Join-Path $DistRoot $zipName

    Write-Host ""
    Write-Host "==> 发布 $tfm => $publishDir"

    $publishArgs = @("publish", $Project, "-c", $Configuration, "-f", $tfm, "-o", $publishDir, "--nologo")
    if ($SelfContained) {
        $publishArgs += @("-r", "win-x64", "--self-contained", "true")
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败 (tfm=$tfm, exit=$LASTEXITCODE)"
    }

    # 写入发布说明
    Set-Content -Path (Join-Path $publishDir "发布说明.txt") -Value $ReleaseNotes -Encoding UTF8

    # 压缩
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "==> 压缩为 $zipName"
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
}

Write-Host ""
Write-Host "完成。产物位于: $DistRoot"
Get-ChildItem $DistRoot -Filter "*.zip" | ForEach-Object { Write-Host ("  {0,-34} {1,9} KB" -f $_.Name, [math]::Round($_.Length / 1KB, 1)) }
