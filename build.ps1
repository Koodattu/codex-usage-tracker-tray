$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build tests/CodexTray.Tests/CodexTray.Tests.csproj -c Release --configfile NuGet.Config --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & ./tests/CodexTray.Tests/bin/Release/net48/CodexTray.Tests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
    New-Item -ItemType Directory -Path dist -Force | Out-Null
    Copy-Item -LiteralPath src/CodexTray/bin/Release/net48/CodexTray.exe -Destination dist/CodexTray.exe -Force
    Copy-Item -LiteralPath LICENSE -Destination dist/LICENSE.txt -Force
    $hash = (Get-FileHash -LiteralPath dist/CodexTray.exe -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $PWD 'dist/SHA256SUMS.txt'), "$hash  CodexTray.exe`n", [Text.Encoding]::ASCII)
    Get-Item dist/CodexTray.exe | Select-Object FullName, Length
} finally { Pop-Location }
