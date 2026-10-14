# Releasing

Pushing builds the mod and runs the tests. Releasing is a manual workflow that creates a GitHub release and then publishes to [Hexium](https://valheim.hexium.gg/) only. Vigil is on Hexium so that one group's mod manager can install it and keep it in step through its profile sync; it is not published anywhere else.

## One-time setup

The icon lives at `packaging/icon.png`: a 256x256 PNG, committed to the repository. The release workflow stops if it is missing, and the package check stops if it is the wrong size.

The Hexium token is a repository secret, `HEXIUM_TOKEN`, created in the Hexium team settings (the settings icon next to your username, then the bottom of the page). The workflow checks for it before building.

Hexium hides a new upload until the uploader makes it visible, unless that is switched off in the account settings. A hidden version is not in the list mod managers read, so the group's mod manager cannot install it until it is made visible on Hexium.

## Cutting a release

1. Set the new version in `Vigil.csproj` (`<Version>`). It is the only place: the plugin's version is generated from it, and the workflow writes it into the package's manifest.
2. Add a `## <version>` entry to `CHANGELOG.md`. It ships in the package and appears on the mod page; the workflow checks it is there.
3. Push, and wait for the Build workflow to pass.
4. Run the Release workflow from the Actions tab. It asks for the version (without `v`), optional release notes, the draft flag, whether to publish to Hexium, the team, and the categories.

The workflow then runs these steps in order:

| Step | |
|---|---|
| Version check | `<Version>` in `Vigil.csproj` matches the input, and `CHANGELOG.md` has its entry. |
| Icon check | `packaging/icon.png` exists. |
| Token check | `HEXIUM_TOKEN` is set, when publishing. |
| Build | Against `lib/`, so no game install is needed. |
| Tests | Everything in `tests/Vigil.Tests`. |
| Package | One zip with the manifest (its version set from the csproj), icon, README, CHANGELOG, LICENSE and the DLL, all at the root. |
| Package check | The zip against the store's rules: required files at the root, icon exactly 256x256, name of letters, digits and underscores, description of at most 256 characters. |
| GitHub release | Tags `vX.Y.Z` and attaches the zip and the DLL. |
| Hexium | Uploads and submits the version, when publishing. |

If the publish step fails, fix the cause and run the workflow again with the same version. The GitHub release is updated rather than duplicated, and Hexium refuses a version it already has.

## Categories

Hexium takes category names. The defaults are Debugging, Performance, Networking, Tools, Open Source and Valheim 1.0; the current list is at `https://hexium.gg/api/experimental/community/valheim/category/`.

## Install location

Vigil runs on the players' games and on the dedicated server. Most of what it shows an admin comes from the server's copy, and the server's copy only records and answers.

## How the publish works

`tools/publish.py` uses the standard library only:

1. `POST /api/experimental/usermedia/initiate-upload/` returns an upload UUID and a presigned URL per part.
2. `PUT` each part, keeping the returned `ETag`.
3. `POST /api/experimental/usermedia/{uuid}/finish-upload/` with those ETags.
4. `POST /api/experimental/submission/submit/` with `author_name` set to the team, `communities` set to `["valheim"]` and the categories.

For authorization the script tries `Bearer` first and falls back to `Token`, printing which one the server accepted. It can also be run by hand:

```bash
HEXIUM_TOKEN=... python3 tools/publish.py dist/hexium/Vigil-0.20.2.zip --store hexium --team isimp --categories "Debugging,Performance,Networking,Tools,Open Source,Valheim 1.0"
python3 tools/publish.py dist/hexium/Vigil-0.20.2.zip --check-only   # validate only
```

## After a game update

Regenerate the reference stubs, rebuild and commit `lib/`:

```powershell
pwsh tools/strip-references.ps1
```

The script reads the installed Valheim and BepInEx, replaces every method body with `throw null` and writes the result to `lib/`. The stubs can be compiled against but not run. If you add a reference to `GameReferences.props`, add it to the list in the script as well.
