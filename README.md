# Data Center Save Editor

An unofficial, portable Windows save editor for [Data Center by Waseku](https://store.steampowered.com/app/4170200/Data_Center/).

The editor safely inspects the game's NRBF save graph without `BinaryFormatter`, exposes friendly common fields and a searchable advanced object tree, and only modifies existing scalar values. It currently supports writing save format version 8; other versions are inspection-only.

> [!IMPORTANT]
> This is an independent community project. It is not affiliated with or endorsed by Waseku, Valve, or Steam. Back up important saves and close the game before editing.

## Features

- Automatic discovery of paired `.save` and `.meta` files.
- Common-field editing for progression and root save values.
- Searchable advanced object-tree editing for numbers, strings, booleans, enum backing values, and Unity vector/quaternion components.
- Strict type, overflow, finite-number, and structural validation.
- Field-level change review before writing.
- Timestamped backups of both files before replacement.
- Transactional two-file commit with rollback after a partial failure.
- Write protection for unsupported save versions and while Data Center is running.
- Self-contained `win-x64` release; users do not need to install .NET.

## Download

Download `DataCenterSaveEditor-win-x64.zip` from [GitHub Releases](../../releases/latest), extract it, and run `DataCenterSaveEditor.exe`.

Windows may display a SmartScreen warning because community builds are not code-signed.

## Usage

1. Close Data Center.
2. Launch `DataCenterSaveEditor.exe`.
3. Select a discovered save and choose **Load selected**, or open a `.save` file manually.
4. Edit existing scalar values in the **Common fields** or **Advanced object tree** tab.
5. Choose **Save changes**, review the diff and any warnings, then confirm.

The default save location is:

```text
%USERPROFILE%\AppData\LocalLow\WASEKU\Data Center\saves
```

Backups are written beneath `SaveEditorBackups` in that save directory. The editor keeps `nameOfSave` synchronized between the binary save and its metadata file.

## Compatibility and safety

- Platform: Windows x64.
- Writable format: version 8 only.
- Advanced editing is limited to existing scalar leaves; object and collection structure cannot be added, removed, resized, or duplicated.
- The parser never loads game assemblies or instantiates serialized game types.
- No personal save files are included in this repository.

Game updates may change the save format. Keep the backup created by the editor until the edited save has loaded successfully in-game.

## Build locally

Install the .NET 10 SDK on Windows, then run:

```powershell
dotnet restore DataCenterSaveEditor.slnx
dotnet build DataCenterSaveEditor.slnx --configuration Release --no-restore
dotnet run --project tests/DataCenterSaveEditor.Tests/DataCenterSaveEditor.Tests.csproj --configuration Release --no-build --no-restore
```

To create the self-contained application:

```powershell
dotnet publish src/DataCenterSaveEditor.App/DataCenterSaveEditor.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output artifacts/publish
```

The dependency-free test executable uses synthetic NRBF fixtures. Set `DATACENTER_TEST_SAVE` to an external version-8 `.save` path to additionally run the conditional real-save integration test; that file remains outside the repository.

## GitHub Actions and releases

`.github/workflows/build-test-release.yml` builds, tests, publishes, and uploads the portable ZIP on every push and pull request.

Pushing a tag beginning with `v` also creates a GitHub Release with generated release notes and attaches the ZIP:

```powershell
git tag v0.1.0
git push origin v0.1.0
```