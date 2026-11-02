# Building RoundedTB

Requires the .NET 10 SDK and Visual Studio's C++ build tools (for `RoundedTB.Tap`, the native part of the highlight rounding). Pick the platform, `x64` or `ARM64`:

```
dotnet build RoundedTB/RoundedTB.csproj -c Release -p:Platform=ARM64
```

Unpackaged release build:

```
dotnet publish RoundedTB/RoundedTB.csproj -c Release -r win-arm64 -p:Platform=ARM64 -p:PublishTrimmed=true
```

MSIX package (unsigned; sign it, or register the unpacked layout in Developer Mode, to install it):

```
dotnet publish RoundedTB/RoundedTB.csproj -c Release -r win-arm64 -p:Platform=ARM64 -p:PublishTrimmed=true -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false
```

`<Version>` in `RoundedTB.csproj` sets the version of local builds, the MSIX included; pass `-p:Version=` to override it.

Settings live in `%LOCALAPPDATA%\RoundedTB\rtb.json`. The packaged app keeps its own copy in its app data, seeded from that file on first run.

## Releases

GitHub Actions builds releases from tags, and the tag sets the version of the app and the MSIX alike:

| Tag | Release |
|---|---|
| `vX.Y.Z` | Stable, with `RoundedTB.appinstaller` so installed copies update |
| `canary*`, or any other `v*` | Pre-release |

Each stable release must have a higher version than the last, or installed copies won't update to it.

The binaries and the `.msixbundle` are signed with Azure Artifact Signing using these secrets in the `release` environment, whose deployments are limited to `v*` and `canary*` tags:

| Secret | |
|---|---|
| `ARTIFACT_SIGNING_ENDPOINT`, `ARTIFACT_SIGNING_ACCOUNT`, `ARTIFACT_SIGNING_PROFILE` | The signing account and certificate profile |
| `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` | A service principal allowed to sign with that profile |

The certificate subject must match the `Publisher` in `RoundedTB/Package.appxmanifest`. Without the secrets, releases carry only the unsigned zips.
