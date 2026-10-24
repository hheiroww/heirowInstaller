# Installer compatibility

heirowInstaller has a native `.hinst` package format and an adapter for existing Windows Installer packages. These are distinct installation backends. Existing MSI packages continue to run through the Windows Installer engine, retaining their authored custom actions, component identities, upgrade codes, repair rules, service configuration, and prerequisites.

WiX is an authoring toolchain for several package types and extensions. Its scope includes MSI, merge modules, patches, bootstrapper chains, IIS, SQL, and firewall authoring; see the [official WiX overview](https://docs.firegiant.com/wix/). Full WiX source-language or extension parity must not be inferred from a new setup interface.

| Capability | Native package | Existing MSI adapter |
| --- | --- | --- |
| File payload, hashes, feature selection | Core package engine | MSI feature/component engine |
| Install, repair, uninstall, major version upgrade | Receipt-based operations | Windows Installer operations |
| Registry, shortcuts, environment, Windows services | Declarative modules | Existing MSI tables and custom actions |
| Runtime prerequisites | Hash-verified prerequisites | Existing authored MSI behavior |
| Full-package update download and rerun | Update feed and SHA-256 verification | Same installer delivery, MSI applies its upgrade rules |
| Existing custom actions, IIS/SQL/firewall extensions | Requires an appropriate module | Preserved inside the original MSI |
| Arbitrary `.wxs`/`.wixproj` compilation | No source-language compatibility claim | Import the already compiled MSI |
| `.msp` patch generation and `.msm` merge-module generation | No authoring compatibility claim | Retain the specialized package workflow |
| VSIX installation | Keep the Visual Studio extension package contract | VSIX stays a VSIX |

Replacing current public download buttons with a heirowInstaller executable can preserve the original payload inside it. That changes the setup and delivery experience; it does not turn an embedded MSI into a native package. Older updater endpoints remain available where installed clients require MSI filenames or invoke `msiexec` directly.

Never rename EXE content to `.msi`. Keep old artifacts available for rollback and installed-client compatibility. Promote a new current installer only after payload hashes, signature policy, the setup executable, and the product's update contract have been verified.
