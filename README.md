<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="branding/logo/lockup-dark.svg">
    <img src="branding/logo/lockup-light.svg" alt="Kiln" height="80">
  </picture>
</p>

<p align="center">Mod framework and launcher for Enshrouded.</p>

**Status: pre-release.** Things change quickly, and some features are still being built. Guides and status: [the wiki](https://frostdev.io/kiln/wiki).

- **C# mods** with hot reload, events, configs, commands and dependencies.
- **Server and client.** Runs on dedicated servers (including Linux) and in the game.
- **Safe by default.** Unknown game builds leave Kiln's hooks and C# mods off.
- **Other frameworks.** EMF 0.6.0 / API v27, EML, ShroudForge and more, with [documented compatibility limits](docs/kiln/engines/README.md).

## Downloads and mods

Get the launcher from [frostdev.io](https://frostdev.io/kiln#download), [GitHub Releases](https://github.com/frostdev-ops/kiln/releases), or the [Nexus Files tab](https://www.nexusmods.com/enshrouded/mods/301?tab=files) when available. The Nexus distribution has no launcher self-updater.

**0.2.0-dev.20:** Nexus Catalog, account sign-in and Mod Manager Download (`nxm://`) are disabled while OAuth approval is pending. Download a mod archive in your browser, then drop it on the launcher or import it through Library. Personal API keys are not a fallback. Client UI, custom portraits, taming and joined-multiplayer compatibility still need game checks; API coverage is not a guarantee for every mod combination.

## Docs
- [Wiki](https://frostdev.io/kiln/wiki)
- [Writing mods](docs/kiln/modding/README.md)
- [Architecture](docs/kiln/architecture.md)
- [Building](docs/kiln/building-on-linux.md)
- [Forge comparison](docs/kiln/forge-parity.md)

## License
MIT core; separate GPL components retain their licences and source. See [CREDITS](CREDITS.md).

Enshrouded is a trademark of Keen Games GmbH. Kiln is unofficial.
