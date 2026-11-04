# Windows graphics startup fallback

The Windows installer attempts native DirectX 12, a separately compiled DirectX 11
WindowsDX host, then the CPU wizard. `--software-rendering` selects CPU directly
and skips both GPU hosts. Automatic fallback is limited to known MonoGame device,
adapter, SDL platform, SDL window, and graphics-system startup failures.

`GraphicsStartup.Run` attempts each presentation path at most once. Its owner must dispose
the failed `Game` before the callback unwinds and mark `contentStarted` at the very
start of `LoadContent`. Only the DX11 child's reserved pre-content graphics status
permits the final CPU path. Child completion, cancellation, operation errors, a software error,
content error, package verification error, or failure after content loading starts
propagates normally. The choice of presentation does not authorize installation or
change package verification, legal consent, elevation, or running-process checks.

The compatibility wizard uses Windows Forms/GDI without creating a MonoGame
window or graphics device. Setting
`GraphicsAdapter.UseDriverType = FastSoftware` is insufficient with the current
Project-Z DirectX 12 package: MonoGame's Native backend does not consume that
property. The separate WindowsDX backend maps it to Direct3D 11 WARP, but changing
this property cannot switch the compiled Native backend. Lowering `GraphicsProfile`
also leaves the Native backend's Direct3D 12 feature-level requirement unchanged.

The Native SDL window currently requests Vulkan support even for its DirectX 12
backend. Using an independent CPU interface also covers this initialization
failure. Install, repair, uninstall, license and legal documents, feature and
destination choices, cancellation, and running-application consent all use the
existing installation controller.

The DX11 host uses the reviewed Project-Z 2026.11.2.2 template runtime and
`MonoGame.Framework.WindowsDX` 3.8.5.1, with matching DX11 shader resources. Its
build excludes the Native/DX12 framework and `mgruntime.dll`. Changing a property
on the public precompiled DX12 NuGet binary cannot turn that binary into DX11.
The official VSIX's DirectX11 `ProjectZ.dll` is pinned by SHA-256
`61138878b47f176fff2a08b7e448e8757af293c605310df99427d458a561e1cd`.

`scripts/Build.ps1` produces the self-contained secondary first and signs it when
requested, then embeds those exact bytes in the primary. Builds use
`InstallerGraphicsBackend=DirectX11` and `InstallerProjectZDx11RuntimePath` for the
secondary; the primary receives `InstallerDx11FallbackExecutable` and
`InstallerRequireDx11Fallback=true`. Embedded length/SHA metadata binds the
resource. Official publishes require this embedding; explicitly unbundled
development publishes must opt in with `InstallerAllowUnbundledDevelopmentPublish`.

For bounded release preparation, `Build.ps1 -BuildOnly` builds and records the four
raw executables without copying a package payload or creating full/delta packages.
After acceptance and free-space review, `-UseRawBuild` with the same `BuildDirectory`
validates the exact source, configuration, signing policy, runtime and file inventory
before packaging. Compiler caches can be reused. Existing published inputs, payloads
and release outputs are preserved and rejected rather than merged or overwritten.

The companion is extracted into a protected current-user cache keyed by its
digest. Reparse points, wrong ownership, extra writers, changed bytes and changed
lengths fail closed. A read lease prevents replacing the executable through child
exit. Isolated diagnostics use only their declared state directory. The original
primary and package carrier stay read-locked and are bound to the parent's PID,
start time, paths and hashes. Neutral internal carrier arguments preserve the
original Install, license, launch, scope, feature and maintenance choices.

The original primary remains the elevation and maintenance host. The worker's
normal same-image check remains in force; its narrowly defined second-image case
must match the exact companion declared in the primary's own resource metadata.

References: [MonoGame Native graphics-device initialization](https://github.com/MonoGame/MonoGame/blob/5e9fe9b100ffa67026dff97cc8adffae1414534e/MonoGame.Framework/Platform/Native/GraphicsDevice.Native.cs),
[MonoGame Native SDL window](https://github.com/MonoGame/MonoGame/blob/5e9fe9b100ffa67026dff97cc8adffae1414534e/native/monogame/sdl/MGP_sdl.cpp).
