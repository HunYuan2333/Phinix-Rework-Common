# Shared layer documentation index

This repository owns game-independent contracts and shared code. Current checkout: branch `dev`, HEAD `85ecb18278cf98c0e4fa9ab6c49f4d7db635b413` (two commits ahead of `origin/dev`) with a local modification in `ManagedExtensionMetadata.cs`. Its protobuf submodule is pinned at `4b0c3aacf0657fbf38253b38918d3358dd4319ec`. Consumer repositories pin this repository independently; their gitlinks need not match this checkout.

- [Repository overview](../README.md)
- [Design philosophy](Design-Philosophy.md) · [Compatibility boundaries](Compatibility-Boundaries.md) · [Storage inventory](Inventory.md)

The client and server own their host-specific behavior and release flows. Shared contracts do not imply a client/server package has been built, tested, or released.
