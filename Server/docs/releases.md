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

Every successful `master` build is published as the latest release, so this URL
always selects the newest successful build.

## Repository setup

1. In **Settings > Actions > General**, ensure repository or organization policy
   allows workflows to grant `contents: write`. No personal access token is
   needed; the workflow uses its short-lived `GITHUB_TOKEN`.
2. Protect `master`. If pull-request CI is configured, require its test check
   before merge.
3. Keep the checked-in [release workflow](../../.github/workflows/release.yml)
   enabled in the repository's **Actions** tab.
4. After validating the pipeline, consider enabling immutable releases in the
   repository settings.

Do not put signing keys or tokens in the repository. GitHub's artifact
attestation uses OpenID Connect and the workflow's temporary token.

## Release workflow

The checked-in workflow tests once, publishes both Windows runtime identifiers,
creates stable ZIP names and checksums, attests the ZIPs, and attaches all three
files to a GitHub Release. A push or pull-request merge to `master` triggers it;
no release branch or manually created tag is used. GitHub Releases still require
a tag internally, so the workflow creates one automatically in the form
`vYYYY.MM.DD.RUN`, such as `v2026.09.08.17`. The date is UTC and the final number
is GitHub's unique run number for this workflow.

Third-party release actions are unnecessary because the GitHub CLI is already
installed on GitHub-hosted runners. The CLI creates the automatic tag directly
on the exact `master` commit that triggered the run.

The action references are pinned to versioned commit SHAs. Dependabot can keep
them current; review its updates before merging. Pinning the .NET SDK also makes
the toolchain reproducible. Update that pin deliberately when adopting a newer
SDK servicing release.

## Publish a release

In Visual Studio, commit the changes to `master` and select **Push** or **Sync**.
If changes are developed on another branch, merge its pull request into `master`
instead. That is the entire release operation; do not create a tag manually.

In GitHub, open **Actions** and watch the **Release Windows executables** job.
After it succeeds, open **Releases** and confirm that both ZIPs, `SHA256SUMS`,
and the repository's attestation record are present before sharing the release.

If the workflow fails, fix the problem and push the correction to `master`; that
push receives a new run number and creates a new release. If a release has
already been shared, keep it immutable and let the correction create another
dated release.

## Why this arrangement

- `dotnet test` gates publication, so a failing tag does not produce a release.
- `-r` and `--self-contained true` produce architecture-specific executables
  that do not require .NET on the user's computer.
- Stable asset names support GitHub's `/releases/latest/download/...` URLs.
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
