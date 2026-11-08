# Publish a MiniDAW release: builds MiniDAW.exe, runs its self-test, builds MiniDAW-Setup.exe, pushes the committed source
# and creates GitHub release v<Version> with the installer attached. Copies of MiniDAW find it the next time they start.
#
# Before running: bump AboutForm.Version in MiniDAW.cs and commit everything. Needs the GitHub CLI, signed in (gh auth login).
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Notes "What changed"
param([string]$Notes = "")
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = Get-Content MiniDAW.cs -Raw
$ver = [regex]::Match($src, 'public const string Version = "([^"]+)"').Groups[1].Value
$repo = [regex]::Match($src, 'public const string Repo = "([^"]*)"').Groups[1].Value
if (-not $ver) { throw "AboutForm.Version not found in MiniDAW.cs" }
if (-not $repo) { throw "Updates.Repo is empty in MiniDAW.cs: installed copies wouldn't find updates" }
$tag = "v$ver"

if (git status --porcelain) { throw "Uncommitted changes: commit them first, so the release matches the source on GitHub" }
gh release view $tag --repo $repo *> $null
if ($LASTEXITCODE -eq 0) { throw "$tag is already released: bump AboutForm.Version in MiniDAW.cs first" }

& $csc /nologo /target:winexe /win32icon:banana.ico /out:MiniDAW.exe MiniDAW.cs
if ($LASTEXITCODE) { throw "MiniDAW.exe didn't build" }
$log = Join-Path $env:TEMP "minidaw-selftest.txt"
$t = Start-Process .\MiniDAW.exe -ArgumentList '--selftest', $log -Wait -PassThru
if ($t.ExitCode) { Get-Content $log | Select-String '^FAIL'; throw "Self-test failed: nothing was released" }
& $csc /nologo /target:winexe /win32icon:banana.ico /resource:MiniDAW.exe,MiniDAW.exe /resource:README.md,README.md /resource:banana.png,banana.png /out:MiniDAW-Setup.exe Setup.cs
if ($LASTEXITCODE) { throw "MiniDAW-Setup.exe didn't build" }

git push origin HEAD
if ($LASTEXITCODE) { throw "git push failed" }
if (-not $Notes) { $Notes = "MiniDAW $ver" }
$Notes += "`n`n**Install:** download **MiniDAW-Setup.exe** below and run it. Already have MiniDAW? It shows an **Update** button when it starts."
gh release create $tag MiniDAW-Setup.exe --repo $repo --target (git rev-parse HEAD) --title "MiniDAW $ver" --notes $Notes
if ($LASTEXITCODE) { throw "gh release create failed" }
"Released ${tag}: https://github.com/$repo/releases/tag/$tag"
