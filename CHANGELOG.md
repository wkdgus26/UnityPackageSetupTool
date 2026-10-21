# Changelog

All notable changes to this package are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this package follows [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-10-22

### Added
- Package presets stored as JSON in a shared folder outside the project (default `<Documents>/PackageSetupTool`), shared by every project on the same PC. Presets are created, renamed, duplicated and deleted from the preset tabs.
- Package list collection from Unity Registry, Asset Store "My Assets" purchases and installed Git packages.
- Install status badges in the package list: **Installed**, **Imported**, and **Installed (dependency)** for packages that are only a dependency or included with the Editor.
- Registering the packages installed in the current project to a preset, choosing them in a selection window (packages already in the preset are shown but cannot be removed there). Built-in and local packages are excluded, and packages from scoped registries are marked.
- Adding Git packages by URL with the add button or Enter, with a check that the URL points to a UPM package (`?path=` and `#revision` supported, `package.json` search inside the repository).
- Applying a preset: UPM install (one package at a time), Asset Store download and import, resumed after domain reloads and editor restarts.
- Install status and update check before applying, with a reinstall selection window.
- Per-item progress overlay while applying. **Stop** asks for confirmation and ends applying right away: downloads in progress are cancelled, and a package whose install or import Unity has already started is marked **Needs check** because Unity may still finish it.
- A progress row below the package toolbar while refreshing the list, checking installed packages or install status, or checking a Git URL, with per-step details (for example Asset Store descriptions 120/220).
- Removing the tool from the project after setup: asked when closing the progress overlay of a fully installed preset, or with **Package Setup > Remove Package Setup Tool**. A progress bar is shown while removing.
- Dialogs (apply, reinstall and register selection, and confirmations) open centered on the Package Setup Tool window.
- Error and reason texts (progress overlay, Git URL messages, shared folder errors, dialog notices) can be selected and copied.
- Korean and English UI.
- Installation as a UPM package from a Git URL or as a `.unitypackage`.
