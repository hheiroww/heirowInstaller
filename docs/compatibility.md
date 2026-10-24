# Installer compatibility

heirowInstaller builds standalone native `.exe` installers and provides an explicit WiX compiler backend for existing `.wxs` and `.wixproj` authoring. Its native package engine and Windows Installer adapter remain distinct installation backends. MSI packages run through Windows Installer, retaining their authored custom actions, component identities, upgrade codes, repair rules, service configuration, and prerequisites.

WiX is an authoring toolchain for several package types and extensions. Its scope includes MSI, merge modules, patches, bootstrapper chains, IIS, SQL, and firewall authoring; see the [official WiX overview](https://docs.firegiant.com/wix/). Full WiX source-language or extension parity must not be inferred from a new setup interface.

| Capability | Native package | Windows Installer / WiX backend |
| --- | --- | --- |
| File payload, hashes, feature selection | Core package engine | MSI feature/component engine |
| Install, repair, uninstall, major version upgrade | Receipt-based operations | Windows Installer operations |
| Registry, shortcuts, environment, Windows services | Declarative modules | Existing MSI tables and custom actions |
| Runtime prerequisites | Hash-verified prerequisites | Existing authored MSI behavior |
| Full-package update download and rerun | Update feed and SHA-256 verification | Same installer delivery, MSI applies its upgrade rules |
| Existing custom actions, IIS/SQL/firewall extensions | Explicit trusted modules | Compile through the matching official WiX extension; authored actions stay in the MSI |
| `.wxs`/`.wixproj` compilation | Separate build-time compiler backend | Original WiX compiler / project SDK, with additional compiler/MSBuild arguments |
| `.msp` patch and `.msm` merge-module generation | Separate build-time compiler backend | WiX authoring produces real MSP/MSM; merge modules are merged into an MSI rather than installed alone |
| Complete setup without installed heirowInstaller | Self-contained launcher, verified package, complete application payload | Generated MSI uses Windows Installer; an optional outer setup embeds it |
| Optional app-local updater | Root-contained companion and pinned generated configuration | Outer companion feature stays separate from original MSI features |
| VSIX installation | Keep the Visual Studio extension package contract | VSIX stays a VSIX |

Replacing current public download buttons with a heirowInstaller executable can preserve the original payload inside it. That changes the setup and delivery experience; it does not turn an embedded MSI into a native package. Older updater endpoints remain available where installed clients require MSI filenames or invoke `msiexec` directly.

The compiler backend runs the separately selected WiX toolchain. It does not translate WiX XML into a limited native subset. The selected compiler version, extensions, SDKs, custom-action projects, native prerequisites, and their licenses determine what the authoring can build. Current SDK-style projects use `dotnet build`; legacy projects can use an explicit full-framework MSBuild. Legacy v3 XML requires its matching v3 toolchain/project or an explicit WiX conversion. [Authoring commands and requirements](authoring.md) describe these routes.

Never rename EXE content to `.msi`. Keep old artifacts available for rollback and installed-client compatibility. Promote a new current installer only after payload hashes, signature policy, the setup executable, and the product's update contract have been verified.
