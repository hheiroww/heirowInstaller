# Windows graphics startup fallback

The Windows installer can present its setup workflow with the CPU when Project-Z
cannot initialize the graphics device. `--software-rendering` selects this path
directly. Automatic fallback is limited to known MonoGame device, adapter, SDL
platform, SDL window, and graphics-system initialization failures.

`GraphicsStartup.Run` attempts the hardware callback once. Its owner must dispose
the failed `Game` before the callback unwinds and mark `contentStarted` at the very
start of `LoadContent`. The software callback then runs once. A software error,
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

References: [MonoGame Native graphics-device initialization](https://github.com/MonoGame/MonoGame/blob/5e9fe9b100ffa67026dff97cc8adffae1414534e/MonoGame.Framework/Platform/Native/GraphicsDevice.Native.cs),
[MonoGame Native SDL window](https://github.com/MonoGame/MonoGame/blob/5e9fe9b100ffa67026dff97cc8adffae1414534e/native/monogame/sdl/MGP_sdl.cpp).
