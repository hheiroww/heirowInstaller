# Web installer attachment

Publish the signed application `.hinst` through the established publisher first.
The full package remains the authoritative source for installation and file
updates. A web installer is a separate immutable carrier smaller than
20,000,000 bytes, containing the approved native launcher and a signed descriptor
bound to that exact application version, URL, digest and size. The application
package must be smaller than 100,000,000 bytes. Public toolkit dependencies are
pinned independently; private application files remain on their protected feed.

Attach the finished web installer with a native administrator publisher session:

```powershell
heirowInstaller attach-bootstrap App-WebSetup.exe --id App --version 1.0.0 --token-env SOCKETJACK_PUBLISH_TOKEN
```

`--publisher` selects another HTTPS origin; `--trust-key` optionally pins the
expected descriptor publisher. Credentials come from the named environment
variable and are sent only in the authorization header. Browser cookies and
GitHub OIDC preparation grants cannot select this native attachment operation.
The server checks product ownership, approved launcher bytes, signed descriptor,
source release binding and accounted storage before accepting the carrier.
Identical retries reuse the same immutable artifact. Attachment does not change
the source package or activate a release.

For an already registered FruityLoopsCollab native beta target, add the raw
signed application representation before attaching its web installer:

```powershell
heirowInstaller attach-beta-application application.hinst --version 1.0.0 --token-env SOCKETJACK_PUBLISH_TOKEN
heirowInstaller attach-bootstrap FruityLoopsCollab-WebSetup.exe --id FruityLoopsCollab --version 1.0.0 --token-env SOCKETJACK_PUBLISH_TOKEN
```

The raw representation must preserve the registered target's original signed
manifest and signature bytes. This operation creates no new beta release and
changes no release pointer. New beta versions retain their established native
registration and activation workflow. Download and file-update routes continue
to check the account's current beta entitlement on every request.

The publishing SDK exposes `PublisherClient.AttachWebBootstrapAsync` and
`AttachBetaApplicationAsync`, returning separate typed immutable receipts. Both
accept asynchronous `IProgress<PublishProgress>` and cancellation. The client
verifies the local signed identity, hashes and uploads one retained read lease,
refuses POST redirects, bounds response metadata, and verifies the receipt's
version, hash, size and exact same-origin download URL. A receipt for another
artifact cannot be reported as success.

`scripts/Test.ps1` runs the isolated SDK and actual CLI attachment checks and
retains their fixtures beneath its supplied output directory. To run only these
checks after building the publishing test project:

```powershell
dotnet run --no-build --project tests/HeirowInstaller.Publishing.Tests -c Release -- --bootstrap-attachment-checks C:/owned-run/attachments
```

Use a fresh absolute output directory. These tests use inert loopback identities
and signed fixtures, send no production request, and execute no uploaded binary.
