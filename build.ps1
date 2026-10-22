# 一键发布：生成 dist\ZERO.exe（单文件，自带 .NET 运行时）
# 用法：在仓库根目录运行  powershell -ExecutionPolicy Bypass -File .\build.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet publish .\src\ZERO\ZERO.csproj -p:PublishProfile=win-x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$exe = Join-Path $PSScriptRoot 'dist\ZERO.exe'
Write-Host "`n发布完成：$exe（$([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB）" -ForegroundColor Green
