# Branch protection setup for `main`

> **Status as of 2026-06-06: DEFERRED, NOT APPLIED.**
>
> The repo is private on GitHub's free tier, which restricts both modern
> rulesets and classic branch protection to **GitHub Pro** or **public**
> repositories. Attempts return `403: Upgrade to GitHub Pro or make this
> repository public to enable this feature`.
>
> **Decision (CEO + Producer):** stay private for now and enforce the "no
> direct pushes to `main`" and "PR required" rules **by discipline only**.
> Revisit if the repo goes public (matches PROJECT_BRIEF intent of MIT /
> open-source) or upgrades to Pro.
>
> The procedure below is kept ready for when that happens — do not delete it.

---

> **Audience:** the repo owner (human). This cannot be done from a PR — it
> requires the GitHub web UI on the live repo.
>
> **When:** apply **after** the Phase 0 bootstrap PR (`sprint-1 phase-0:
> repo bootstrap + CI`) has been merged to `main`, so that:
>   1. `.github/workflows/ci.yml` lives on `main`, and
>   2. CI has run at least once — this makes the status check appear in the
>      dropdown when configuring the rule.

This doc covers two paths. Pick **one**; do not configure both at once.
- **Path A — Branch ruleset** (modern, recommended). GitHub's current direction.
- **Path B — Classic branch protection rule** (older UI, same effect).

---

## Path A — Branch ruleset (recommended)

1. Open the repo on GitHub: `https://github.com/denis-a-evdokimov/get-and-see`.
2. **Settings** (top nav) → **Rules** (left sidebar) → **Rulesets** → **New ruleset** → **New branch ruleset**.
3. **Ruleset name:** `main protection`
4. **Enforcement status:** **Active**
5. **Target branches:**
   - Click **Add target** → **Include by pattern** → enter `main` → **Add Inclusion pattern**.
6. **Branch protections** — enable each of the following:

   - [x] **Restrict deletions**
     (Prevents anyone from deleting `main`.)

   - [x] **Block force pushes**
     (Prevents `git push --force` to `main` — already a team rule per
     PROJECT_BRIEF §14.)

   - [x] **Require a pull request before merging**
     - **Required approvals:** `1`
     - [x] Dismiss stale pull request approvals when new commits are pushed
     - (Leave "Require review from Code Owners" **off** — no CODEOWNERS file
       in v1.)
     - (Leave "Require approval of the most recent reviewable push" **on**
       if available.)
     - **Allowed merge methods:** check **Merge** (and optionally **Squash**
       and **Rebase** — but the team uses regular merge per PROJECT_BRIEF
       §14 / user preferences).

   - [x] **Require status checks to pass**
     - [x] Require branches to be up to date before merging
     - Click **Add checks** → search the dropdown for the CI job. The check
       name comes from the `name:` field under `jobs.build` in
       `.github/workflows/ci.yml`, which is:
       **`build + test (windows-latest, .NET 10)`**
     - Select that check and add it.
     - **Source:** GitHub Actions.

7. **DO NOT** enable **"Require linear history"** — the team uses **regular
   merge commits**, not squash or rebase, per PROJECT_BRIEF §14. Linear
   history would reject regular merges.

8. (Optional, leave off for v1 unless the team explicitly wants them)
   - Require signed commits — off (no signing setup in v1).
   - Require deployments to succeed — off (no environments yet).
   - Require code scanning results — off (no CodeQL setup in v1).
   - Require conversation resolution — off (nice-to-have; not blocking yet).

9. Click **Create** at the bottom.

10. **Verify** by:
    - Try to push directly to `main` from a clone — it should be rejected.
    - Open a throwaway PR — the **Merge** button should be greyed out until
      CI passes and at least one approval is given.

---

## Path B — Classic branch protection rule (if you prefer the older UI)

1. **Settings** → **Branches** (left sidebar) → under **Branch protection
   rules**, click **Add branch protection rule** (or **Add rule**).
2. **Branch name pattern:** `main`
3. Enable:

   - [x] **Require a pull request before merging**
     - **Required approvals:** `1`
     - [x] Dismiss stale pull request approvals when new commits are pushed

   - [x] **Require status checks to pass before merging**
     - [x] Require branches to be up to date before merging
     - In the search box, find and add: **`build + test (windows-latest, .NET 10)`**
       (This is the job name from `.github/workflows/ci.yml`. If it does not
       appear, ensure CI has run at least once on `main` first.)

   - [x] **Require conversation resolution before merging** (optional)

   - [x] **Do not allow bypassing the above settings** — apply rules to
     administrators too. (Recommended.)

   - [x] **Restrict who can push to matching branches** is **not** needed —
     the PR requirement above already blocks direct pushes.

4. **DO NOT** check:
   - **Require linear history** — incompatible with regular merge commits
     (PROJECT_BRIEF §14).
   - **Require signed commits** — no signing setup in v1.

5. Under **Rules applied to everyone including administrators**:
   - [x] **Allow force pushes:** leave **unchecked** (force pushes blocked).
   - [x] **Allow deletions:** leave **unchecked** (deletion blocked).

6. Click **Create** (or **Save changes**).

7. Verify same as Path A above.

---

## Why these specific settings

| Setting | Why |
|---|---|
| Require PR | Code review + CI gate, per PROJECT_BRIEF §14 ("Never push directly to main"). |
| 1 approval | Small team; one reviewer is sufficient. Raise to 2 if the team grows. |
| Require CI status check | Build, format, tests, **and the read-only contract test** must pass before merge (PROJECT_BRIEF §9.1). |
| Require branches up to date | Catches semantic conflicts where two branches each pass CI alone but break together. |
| Block force pushes | Force-push lost commits in past projects (see user memory — "Arcade After Dark" Sprint 2b lesson). |
| Restrict deletions | `main` is the source of truth; accidental deletion is catastrophic. |
| **NOT** linear history | Team uses regular merge commits, not squash/rebase (PROJECT_BRIEF §14, user preferences). |

---

## After applying

1. Confirm the rule is active by attempting `git push origin main` from a
   clone with a local commit — it must be rejected.
2. Run `pwsh ./docs/sprint-1/setup-labels.ps1` from any clone (after
   `gh auth login`) to seed the issue/PR labels listed in
   `.github/labels.yml`.
3. The repo is now ready for QA to start filing bugs and for Nova/Sage to
   open PRs against `main`.
