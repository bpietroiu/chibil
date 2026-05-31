# samples/sqlite/fetch-amalgamation.ps1
# Pinned SQLite amalgamation. If this URL 404s, bump to a current release
# from https://sqlite.org/download.html and update VERSION.
$ErrorActionPreference = 'Stop'
$VERSION = '3470200'   # SQLite 3.47.2
$url = "https://www.sqlite.org/2024/sqlite-amalgamation-$VERSION.zip"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$vendor = Join-Path $here 'vendor'
New-Item -ItemType Directory -Force $vendor | Out-Null
$zip = Join-Path $env:TEMP "sqlite-$VERSION.zip"
Invoke-WebRequest -Uri $url -OutFile $zip
$tmp = Join-Path $env:TEMP "sqlite-$VERSION"
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
Expand-Archive $zip -DestinationPath $tmp
$src = Get-ChildItem -Recurse $tmp -Filter sqlite3.c | Select-Object -First 1
$hdr = Get-ChildItem -Recurse $tmp -Filter sqlite3.h | Select-Object -First 1
Copy-Item $src.FullName (Join-Path $vendor 'sqlite3.c')
Copy-Item $hdr.FullName (Join-Path $vendor 'sqlite3.h')
Write-Output "vendored sqlite $VERSION -> $vendor"
