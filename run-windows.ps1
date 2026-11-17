[CmdletBinding()]
param(
    [string]$Godot = $env:GODOT,
    [string]$Spawn,
    [switch]$Headless
)

$ErrorActionPreference = 'Stop'
$godotExample = 'Pass -Godot with the path to the Godot 4.7.2 .NET executable, or set GODOT.'

if ($Godot -notlike '*.exe' -or -not (Test-Path -LiteralPath $Godot -PathType Leaf)) {
    throw $godotExample
}

$godotExe = (Get-Item -LiteralPath $Godot).FullName
if ($godotExe -notlike '*_console.exe') {
    $consoleExe = $godotExe -replace '\.exe$', '_console.exe'
    if (-not (Test-Path -LiteralPath $consoleExe -PathType Leaf)) {
        throw 'Keep the Godot executable and its _console.exe companion together in the extracted folder.'
    }
    $godotExe = $consoleExe
}

$version = & $godotExe --version
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^4\.7\.2\.stable\.mono(\.|$)') {
    throw "Expected Godot 4.7.2 .NET. $godotExample"
}

$dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $dotnet) {
    throw 'Install the .NET 8 SDK and open a new PowerShell window.'
}

Push-Location -LiteralPath $PSScriptRoot
try {
    & $dotnet.Source build World.csproj
    if ($LASTEXITCODE -ne 0) {
        throw "C# build failed (exit code $LASTEXITCODE). Check the build output and the .NET SDK installation."
    }

    & $godotExe --headless --path . --import
    if ($LASTEXITCODE -ne 0) {
        throw "Godot import failed (exit code $LASTEXITCODE)."
    }

    $engineArgs = @('--path', '.')
    if ($Headless) {
        $engineArgs += '--headless', '--quit-after', '120'
    }
    if ($Spawn) {
        $engineArgs += '--', "--spawn=$Spawn"
    }

    & $godotExe @engineArgs
    if ($LASTEXITCODE -ne 0) {
        throw "World exited with code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
