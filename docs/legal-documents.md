# Privacy Policy and Terms and Conditions

Every new installer requires both actual documents. Type or paste the text in the desktop **Build & publish → Privacy & terms** editor or the signed-in publisher management page. Importing a UTF-8 `.txt`, `.md` or `.markdown` file is another way to fill the same text field. Saved per-product drafts can autofill the editor. A blank document prevents creation and publication.

Each document is limited to 65,536 UTF-8 bytes. The package's signed `manifest.json` contains the actual text, its exact byte count and SHA256, file name, media type and public URL. No HTML or JavaScript is executed. Markdown is shown as text in the native installer. A privacy notice is not blanket consent to data processing.

```json
{
  "legalDocuments": {
    "privacyPolicy": {
      "url": "https://socketjack.com/heirowInstaller/PrivacyPolicy/example.app",
      "fileName": "privacy-policy.txt",
      "mediaType": "text/plain",
      "content": "Supply this application's actual Privacy Policy."
    },
    "termsAndConditions": {
      "url": "https://socketjack.com/heirowInstaller/TermsAndConditions/example.app",
      "fileName": "terms-and-conditions.txt",
      "mediaType": "text/plain",
      "content": "Supply this application's actual Terms and Conditions."
    }
  }
}
```

These example strings describe required input; they are not ready-to-publish policies. Authors supply documents that match their application. heirowInstaller does not invent third-party policies. New builders compute `size` and lowercase `sha256` from the exact UTF-8 content without changing line endings. Readers verify those values. Existing published packages without `legalDocuments` remain readable, installable and repairable.

The authoring JSON can be part of your application manifest or a separate file:

```text
heirowInstaller.Cli pack installer.json --source published --output app.hinst --legal-documents legal-documents.json --sign-key publisher.pem
heirowInstaller.Cli build installer.json --source published --output Setup.exe --legal-documents legal-documents.json --sign-key publisher.pem
heirowInstaller.Cli publisher-legal example.app --token-env SOCKETJACK_PUBLISHER_TOKEN
heirowInstaller.Cli pack installer.json --source published --output app.hinst --autofill-legal --token-env SOCKETJACK_PUBLISHER_TOKEN --sign-key publisher.pem
```

`--privacy-policy-file` and `--terms-and-conditions-file` import text; optional matching `--privacy-policy-url` and `--terms-and-conditions-url` supply public links. MSI/EXE imports use the same options. Supply `--id` for file import or saved-draft lookup without an application manifest.

Saving edited publisher text does not change an existing signed installer. Rebuild and sign the package with the new documents before publishing. The publisher rejects missing documents and mismatches with the selected saved draft. A new incremental update may use an existing legacy base, but its newly signed target must contain both documents; the existing signed target manifest carries them.

SocketJack hosts successful publications at `/heirowInstaller/PrivacyPolicy/APPID` and `/heirowInstaller/TermsAndConditions/APPID`. The management page shows these URLs alongside the editable drafts. Public pages reflect the last successful publication. The generic SDK also accepts another publisher's absolute HTTPS document URLs.

The SDK's `PublisherClient.GetLegalDocumentsAsync(productId)` and `SaveLegalDocumentsAsync(productId, documents)` use `GET`/`PUT /api/installer/publisher/apps/{productId}/legal`. Native saves include the SHA256 of the exact request body and use the current publisher account. Drafts can remain incomplete while editing; creating or publishing requires both nonblank documents. Each published document has `url`, `fileName`, `mediaType`, `content`, `size` and `sha256`.

The WiX compiler preserves the original external toolchain. Installer-producing compilation requires `--id` and legal input, and emits an explicitly **nonembedded intermediate** `.legal.json` beside the unchanged MSI/Burn output. Import/wrap that result with the same legal input to create a publishable signed heirowInstaller package. A sidecar is not a claim that the raw MSI/Burn artifact embeds those texts. Merge modules and WiX libraries remain compiler objects.

The toolkit's own public documents are [Privacy Policy](https://socketjack.com/heirowInstaller/PrivacyPolicy/heirowinstaller) and [Terms and Conditions](https://socketjack.com/heirowInstaller/TermsAndConditions/heirowinstaller). The source inputs for its own release live in `legal/heirowinstaller/`; the release builder binds them into the signed package.
