# Native runtime prerequisites

An optional `detection` object on a prerequisite checks a fixed Windows runtime registration before the existing verified executable/download path runs:

```json
{
  "id": "dotnet-desktop-10-x64",
  "name": ".NET Desktop Runtime 10 (x64)",
  "detection": { "kind": "dotnet-desktop", "architecture": "x64", "minimumVersion": "10.0.0" },
  "executable": "prerequisites/windowsdesktop-runtime-10.0.12-win-x64.exe",
  "arguments": ["/install", "/quiet", "/norestart"]
}
```

The executable must still belong to the signed, hash-verified payload. Existing HTTPS downloads retain their SHA-256 verification and existing success/reboot exit-code behavior. Detection never downloads or starts an installer. Structured detection requires package format3, and the builder promotes new manifests automatically so older engines fail closed. Old manifests omit `detection`, preserve their existing format and JSON, and retain file-only detection. When structured detection is present it is authoritative: a generic `detectFile` cannot skip a missing or incompatible runtime. Isolated validation can detect installed runtimes but refuses external installer execution.

`dotnet-desktop` reads only `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App` in the explicit 32- and 64-bit registry views. An installed stable numeric version name must have the documented DWORD value `1`. It must be at least the minimum and share its major version. Newer .NET10 patches satisfy `10.0.0`; .NET11 alone does not. Preview version names do not satisfy a stable runtime requirement. No SDK, Core-only runtime, PATH search or `dotnet` command substitutes for the desktop-runtime registration.

`vc-runtime` uses the same structure with `minimumVersion` such as `14.0.0.0`. It reads only `HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64` in both explicit registry views. DWORD `Installed=1` is required, together with a stable v14 `Version` string (an initial `v` is accepted), or the documented `Major`, `Minor`, `Bld`, `Rbld` DWORD components. Newer v14 versions meet the minimum without attempting an older redist installation. Unknown kinds/architectures and malformed minimum versions are rejected. Missing, malformed or unreadable registry evidence never grants an installed result.

The registry reader is internal, injectable for tests, and limited to two fixed locations. Manifests cannot supply registry keys, hives, shell expressions or arbitrary detection commands. Three- and four-component stable versions compare consistently. `PrerequisiteDetection.Detect` exposes only a read-only satisfaction/version result.

The registry conventions come from the [.NET installer registry source](https://github.com/dotnet/arcade/blob/main/src/Microsoft.DotNet.Build.Tasks.Installers/build/wix/product/registrykeys.wxs) and Microsoft's [VC++ redistributable documentation](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files?view=msvc-170). The required-major rule follows [.NET runtime selection](https://learn.microsoft.com/en-us/dotnet/core/versions/selection).

Run `tests/HeirowInstaller.Prerequisites.Tests` for injected registry, malformed data, version/architecture boundaries, legacy detection compatibility, cancellation and safe isolated refusal checks. Its optional receipt path also records a read-only local probe; tests do not modify installed runtimes or registry values.
