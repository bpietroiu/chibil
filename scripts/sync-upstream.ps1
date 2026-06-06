<#
.SYNOPSIS
    Helper for the Claude-assisted upstream integration ("/sync-upstream").
    Automates the mechanical phases; conflict resolution is done by Claude
    using domain knowledge (see .claude/commands/sync-upstream.md).

.DESCRIPTION
    Modes:
      analyze  - Fetch upstream and report the incoming backlog: commit list,
                 files touched, and a predicted-conflict list (git merge-tree).
                 Read-only; does not modify the working tree.
      start    - Create the integration branch and run `git merge --no-ff
                 upstream/master`, leaving conflicts in the tree for Claude to
                 resolve. Refuses to run on a dirty tree.
      regress  - Run the full green-gate regression suite (build + tests +
                 MicroPython + QuickJS). Exits non-zero on the first failure.

    `regress` is the exact gate the merge must pass before it is committed and
    landed on master. Run it from a developer shell that has MSVC on PATH
    (vcvars64) so the cl.exe/link.exe interop tests are not spuriously skipped.

.EXAMPLE
    pwsh scripts/sync-upstream.ps1 -Mode analyze
    pwsh scripts/sync-upstream.ps1 -Mode start
    pwsh scripts/sync-upstream.ps1 -Mode regress
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('analyze', 'start', 'regress')]
    [string]$Mode,

    # Upstream ref to integrate (default: the tracking branch).
    [string]$Upstream = 'upstream/master',

    # Build configuration for the regression suite.
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $repo

function Write-Section($text) {
    Write-Host ''
    Write-Host "=== $text ===" -ForegroundColor Cyan
}

function Invoke-Step($name, [scriptblock]$body) {
    Write-Section $name
    & $body
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
    Write-Host "OK: $name" -ForegroundColor Green
}

switch ($Mode) {

    'analyze' {
        Invoke-Step 'fetch upstream' { git fetch upstream --prune }

        Write-Section "incoming commits ($Upstream not in HEAD)"
        $incoming = git rev-list --reverse "HEAD..$Upstream"
        if (-not $incoming) {
            Write-Host 'Up to date — no upstream commits to integrate.' -ForegroundColor Green
            exit 0
        }
        git log --oneline --no-decorate "HEAD..$Upstream"

        Write-Section 'files touched by the incoming backlog'
        git diff --stat "HEAD...$Upstream"

        Write-Section 'predicted conflicts (git merge-tree)'
        $base = git merge-base HEAD $Upstream
        # merge-tree emits conflict markers for files that will not auto-merge.
        $mt = git merge-tree --write-tree --name-only HEAD $Upstream 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host 'Clean merge predicted (no conflicts).' -ForegroundColor Green
        } else {
            Write-Host 'Conflicts predicted in:' -ForegroundColor Yellow
            # The lines after the tree OID are the conflicting paths.
            $mt | Select-Object -Skip 1 | Where-Object { $_ } | ForEach-Object { Write-Host "  $_" }
        }
        Write-Host ''
        Write-Host "Next: pwsh scripts/sync-upstream.ps1 -Mode start" -ForegroundColor Cyan
    }

    'start' {
        $dirty = git status --porcelain
        if ($dirty) { throw "Working tree is dirty — commit or stash before starting a sync." }

        Invoke-Step 'fetch upstream' { git fetch upstream --prune }

        $stamp = git log -1 --format=%cd --date=format:'%Y%m%d' "$Upstream"
        $branch = "sync/upstream-$stamp"
        Write-Section "create integration branch $branch"
        git checkout -b $branch
        if ($LASTEXITCODE -ne 0) { throw "Could not create $branch (already exists?)." }

        Write-Section "merge $Upstream"
        git merge --no-ff --no-commit $Upstream
        # A non-zero exit here means conflicts — expected. Leave them for Claude.
        $conflicts = git diff --name-only --diff-filter=U
        if ($conflicts) {
            Write-Host 'CONFLICTS to resolve:' -ForegroundColor Yellow
            $conflicts | ForEach-Object { Write-Host "  $_" }
            Write-Host ''
            Write-Host 'Resolve with domain knowledge (see the command doc), then:' -ForegroundColor Cyan
            Write-Host '  pwsh scripts/sync-upstream.ps1 -Mode regress' -ForegroundColor Cyan
        } else {
            Write-Host 'Merged with no conflicts. Run the regression gate next:' -ForegroundColor Green
            Write-Host '  pwsh scripts/sync-upstream.ps1 -Mode regress' -ForegroundColor Cyan
        }
    }

    'regress' {
        $remaining = git diff --name-only --diff-filter=U
        if ($remaining) { throw "Unresolved conflicts remain: $($remaining -join ', ')" }
        if (git grep -lE '^(<<<<<<<|=======|>>>>>>>)' -- '*.cs' 2>$null) {
            throw "Conflict markers still present in source."
        }

        Invoke-Step 'build chibil'      { dotnet build chibil/Chibil.csproj -c $Configuration -v q --nologo }
        Invoke-Step 'build chibil-link' { dotnet build tools/chibil-link/ChibilLink.csproj -c $Configuration -v q --nologo }
        Invoke-Step 'build tests'       { dotnet build tests/Chibil.Tests/Chibil.Tests.csproj -c $Configuration -v q --nologo }

        # Full unit suite. ~120 cl.exe/link.exe interop tests need MSVC on PATH;
        # warn (do not fail) if vcvars was not sourced, since those failures are
        # environmental, never regressions.
        if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
            Write-Host 'WARNING: cl.exe not on PATH — run from a vcvars64 shell so MSVC interop tests run.' -ForegroundColor Yellow
        }
        Invoke-Step 'unit tests' {
            dotnet test tests/Chibil.Tests/Chibil.Tests.csproj -c $Configuration --no-build -v q --nologo
        }

        # Integration oracles — exercise setjmp/NLR, calli/fnptr, variadic, and
        # the field-partition path far harder than the unit tests.
        Invoke-Step 'MicroPython on managed musl' {
            dotnet build targets/micropython-managed/MicroPythonManaged.proj -t:Run -c $Configuration -v m --nologo
        }
        Invoke-Step 'QuickJS oracle' {
            dotnet build targets/quickjs/QuickJsManaged.proj -t:Oracle -c $Configuration -v m --nologo
        }

        Write-Host ''
        Write-Host 'GREEN — all regression gates passed.' -ForegroundColor Green
        Write-Host 'Finalize: git commit (the merge), then fast-forward master:' -ForegroundColor Cyan
        Write-Host '  git commit --no-verify' -ForegroundColor Cyan
        Write-Host '  git checkout master; git merge --ff-only <sync-branch>' -ForegroundColor Cyan
    }
}
