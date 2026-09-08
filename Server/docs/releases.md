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
2. Protect the default branch and require the normal test workflow before merge.
3. Create annotated, preferably signed, SemVer tags such as `v0.1.0-beta` or
   `v1.0.0`. The tag is the release trigger and source of the executable version.
4. Add the workflow below as `.github/workflows/release.yml`.
5. After validating a beta release, consider enabling immutable releases in the
   repository settings. Publish as a draft first if assets need manual review.

Do not put signing keys or tokens in the repository. GitHub's artifact
attestation uses OpenID Connect and the workflow's temporary token.

## Release workflow

This workflow tests once, publishes both Windows runtime identifiers, creates
stable ZIP names and checksums, attests the ZIPs, and attaches all three files to
a GitHub Release. Third-party release actions are unnecessary because the GitHub
CLI is already installed on GitHub-hosted runners.

```yaml
name: Release Windows executables

on:
  push:
    tags:
      - "v*.*.*"

permissions:
  contents: write
  id-token: write
  attestations: write
  artifact-metadata: write

jobs:
  release:
    runs-on: windows-latest
    timeout-minutes: 30

    env:
      DOTNET_CLI_TELEMETRY_OPTOUT: "1"
      DOTNET_NOLOGO: "1"

    steps:
      - name: Check out the tagged source
        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1

      - name: Install .NET SDK
        uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0
        with:
          dotnet-version: "10.0.400"

      - name: Read and validate the version tag
        shell: pwsh
        run: |
          $tag = "${{ github.ref_name }}"
          if ($tag -notmatch '^v(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)$') {
            throw "Tag '$tag' is not a supported SemVer release tag."
          }
          "PORTSTER_VERSION=$($Matches.version)" >> $env:GITHUB_ENV

      - name: Restore and test
        shell: pwsh
        run: |
          dotnet restore Portster.slnx
          dotnet test Portster.slnx -c Release --no-restore

      - name: Publish and package Windows executables
        shell: pwsh
        run: |
          $releaseDirectory = Join-Path $env:GITHUB_WORKSPACE 'artifacts\release'
          New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null

          foreach ($rid in @('win-x64', 'win-arm64')) {
            $publishDirectory = Join-Path $env:GITHUB_WORKSPACE "artifacts\publish\$rid"
            dotnet publish Server/Server.csproj `
              -c Release `
              -r $rid `
              --self-contained true `
              --no-restore `
              -p:Version=$env:PORTSTER_VERSION `
              -o $publishDirectory

            $archive = Join-Path $releaseDirectory "portster-$rid.zip"
            Compress-Archive -Path "$publishDirectory\*" -DestinationPath $archive
          }

          Get-ChildItem -LiteralPath $releaseDirectory -Filter '*.zip' |
            Sort-Object Name |
            ForEach-Object {
              $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
              "$hash *$($_.Name)"
            } |
            Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS') -Encoding ascii

      - name: Attest release archives
        uses: actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6 # v4.2.2
        with:
          subject-path: artifacts/release/*.zip

      - name: Create the GitHub Release
        shell: pwsh
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          $releaseArguments = @(
            'release', 'create', '${{ github.ref_name }}',
            'artifacts/release/portster-win-x64.zip',
            'artifacts/release/portster-win-arm64.zip',
            'artifacts/release/SHA256SUMS',
            '--verify-tag',
            '--generate-notes',
            '--title', "Portster $env:PORTSTER_VERSION"
          )

          if ($env:PORTSTER_VERSION.Contains('-')) {
            $releaseArguments += '--prerelease'
          } else {
            $releaseArguments += '--latest'
          }

          gh @releaseArguments
```

The action references are pinned to reviewed commit SHAs. Dependabot can keep
them current; review its updates before merging. Pinning the .NET SDK also makes
the toolchain reproducible. Update that pin deliberately when adopting a newer
SDK servicing release.

## Publish a release

Run the normal tests locally, commit the release notes and version-related docs,
then create and push the tag:

```powershell
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
