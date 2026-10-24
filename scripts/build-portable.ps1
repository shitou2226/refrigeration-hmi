#Requires -Version 7.4
param([string]$Version = '0.1.0-preview.1')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?$') { throw 'Invalid version.' }
$TaskRoot = Split-Path -Parent $PSScriptRoot
$TaskRelease = Join-Path $TaskRoot ('artifacts/releases/' + $Version + '/' + (Get-Date -Format 'yyyyMMdd-HHmmssfff'))
$TaskStage = Join-Path $TaskRelease "RefrigerationHmi-$Version-win-x64"
$TaskBuild = Join-Path $TaskRelease 'build'
$TaskZip = Join-Path $TaskRelease "RefrigerationHmi-$Version-win-x64.zip"
$TaskExtract = Join-Path $TaskRelease 'verified-extraction'
New-Item -ItemType Directory -Path $TaskRelease -Force | Out-Null
Push-Location $TaskRoot
try {
    & dotnet restore RefrigerationHmi.csproj -r win-x64 --locked-mode --artifacts-path $TaskBuild
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & dotnet publish RefrigerationHmi.csproj -c Release -r win-x64 --self-contained true --no-restore --artifacts-path $TaskBuild --output $TaskStage "-p:Version=$Version" "-p:PathMap=$TaskRoot=/_/RefrigerationHmi" -p:DebugType=None -p:DebugSymbols=false -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
    Copy-Item -LiteralPath (Join-Path $TaskRoot 'LICENSE'), (Join-Path $TaskRoot 'THIRD_PARTY_NOTICES.md') -Destination $TaskStage
    Copy-Item -LiteralPath (Join-Path $TaskRoot 'docs') -Destination $TaskStage -Recurse
    @'
Refrigeration HMI - Windows x64 portable preview

Extract the complete ZIP and run RefrigerationHmi.exe.
The .NET Desktop Runtime is included. No SDK, account or API key is needed.
The app starts with simulated readings and creates its own AppData folder.
For a TCP demonstration, start the built-in TCP simulator and select local Modbus TCP.
See docs/demo.md and docs/quick-start.md for Chinese instructions.
This release is unsigned. No real PLC or refrigeration equipment was validated.
'@ | Set-Content -LiteralPath (Join-Path $TaskStage 'START-HERE.txt') -Encoding utf8
    $TaskAssets = Get-Content -LiteralPath (Join-Path $TaskBuild 'obj/RefrigerationHmi/project.assets.json') -Raw | ConvertFrom-Json
    $TaskCache = $TaskAssets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
    $TaskLicenses = Join-Path $TaskStage 'licenses'; New-Item -ItemType Directory -Path $TaskLicenses -Force | Out-Null
    foreach ($TaskLibrary in $TaskAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
        $TaskDirectory = Join-Path $TaskCache $TaskLibrary.Value.path
        $TaskFiles = @(Get-ChildItem -LiteralPath $TaskDirectory -File | Where-Object { $_.Name -match '^(licen[cs]e|third.?party)' })
        if (-not ($TaskFiles | Where-Object { $_.Name -match '^licen[cs]e' })) {
            $TaskFallback = if ($TaskLibrary.Name -like 'Microsoft.Data.Sqlite*') { Join-Path $TaskRoot 'docs/licenses/Microsoft.Data.Sqlite-MIT.txt' } elseif ($TaskLibrary.Name -like 'SQLitePCLRaw.*') { Join-Path $TaskRoot 'docs/licenses/SQLitePCLRaw-Apache-2.0.txt' } else { $null }
            if (-not $TaskFallback -or -not (Test-Path -LiteralPath $TaskFallback)) { throw "Missing dependency license: $($TaskLibrary.Name)" }
            $TaskFiles = @($TaskFiles) + @(Get-Item -LiteralPath $TaskFallback)
        }
        $TaskTarget = Join-Path $TaskLicenses $TaskLibrary.Name.Replace('/', '-'); New-Item -ItemType Directory -Path $TaskTarget -Force | Out-Null
        $TaskFiles | Copy-Item -Destination $TaskTarget
    }
    $TaskRuntime = Get-Content -LiteralPath (Join-Path $TaskStage 'RefrigerationHmi.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach ($TaskFramework in $TaskRuntime.runtimeOptions.includedFrameworks) {
        $TaskDirectory = Join-Path $TaskCache ($TaskFramework.name.ToLowerInvariant() + '.runtime.win-x64/' + $TaskFramework.version)
        $TaskFiles = @(Get-ChildItem -LiteralPath $TaskDirectory -File | Where-Object { $_.Name -match '^(licen[cs]e|third.?party)' })
        if (-not ($TaskFiles | Where-Object { $_.Name -match '^licen[cs]e' })) { throw "Missing runtime license: $($TaskFramework.name)" }
        $TaskTarget = Join-Path $TaskLicenses ($TaskFramework.name + '-' + $TaskFramework.version); New-Item -ItemType Directory -Path $TaskTarget -Force | Out-Null
        $TaskFiles | Copy-Item -Destination $TaskTarget
    }
    $TaskUnexpected = @(Get-ChildItem -LiteralPath $TaskStage -Recurse -File | Where-Object { $_.Name -match '^\.env(?:\.|$)|\.(pdb|pfx|p12|pem|key|db|sqlite|log)$|^self-test-results\.txt$' })
    if ($TaskUnexpected.Count -gt 0 -or (Test-Path -LiteralPath (Join-Path $TaskStage 'AppData'))) { throw 'Unexpected local data in package.' }
    $TaskManifest = [ordered]@{ Version=$Version; RuntimeIdentifier='win-x64'; SelfContained=$true; Signed=$false; Frameworks=$TaskRuntime.runtimeOptions.includedFrameworks }
    $TaskManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $TaskStage 'release-info.json') -Encoding utf8
    $TaskHashes = foreach ($TaskFile in Get-ChildItem -LiteralPath $TaskStage -Recurse -File | Sort-Object FullName) { '{0}  {1}' -f (Get-FileHash -LiteralPath $TaskFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetRelativePath($TaskStage,$TaskFile.FullName).Replace('\','/') }
    $TaskHashes | Set-Content -LiteralPath (Join-Path $TaskStage 'SHA256SUMS.txt') -Encoding utf8
    [IO.Compression.ZipFile]::CreateFromDirectory($TaskStage,$TaskZip,[IO.Compression.CompressionLevel]::Optimal,$false)
    [IO.Compression.ZipFile]::ExtractToDirectory($TaskZip,$TaskExtract)
    foreach ($TaskLine in Get-Content -LiteralPath (Join-Path $TaskExtract 'SHA256SUMS.txt')) {
        if ($TaskLine -notmatch '^([0-9a-f]{64})  (.+)$') { throw 'Invalid checksum manifest.' }
        if ((Get-FileHash -LiteralPath (Join-Path $TaskExtract $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Matches[1]) { throw 'Extraction hash mismatch.' }
    }
    $TaskTestRoot = Join-Path $TaskRelease 'self-test-data'; New-Item -ItemType Directory -Path $TaskTestRoot -Force | Out-Null
    $TaskProcess = Start-Process -FilePath (Join-Path $TaskExtract 'RefrigerationHmi.exe') -ArgumentList '--self-test' -WorkingDirectory $TaskExtract -WindowStyle Hidden -Environment @{REFRIGERATION_HMI_TEST_ROOT=$TaskTestRoot} -PassThru
    if (-not $TaskProcess.WaitForExit(45000)) { $TaskProcess.Kill(); throw 'Self-test timed out.' }
    if ($TaskProcess.ExitCode -ne 0) { throw "Packaged self-test failed: $($TaskProcess.ExitCode)" }
    $TaskProof = Get-Content -LiteralPath (Join-Path $TaskExtract 'self-test-results.txt')
    if (@($TaskProof | Where-Object { $_ -like 'PASS *' -and $_ -notmatch '^PASS \d+ checks\.' }).Count -ne 19 -or $TaskProof[-1] -notmatch '^PASS 19 checks\.') { throw 'Expected 19 passing checks.' }
    Copy-Item -LiteralPath (Join-Path $TaskExtract 'self-test-results.txt') -Destination (Join-Path $TaskRelease 'self-test-results.txt')
    $TaskZipHash = (Get-FileHash -LiteralPath $TaskZip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $TaskRelease 'SHA256SUMS.txt'), ($TaskZipHash + '  ' + [IO.Path]::GetFileName($TaskZip) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    $TaskResult = [ordered]@{ Archive=$TaskZip; ExtractedDirectory=$TaskExtract; SHA256=$TaskZipHash; Bytes=(Get-Item -LiteralPath $TaskZip).Length; SelfTest='PASS'; Checks=19 }
    $TaskResult | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $TaskRelease 'release-result.json') -Encoding utf8
    $TaskResult | ConvertTo-Json
} finally { Pop-Location }
