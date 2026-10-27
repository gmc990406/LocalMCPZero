# ZERO 构建脚本：Test（调试产物）与 Release（单文件正式发布）
# 用法（在仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 -Mode Test
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 -Mode Release
#
# Test：Debug 配置 → %TEMP%\zbuild，中间文件 %TEMP%\zobj\
# Release：按 win-x64.pubxml 单文件自包含发布 → release\v<Version>\，中间文件 %TEMP%\zobj-release\
# 两种模式均不写入 src\ZERO\bin 与默认 obj；Release 编译输出到 %TEMP%\zout-release\；构建期间临时挪走 src\ZERO\obj，避免与 BaseIntermediateOutputPath
# 下的生成文件重复（WPF *.g.cs / AssemblyInfo），结束后原样恢复，不影响正在运行的 Debug 实例。
# restore / build / publish 共用同一 BaseIntermediateOutputPath。
param(
    [ValidateSet('Test', 'Release')]
    [string]$Mode = 'Test'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$proj = Join-Path $PSScriptRoot 'src\ZERO\ZERO.csproj'
$projDir = Join-Path $PSScriptRoot 'src\ZERO'
$projObj = Join-Path $projDir 'obj'
if (-not (Test-Path -LiteralPath $proj)) {
    Write-Error "找不到项目：$proj"
    exit 1
}

function Ensure-TrailingSlash([string]$path) {
    if ($path.EndsWith('\') -or $path.EndsWith('/')) { return $path }
    return $path + '\'
}

function Get-ProjectVersion([string]$csproj) {
    $text = [IO.File]::ReadAllText($csproj)
    $m = [regex]::Match($text, '<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw "无法从 $csproj 读取 <Version>" }
    return $m.Groups[1].Value.Trim()
}

# 暂存项目 obj，避免与重定向中间目录的生成源重复编译
$stashObj = $null
if (Test-Path -LiteralPath $projObj) {
    $stashObj = Join-Path $env:TEMP ("zero-obj-stash-{0}" -f [guid]::NewGuid().ToString('N'))
    Write-Host "临时挪走项目 obj → $stashObj" -ForegroundColor DarkYellow
    Move-Item -LiteralPath $projObj -Destination $stashObj
}

try {
    if ($Mode -eq 'Test') {
        $outDir = Join-Path $env:TEMP 'zbuild'
        $objDir = Ensure-TrailingSlash (Join-Path $env:TEMP 'zobj')
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
        New-Item -ItemType Directory -Force -Path $objDir | Out-Null

        Write-Host "[Test] Debug → $outDir" -ForegroundColor Cyan
        Write-Host "[Test] 中间文件 → $objDir" -ForegroundColor Cyan

        dotnet restore $proj -p:BaseIntermediateOutputPath=$objDir
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        dotnet build $proj -c Debug -o $outDir -p:BaseIntermediateOutputPath=$objDir --no-restore
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        $exe = Join-Path $outDir 'ZERO.exe'
        if (-not (Test-Path -LiteralPath $exe)) { Write-Error "未找到产物：$exe"; exit 1 }
        $mb = [math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)
        Write-Host "`nTest 完成：$exe（$mb MB）" -ForegroundColor Green
        exit 0
    }

    # Release
    $version = Get-ProjectVersion $proj
    $outDir = Join-Path $PSScriptRoot "release\v$version"
    $objDir = Ensure-TrailingSlash (Join-Path $env:TEMP 'zobj-release')
    $publishDir = Ensure-TrailingSlash $outDir
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    New-Item -ItemType Directory -Force -Path $objDir | Out-Null

    Write-Host "[Release] v$version 单文件发布 → $outDir" -ForegroundColor Cyan
    Write-Host "[Release] 中间文件 → $objDir" -ForegroundColor Cyan

    dotnet restore $proj -r win-x64 -p:BaseIntermediateOutputPath=$objDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $buildOut = Ensure-TrailingSlash (Join-Path $env:TEMP 'zout-release')
    New-Item -ItemType Directory -Force -Path $buildOut | Out-Null
    Write-Host "[Release] 编译输出 → $buildOut" -ForegroundColor Cyan

    dotnet publish $proj `
        -c Release `
        -p:PublishProfile=win-x64 `
        -p:PublishDir=$publishDir `
        -p:OutputPath=$buildOut `
        -p:BaseIntermediateOutputPath=$objDir `
        --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $exe = Join-Path $outDir 'ZERO.exe'
    if (-not (Test-Path -LiteralPath $exe)) { Write-Error "未找到产物：$exe"; exit 1 }
    $mb = [math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)
    Write-Host "`n发布完成：$exe（$mb MB）" -ForegroundColor Green
}
finally {
    # 恢复项目 obj（即使构建失败也要恢复，避免影响 VS / 运行中实例）
    if ($stashObj -and (Test-Path -LiteralPath $stashObj)) {
        if (Test-Path -LiteralPath $projObj) {
            Remove-Item -LiteralPath $projObj -Recurse -Force -ErrorAction SilentlyContinue
        }
        Move-Item -LiteralPath $stashObj -Destination $projObj -Force
        Write-Host "已恢复项目 obj" -ForegroundColor DarkYellow
    }
}
