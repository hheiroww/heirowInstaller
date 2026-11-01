# Windows publisher signing

Windows shows a verified publisher when an executable has a valid Authenticode signature from a certificate Windows trusts. The RSA package key used by `--sign-key` protects heirowInstaller packages and updates; it does not establish Windows publisher trust. Use both signatures when publishing a signed package inside a Windows-signed Setup.

## Signing and SocketJack hosting are separate

Windows publisher signing uses **your own** purchased certificate or Microsoft Artifact Signing account. Signing into SocketJack grants installer hosting and update-feed access only. It never grants access to SocketJack's Microsoft signing account, certificate profiles, private keys or signing service. The builder includes no shared Microsoft account or signing credentials. Bundling a new application removes the toolkit launcher's old Windows signature; your completed Setup must be signed with your identity.

Windows signing is currently optional on SocketJack while publisher onboarding is in progress. Windows uploads without verified Authenticode are labeled **Windows publisher unverified**. RSA package integrity, legal documents and hosting authorization still apply. The client reads the server hosting policy; neither a hosting login nor a workflow approval provides Windows signing.

When an administrator enables Windows signature enforcement, new Windows uploads require a trusted Authenticode-signed `Setup.exe`. A Windows `.hinst` alone cannot satisfy that mode. `.hupd` uploads bind the exact full target installer and follow the same configured requirement. Existing hosted versions remain downloadable.

This requirement applies **only to Windows apps**. The signed manifest's `targetPlatform` defaults to `"windows"` for legacy packages. Non-Windows `.hinst` packages explicitly declare `"linux"`, `"macos"`, `"freebsd"`, `"android"`, `"ios"` or `"other"` and do not need Windows Authenticode. They retain package-integrity and hosting authorization checks; their platform-specific signing policies are separate. A Windows Setup.exe or Windows executable entry point cannot opt out by claiming another platform. The current Setup.exe runtime supports Windows installation; this metadata does not add a Linux or macOS installer runtime.

Signing also differs from Microsoft Defender SmartScreen reputation. New downloads can still show reputation warnings after successful signing. A self-signed certificate does not establish public Windows trust, and EV certificates no longer guarantee an immediate SmartScreen bypass. See Microsoft's [signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options) and [SmartScreen guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).

## Choose a signing provider

| Provider | What you need | Cost and scope |
| --- | --- | --- |
| Windows certificate | A trusted code-signing certificate and accessible private key in the Windows certificate store, including a provider-backed token or cloud key | Your certificate provider sets the price and identity checks |
| Microsoft Artifact Signing | An existing Azure Artifact Signing Public Trust account, validated identity, certificate profile and signer permission | Basic service is currently $9.99/month for up to 5,000 signatures; a paid Azure subscription is required |
| None | No Windows signing account | Setup can be built, but this does not make its publisher verified |

Installing signing tools does not enroll a publisher or buy a certificate. heirowInstaller does not create Azure subscriptions, provision paid resources, purchase certificates or complete identity verification. [Microsoft pricing](https://azure.microsoft.com/en-us/products/artifact-signing), [Azure subscription requirements](https://learn.microsoft.com/en-us/azure/artifact-signing/faq).

## Use the desktop builder

Open **Build & publish → Windows signing**. The **Windows publisher signing** dialog offers **Not signed**, **Purchased certificate** and **Microsoft Artifact Signing**. Choose a provider before building a standalone `.exe`.

For **Purchased certificate**, select **Current user** or **Local computer**, then **Choose certificate**. To import a purchased `.pfx`/`.p12`, select **Import certificate** and complete the Windows wizard. Enter the password only in that wizard. A hardware token or cloud certificate may instead require your certificate provider's middleware and PIN prompt. The signing process must have access to its private key.

For **Microsoft Artifact Signing**, enter the account's regional endpoint, account name and certificate profile name from Azure. **Microsoft setup** opens Microsoft's prerequisites; **Get signing tools** opens its client-tool instructions. Authenticate using your existing approved Azure environment before building. Account/profile names and endpoints select the signing identity; they are not account credentials.

**Tools & timestamp** contains optional tool paths and a timestamp-service override. **Check settings** checks local tool and certificate availability without making a cloud signing request. **Save & use** saves the nonsecret profile for future builds. Private keys and passwords stay outside the profile.

Build the setup after configuring signing. A requested signing failure fails the build; it does not silently produce an unsigned release. The completed Setup is signed after its payload has been appended, and its signature and embedded package are checked before success is reported.

Signed application setups are cached in full for later repair, uninstall and administrator prompts. Truncating a Setup to its launcher would invalidate its Windows signature. This uses more disk space than unsigned setups, whose common launcher prefix can be shared between applications. Maintenance continues to use the installed application's verified package and receipt.

## Windows certificate setup

Obtain a publicly trusted code-signing certificate appropriate for your distribution. Importing a self-signed certificate into your own computer's trust store does not make other users trust it.

For command-line signing, select the certificate by its thumbprint. By default the builder uses the current user's Personal (`My`) store. Add `--machine-certificate-store` for the local machine's Personal store. Windows controls private-key permissions; machine-store selection does not grant access or automatically elevate the builder.

```powershell
heirowInstaller.Cli.exe build installer.json --source .\publish `
  --output .\dist\MyApp-Setup.exe --sign-key $env:APPLICATION_SIGNING_KEY `
  --windows-signing certificate `
  --certificate-thumbprint $env:CODE_SIGNING_CERT_THUMBPRINT
```

The thumbprint identifies the certificate and is not a password. Keep private keys, PFX files and passwords outside manifests, source folders and repositories. There is no command-line PFX-password option.

## Microsoft Artifact Signing setup

Use Microsoft's [account setup](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart) to establish a validated Public Trust identity and certificate profile. Assign the signing account the **Artifact Signing Certificate Profile Signer** role. Private Trust and test profiles are unsuitable for establishing publisher trust on public users' computers.

Install Microsoft's [Artifact Signing Client Tools](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations) on the signing machine. Their documented WinGet command is:

```powershell
winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
```

The client needs compatible Windows SDK SignTool, its `Azure.CodeSigning.Dlib.dll`, the matching .NET runtime and Visual C++ dependencies. If discovery cannot find your installation, supply `--signtool` and `--artifact-signing-dlib` with the actual matching-architecture paths.

Set the following environment variables to your existing account's endpoint and names. These examples do not create resources or authenticate an account:

```powershell
heirowInstaller.Cli.exe build installer.json --source .\publish `
  --output .\dist\MyApp-Setup.exe --sign-key $env:APPLICATION_SIGNING_KEY `
  --windows-signing microsoft `
  --artifact-endpoint $env:ARTIFACT_SIGNING_ENDPOINT `
  --artifact-account $env:ARTIFACT_SIGNING_ACCOUNT `
  --artifact-profile $env:ARTIFACT_SIGNING_PROFILE
```

The endpoint must match the Azure account's region. The builder generates temporary service metadata and uses the client's existing Azure authentication. Supply credentials through your approved Azure identity mechanism, not command-line arguments or project files. Microsoft's timestamp service is the default for this provider; timestamping preserves verification beyond its short-lived signing certificates.

Use `signing-check` with the same provider options to check local tools and certificate availability before building. It sends no signing request. Final signature verification uses Windows trust on the signing computer; use a publicly trusted certificate or a Microsoft Public Trust profile for public distribution.

## Sign an existing artifact

The `sign` command accepts an existing `.exe`, `.dll` or `.msi` and the same signing options:

```powershell
heirowInstaller.Cli.exe sign .\dist\MyApp-Setup.exe `
  --windows-signing certificate `
  --certificate-thumbprint $env:CODE_SIGNING_CERT_THUMBPRINT
```

Sign application EXEs and DLLs before packaging when you need signatures on those individual files. The standalone `build` option signs the completed Setup; it does not rewrite your supplied application's payload. An installer wrapper's signature does not sign the files it installs.

Sign the final artifact before computing its download hash, update feed, delta or release manifest. Changing a file after signing can invalidate its signature. Authenticode does not apply to `.hinst` or `.hupd`; continue using the publisher package key for those formats.

Verify an existing Windows artifact without signing credentials or a signing request:

```powershell
heirowInstaller.Cli.exe verify-windows-signature .\dist\MyApp-Setup.exe
```

The official release workflow enforces Windows signing only when the repository variable `REQUIRE_WINDOWS_SIGNATURE` is `true`. Its build job must use the certificate or Microsoft options below with the repository owner's signing identity and authorized CI authentication; no shared SocketJack profile is supplied.

## Build the toolkit release

`scripts/Build.ps1` signs the three published toolkit executables before packaging and signs the completed Setup before generating its hashes and feed. Existing `-CertificateThumbprint` invocations continue to select certificate signing:

```powershell
pwsh -File scripts/Build.ps1 `
  -CertificateThumbprint $env:CODE_SIGNING_CERT_THUMBPRINT

pwsh -File scripts/Build.ps1 -WindowsSigning microsoft `
  -ArtifactEndpoint $env:ARTIFACT_SIGNING_ENDPOINT `
  -ArtifactAccount $env:ARTIFACT_SIGNING_ACCOUNT `
  -ArtifactProfile $env:ARTIFACT_SIGNING_PROFILE
```

The script also accepts `-MachineCertificateStore`, `-SignTool`, `-ArtifactSigningDlib` and `-TimestampUrl`. The existing `-SigningKey`, `-PreviousPackage` and `-FirstRelease` requirements for package signatures and incremental releases remain separate.

## Free distribution options

Individual Microsoft Store developer registration is free, with Microsoft's identity verification. Store-submitted **MSIX** packages receive free Microsoft signing. A published free Store app can offer Microsoft's generated web installer through a Store badge on your website. This is a separate distribution route; Microsoft does not re-sign an existing EXE/MSI installer submitted to the Store. [Free individual registration](https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/), [web installer](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-use-store-web-installer-for-distribution), [EXE/MSI requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements).

[SignPath Foundation](https://signpath.org/terms.html) offers free signing to qualifying open-source projects under its eligibility and approval rules. That does not cover proprietary payloads or provide blanket signing for every application a publisher hosts.
