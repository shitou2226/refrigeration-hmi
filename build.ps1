$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish .\RefrigerationHmi.csproj -c Release -r win-x64 --self-contained false -o .\dist
    if ($LASTEXITCODE -ne 0) { throw '发布失败，请检查以上输出。' }
    Write-Host '已生成 dist\RefrigerationHmi.exe，启动时保留整个 dist 文件夹。'
} finally { Pop-Location }
