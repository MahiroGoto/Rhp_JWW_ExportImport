# Builds bin\Rhp_JWW_ExportImport.rhp and bin\JwwDump.exe with the .NET Framework C# compiler
# (no Visual Studio / .NET SDK needed).
# Run:  powershell -ExecutionPolicy Bypass -File build.ps1
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw   = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$rh   = "C:\Program Files\Rhino 8\System"
$refs = @("$rh\RhinoCommon.dll", "$fw\System.Drawing.dll", "$fw\System.dll", "$fw\System.Core.dll")
if (Test-Path "$fw\netstandard.dll") { $refs += "$fw\netstandard.dll" }
$common = @("/nologo", "/optimize+", "/platform:x64", "/codepage:65001")
New-Item -ItemType Directory -Force "$here\bin" | Out-Null
$ok = $true

function Build($out, $target, $refList, $sources) {
    $a = $common + @("/target:$target", "/out:$out") + ($refList | ForEach-Object { "/r:$_" }) + $sources
    & "$fw\csc.exe" @a
    if ($LASTEXITCODE -eq 0) { Write-Output "built $out" } else { $script:ok = $false }
}

$plugin = @(Get-ChildItem "$here\src" -Recurse -Filter *.cs | ForEach-Object { $_.FullName })
Build "$here\bin\Rhp_JWW_ExportImport.rhp" "library" $refs $plugin
Build "$here\bin\JwwDump.exe" "exe" @() @("$here\tools\JwwDump.cs", "$here\src\Common\JwwReader.cs")
if (-not $ok) { exit 1 }
