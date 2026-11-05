# Registered publisher trust

Protected components require an independently trusted outer package signature
before their signed component-key delegation is evaluated. An explicit
`TrustedPublisherKeySha256` or the existing installation receipt takes precedence.
For a fresh official heirowSnap, FruityLoopsCollab, heirowWebUI, heirowInstaller or
heirowUpdater installation, Core can use its embedded reviewed SocketJack
publisher identity and public key. The embedded SPKI must match the fixed
`eab5ba01a94557088fd7b23d9569b2f289368964e9d4922a79970ecb13409e22` anchor.

This registry is part of the installer code. Working-directory files, adjacent
publisher JSON, package labels, update URLs and self-declared `outerPublisherPin`
values cannot register a key. Core verifies the original package signature
against the selected anchor and validates delegation before retaining that
effective pin and signature requirement for worker handoff. Custom protected
publishers continue to supply their own independent pin.

Older released installers do not acquire this behavior from a package or registry
file. A repaired installer/runtime carrying this Core change must be distributed,
or an already trusted current installer must receive the independent publisher
pin. `HeirowInstaller.Trust.Tests` checks fresh state, immutable registry copying,
forged identities/delegations, explicit and receipt precedence, custom publishers,
adjacent-file rejection and absence of filesystem effects during planning.
