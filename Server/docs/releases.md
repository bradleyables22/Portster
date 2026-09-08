# Publish Windows executables with GitHub Actions

GitHub Releases is Portster's distribution channel for end users. A release is a
durable, versioned page; its attached files are the public downloads. GitHub
Actions run artifacts are only temporary handoffs between jobs and are not the
end-user download location.

## Release asset contract

Every published release should contain these assets:

- `portster-win-x64.zip`
- `portster-win-arm64.zip`
- `SHA256SUMS`

Each ZIP contains the self-contained `Portster.exe` and its accompanying README,
docs, example policy, profile schema, and license. The asset names intentionally
do not contain the version. Stable names allow a future stable release to use a
permanent URL such as:

```text
https://github.com/bradleyables22/Portster/releases/latest/download/portster-win-x64.zip
```

The `releases/latest` route selects the newest full release, not a prerelease.
Beta users should choose the version from the Releases page.

## Repository setup

1. In **Settings > Actions > General**, ensure repository or organization policy
   allows workflows to grant `contents: write`. No personal access token is
   needed; the workflow uses its short-lived `GITHUB_TOKEN`.
2. Protect `master`. If pull-request CI is configured, require its test check
   before merge.
3. Create annotated, preferably signed, SemVer tags such as `v0.1.0-beta` or
   `v1.0.0` on a commit contained in `master`. The tag is the release trigger and
   source of the executable version.
4. Keep the checked-in [release workflow](../../.github/workflows/release.yml)
   enabled in the repository's **Actions** tab.
5. After validating a beta release, consider enabling immutable releases in the
   repository settings. Publish as a draft first if assets need manual review.

Do not put signing keys or tokens in the repository. GitHub's artifact
attestation uses OpenID Connect and the workflow's temporary token.

## Release workflow

The checked-in workflow tests once, publishes both Windows runtime identifiers,
creates stable ZIP names and checksums, attests the ZIPs, and attaches all three
files to a GitHub Release. It fetches `origin/master` and rejects the release
unless the tagged commit is part of that branch. No release branch is used.
Third-party release actions are unnecessary because the GitHub CLI is already
installed on GitHub-hosted runners.

The action references are pinned to versioned commit SHAs. Dependabot can keep
them current; review its updates before merging. Pinning the .NET SDK also makes
the toolchain reproducible. Update that pin deliberately when adopting a newer
SDK servicing release.

## Publish a release

Run the normal tests locally, commit the release notes and version-related docs,
make sure that commit is on `master`, then create and push the tag:

```powershell
git switch master
git pull --ff-only origin master
dotnet test Portster.slnx -c Release
git tag -s v0.1.0-beta -m "Portster 0.1.0-beta"
git push origin v0.1.0-beta
```

Use `git tag -a` instead of `-s` only when signed Git tags are not configured.
Pushing the tag starts the workflow. In GitHub, open **Actions**, watch the
release job, then open **Releases** and confirm that both ZIPs, `SHA256SUMS`, and
the repository's attestation record are present before sharing the release.

If the workflow fails before release creation, fix the problem, delete and
recreate the local tag at the corrected commit, and push it only if the tag was
never distributed. If a release or tag has already been shared, keep it
immutable and publish a new patch version instead.

## Why this arrangement

- `dotnet test` gates publication, so a failing tag does not produce a release.
- `-r` and `--self-contained true` produce architecture-specific executables
  that do not require .NET on the user's computer.
- Stable asset names support GitHub's `/releases/latest/download/...` URLs after
  Portster has a non-prerelease release.
- `SHA256SUMS` detects corrupted or substituted downloads.
- Artifact attestations link the archives to the repository and workflow that
  built them. Users can verify one with GitHub CLI, for example:

  ```powershell
  gh release verify-asset v1.0.0 .\portster-win-x64.zip --repo bradleyables22/Portster
  ```

- The release remains the durable public download; no retention setting for
  temporary Actions artifacts affects it.

## Current references

- [GitHub release asset URLs](https://docs.github.com/en/repositories/releasing-projects-on-github/linking-to-releases)
- [GitHub CLI release creation](https://cli.github.com/manual/gh_release_create)
- [GitHub artifact attestations](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations)
- [.NET single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
