# Builds JwwExport.rhp with the .NET Framework C# compiler (no Visual Studio / .NET SDK needed).
# Run:  powershell -ExecutionPolicy Bypass -File build.ps1
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw   = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$rh   = "C:\Program Files\Rhino 8\System"
$refs = @("$rh\RhinoCommon.dll", "$fw\System.Drawing.dll", "$fw\System.dll", "$fw\System.Core.dll")
if (Test-Path "$fw\netstandard.dll") { $refs += "$fw\netstandard.dll" }
$args = @("/nologo", "/target:library", "/optimize+", "/platform:x64", "/codepage:65001",
          "/out:$here\bin\JwwExport.rhp") + ($refs | ForEach-Object { "/r:$_" }) + @("$here\src\JwwExport.cs")
New-Item -ItemType Directory -Force "$here\bin" | Out-Null
& "$fw\csc.exe" @args
if ($LASTEXITCODE -eq 0) { Write-Output "built $here\bin\JwwExport.rhp" }
