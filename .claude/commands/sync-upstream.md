---
description: Claude-assisted integration of upstream (MichalStrehovsky/chibil) into this fork — fetch, analyze, merge, resolve conflicts with domain knowledge, regression-gate, land on master.
argument-hint: "[analyze | full]   (default: full)"
---

You are performing a **Claude-based upstream merge**: an AI-assisted integration
of `upstream/master` (MichalStrehovsky/chibil) into this fork. The mechanical
phases are scripted (`scripts/sync-upstream.ps1`); the *judgment* — resolving
conflicts so our forks survive on top of upstream's refactors — is yours.

`$ARGUMENTS` selects scope: `analyze` stops after the impact report; `full` (the
default) runs the whole pipeline through landing on master.

## Standing constraints (do not violate)

- **Never** commit anything under `targets/musl-1.2.6/`, `targets/quickjs-*/`, or
  `targets/micropython/` — third-party, git-ignored.
- **Never** keep edits to third-party musl/quickjs/micropython sources as
  permanent workarounds. Temporary debug instrumentation must be reverted.
- Commit messages end with:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- **Do not push.** Land on `master` locally only. Pushing is a separate, explicit
  user request.
- The ~120 cl.exe/link.exe test failures when MSVC is not on PATH are
  environmental, never regressions. Run the suite from a `vcvars64` shell.

## Phase 1 — Analyze (always)

Run `pwsh scripts/sync-upstream.ps1 -Mode analyze`. It fetches upstream and
reports: the incoming commit list, files touched, and predicted conflicts.

Summarize for the user: how many commits, whether they are feature work or
internal refactors, and which of *our* heavily-forked files they collide with
(`chibil/CodeGen.cs`, `chibil/Parser.cs`, `chibil/ChibiTypes.cs`,
`chibil/TypeSystem.cs`, `tools/chibil-link/*`). If `$ARGUMENTS` is `analyze`,
stop here and report.

## Phase 2 — Merge

Run `pwsh scripts/sync-upstream.ps1 -Mode start`. It creates `sync/upstream-<date>`
and runs `git merge --no-ff --no-commit`, leaving conflicts in the tree.

## Phase 3 — Resolve conflicts (your domain knowledge)

For each conflicted file, read both sides and the merge base
(`git show :1:<f>`, `:2:<f>`, `:3:<f>` = base/ours/theirs) before editing.
Take upstream's refactor as the new substrate and re-port our changes onto it.
**Invariants our fork must preserve** (verify each survives):

1. **setjmp/longjmp lowering** (`CodeGen.cs`). Managed-exception resumption via
   `.try { body } filter { ours? } handler { resume }`. The synthetic labels
   live in dedicated `LabelHandle` fields (`_setjmpEpiLabel`, `_setjmpLhead`,
   …), *not* in the label table.
   - `Goto`/`Label` route through `SjBranch(...)` (not raw `_enc.Branch`) so a
     branch exiting the try becomes a `leave` via trampoline; `Label` registers
     the target in `_setjmpOuterLabels` when `_setjmpDeferStart && !_setjmpTryOpen`.
   - `Return` inside the try funnels the value into `_setjmpRetvalLocal` and
     `leave`s to `_setjmpEpiLabel`; outside it emits `ret`.
   - `StmtAlwaysTransfers` suppresses the dead if/else merge `br` that would
     otherwise branch into the try. The re-arm reset (zero the setjmp result
     local before `Lhead`) must remain.
   - Upstream uses an **int label table**: `LabelHandle[] _labels`,
     `GetLabel(int)`, `Node.LabelId/BrkLabelId/ContLabelId`, `Obj.LabelCount`.
     Port our setjmp hooks onto `GetLabel(node.LabelId)` — do **not** reintroduce
     a string-keyed `_labels` dictionary.

2. **CoreCLR Default calling convention** for indirect calls. Both
   `EncodeFnPtrSignature` and the inline `calli` signature must force
   `SignatureCallingConvention.Default` when `_options.Target == TargetProfile.CoreClr`.
   If upstream's helper drops this, the `mp_iternext` `InvalidProgramException`
   returns. Fold the CoreCLR check into the conv byte.

3. **Variadic function definitions** — the hidden trailing `__va` pointer param.
   `RegisterFunction`, `RegisterExternalFunction`, and the `calli` sig each
   append `_types.TyVaList` when `funcTy.IsVariadic && funcTy.Params != null`.
   Upstream's `EncodeFunctionSignature` does **not** add it, so keep those three
   sites' param-emission inline. Also keep `FunctionDeclaratorTail`
   (multi-declarator prototypes), `fn.AllocaBottom`/alloca, `Node.ScopeId`
   scope tracking, and the `EvalRval` null-ref-sink guard.

4. **Computed goto is gone upstream** (`NodeKind.GotoExpr` / labels-as-values
   removed). Delete any `case NodeKind.GotoExpr` we still reference (e.g. in
   `StmtAlwaysTransfers`).

5. **chibil-link** field partitioning (`$GlobalFields$N` for >0xFFFF `<Module>`
   fields) and the weak-alias-within-defining-object resolution must survive.

After editing, confirm no markers remain: `git grep -nE '^(<<<<<<<|=======|>>>>>>>)'`.

## Phase 4 — Regression gate (green or stop)

Run `pwsh scripts/sync-upstream.ps1 -Mode regress` **from a vcvars64 shell**. It
builds chibil/chibil-link/tests, runs the full unit suite, then the MicroPython
(managed-musl `-t:Run`) and QuickJS (`-t:Oracle`) integration oracles. Any red
gate stops the merge — fix and re-run.

**Deep check for codegen-touching merges** (optional but recommended when
`CodeGen.cs`/`Parser.cs` conflicted): prove the merge adds no new musl compile
failures. Build a pre-merge chibil from the first parent in a worktree and A/B
compile representative files; the failure set must be identical:

```
git worktree add -f /tmp/chibil-premerge HEAD~1
dotnet build /tmp/chibil-premerge/chibil/Chibil.csproj -c Release -v q
# compile the same musl files with both chibils; exit codes must match
git worktree remove /tmp/chibil-premerge --force
```

## Phase 5 — Land

Only when **every** gate is green:

1. `git add` the resolved files (never third-party paths).
2. `git commit --no-verify` with a message that lists what upstream changed,
   what we re-ported, and the regression result (see the prior merge commit for
   the template). End with the Co-Authored-By line.
3. Land on master: `git checkout master && git merge --ff-only sync/upstream-<date>`.
4. Delete the branch: `git branch -d sync/upstream-<date>`.
5. Report divergence: `git rev-list --left-right --count upstream/master...master`
   (left = upstream commits still unmerged; should be 0).

Do **not** push. Tell the user the merge landed locally and offer to push if they
want it on origin.
