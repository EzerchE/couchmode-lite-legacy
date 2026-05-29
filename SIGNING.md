# Code signing setup (SignPath Foundation)

Release builds are signed for free through the [SignPath Foundation](https://signpath.org)
open-source program. Signing happens automatically in GitHub Actions
(`.github/workflows/release.yml`): the workflow builds `CouchMode.exe`, submits
it to SignPath, and publishes the signed binary to the GitHub Release.

## One-time setup

1. **Register the project**
   - Sign up at https://app.signpath.io and request the Foundation (open-source) plan.
   - Submit this repository for review. Approval is done by SignPath staff.

2. **Configure the project in SignPath**
   - Create a **project** (suggested slug: `CouchMode`).
   - Add an **artifact configuration** that points at `CouchMode.exe`
     (suggested slug: `exe`).
   - Create a **signing policy** for releases (suggested slug: `release-signing`).
   - Connect this GitHub repository as a trusted build origin and install the
     **SignPath GitHub App** on it.

3. **Add the API token to GitHub**
   - In SignPath, create a CI user / API token.
   - In the repo: Settings -> Secrets and variables -> Actions -> New repository
     secret, named `SIGNPATH_API_TOKEN`.

4. **Fill in the workflow**
   - In `.github/workflows/release.yml`, replace the `TODO` values:
     `organization-id`, and confirm `project-slug`, `signing-policy-slug`, and
     `artifact-configuration-slug` match what you created in SignPath.

## Cutting a signed release

```bash
# bump the version in src/CouchMode.cs and app.manifest first, then:
git tag v1.3.3
git push origin v1.3.3
```

The workflow runs on the tag, signs the binary, and creates the release with the
signed `CouchMode.exe` plus a `.sha256` checksum.

## References

- SignPath open-source program: https://signpath.io/solutions/open-source-community
- Foundation terms: https://signpath.org/terms.html
- GitHub Action: https://github.com/signpath/github-action-submit-signing-request
