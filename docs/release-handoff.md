# Preview publication and consumer handoff

Updated 2026-10-03. Publish corrected IO first, then Luban, then verify Hufu
against NuGet.org. Candidate artifacts and caches do not close public adoption.

## Ready release tooling

The tagged release workflow validates the checked-in version against the exact
selected existing tag, restores dependencies only from NuGet.org into a fresh
cache, tests both frameworks with no skipped/failed cases, builds the host sample,
qualifies the Linux neutral core, packs and inspects the package, and runs an
isolated package consumer. The publish job uses environment nuget and NuGet OIDC;
it receives only the validated artifact, checks existing package contents before
pushing, and verifies publicly downloadable contents afterward. NuGet's
repository signature is the only excluded ZIP entry during content comparison.

The ordinary CI still uses the explicit pinned IO candidate feed. Switch that
workflow to public IO restores after IO publication succeeds. Do not claim that
existing candidate CI proves public adoption.

## Qualified inputs and current blocker

- IO tag v0.1.0-preview.1 is fixed at
  468dde33f0cda8f8f26a734abd0e512cea70d138.
- [IO release run 37084905564](https://github.com/jenolaszlo-sketch/penghou/actions/runs/37084905564)
  passes exact-tag validation, Windows tests, Linux builds, package checks and
  isolated consumption. Its publish job stops because NUGET_USER is missing.
- The final Luban API working tree passes 338 cases on each framework, packs with
  API/TFM compatibility guards, and passes package smoke on both frameworks using
  those exact IO release artifacts. Final API changes belong to the concurrently
  completed API review; preserve them and qualify their committed revision before
  tagging Luban.
- Hufu passes 93 Biscuit, 101 core and 19 IO cases per framework using the IO and
  Luban packages in an isolated snapshot with a fresh cache and no IO/Luban source.
  [Its JSON report](../../Penghou.Hufu/docs/qualification/candidate-resource-packages.json)
  records all four archive hashes, actual restore sources and six suite results.
  This is candidate-only evidence. Zhinu still comes from explicitly copied source;
  BiscuitSharp remains the pinned unpublished preview.2 artifact.

Both repositories need NUGET_USER in the nuget environment and matching NuGet
trusted-publisher policies: GitHub owner jenolaszlo-sketch, repository penghou or
penghou-luban respectively, workflow publish.yml, environment nuget. The Luban
GitHub environment has been created. No secret value is stored in source.

## Exact next steps

1. Configure the two publishing identities. Retry only the failed IO publish job
   to reuse release run 37084905564's already validated artifacts:
   gh run rerun 37084905564 --failed --repo jenolaszlo-sketch/penghou.
   Verify all three public versions and matching package contents.
2. Restore/build/test Luban with a fresh cache and NuGet.org-only IO dependencies.
   Commit the final reviewed API and release tooling, qualify that exact revision
   in CI, then create/push v0.1.0-preview.1 on the reviewed Luban commit.
   Manual retry selects that existing tag and release_tag=v0.1.0-preview.1.
   Never move an existing published tag to different source.
3. Verify Luban publication and switch ordinary CI to public IO dependencies.
4. Run Hufu's eng/Test-PublishedResourcePackages.ps1 without CandidateFeedPath.
   It restores exact IO/Luban versions from NuGet.org in an isolated snapshot,
   verifies each fresh-cache source and package asset type, runs all three suites
   on both frameworks, and writes public-resource-packages.json only on success.
5. Refresh the corrective ledger, owner roadmaps and Biscuit handoff/source
   inventory. RA-5B/RA-5C close only after actual publication and public restores.

Hufu's default Luban package reference is exact [0.1.0-preview.1]. Explicit
UseLubanSource=true and UsePenghouSource=true retain development source builds.
Unsupported Luban v2 remains rejected before authority access; its policy support,
production host custody/capacity and governed mutation start/outcome recovery
remain separate Hufu work. VFS and broader providers remain deferred.
