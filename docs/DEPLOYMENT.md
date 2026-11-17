# MOIRAI — Build & Deployment

## Build a portable release

```
powershell publish.ps1            # uses the version in Moirai.App.csproj
powershell publish.ps1 -Version 1.1.0
```

Produces:
- a folder: `Moirai.App/bin/portable/win-x64/`
- a zip: `MOIRAI-<version>-portable-win-x64.zip`

It is **self-contained**: the .NET runtime, Avalonia and all libraries are inside the folder
as plain DLLs (it is a folder of loose DLLs, **not** a single bundled exe). No .NET install,
no admin, no internet and **no temp extraction**. The loose-DLL form is the most compatible
with locked-down IT and antivirus — nothing is bundled or packed, just ordinary signed-by-
Microsoft framework DLLs plus the app DLLs. The folder has many files; copy the whole folder.

> A tidier single-file profile also exists (`portable-single-win-x64`): it bundles the managed
> DLLs into the exe, leaving ~8 items. It runs with no temp extraction either, but a folder of
> loose DLLs is the safer default for very strict machines, so `publish.ps1` uses it.

## Run on a locked-down company PC

1. Copy the zip to the machine (USB or the internal GitLab).
2. Right-click the zip → **Properties → Unblock** (if Windows tagged it as downloaded), then extract.
3. Put the folder somewhere **writable** (Desktop, a user folder, a USB drive) — not Program Files.
4. Run `Moirai.App.exe`.

The session file `moirai.json` is written next to the exe, so the data travels with the
folder. No installer, no registry, nothing to uninstall.

## Auto-update (download → replace → restart)

At startup the app reads `update-config.json` (next to the exe) and a small
`latest-version.json` manifest from one or more sources, tried in order. A machine with
internet hits the private GitHub repo; an offline company machine falls through to the
internal GitLab. If a newer version exists, a window opens with the changelog and an
**Apply & restart** button: the app downloads the new portable zip, replaces itself (a tiny
temp batch waits for exit, extracts the zip with the built-in `tar.exe`, restarts) and comes
back on the new version. "Later" just closes the window.

Edit `update-config.json` per machine:

```json
{
  "manifestPath": "latest-version.json",
  "sources": [
    { "kind": "github", "owner": "Erdemes", "repo": "MOIRAI", "ref": "main", "token": "<read-only PAT>" },
    { "kind": "gitlab", "baseUrl": "https://gitlab.company.local", "projectId": "123", "ref": "main", "token": "" }
  ]
}
```

- **GitHub token:** a fine-grained Personal Access Token, read-only, `Contents: read` (and the
  release assets are read with the same token). The repo is private, so the token is required;
  it ships in this local file (acceptable — read-only, single repo) and is never compiled in.
- **GitLab:** on the intranet the project is public, so leave `token` empty. Set `projectId`
  and `baseUrl` to your internal GitLab. The GitLab project holds **only releases** (no source):
  the app reads the latest one straight from the Releases API — no manifest file is committed
  there. `ref` is ignored for GitLab.
- On company machines with no internet, the GitHub source simply fails and the app falls
  through to GitLab automatically (you can also remove the GitHub source).

### The manifest

- **GitHub** reads a committed `latest-version.json` at the repo root:

  ```json
  {
    "version": "1.1.0",
    "url": "https://github.com/Erdemes/MOIRAI/releases",
    "notes": "Short summary.",
    "downloadUrl": "",
    "changelog": ["Added X.", "Fixed Y."]
  }
  ```

  Leave `downloadUrl` empty — the app finds the `.zip` asset of the release tagged
  `v<version>` (or `<version>`) automatically and downloads it with the token.
- **GitLab** needs **no manifest file**. The app derives everything from the newest release:
  the **tag** is the version, the **description** is the changelog, and the first **`.zip`
  asset link** is the download. Just publish a release with a `.zip` asset attached.

## Cutting a new version

1. Bump `<Version>` in `Moirai.App/Moirai.App.csproj` and add a `AboutViewModel.BuildChangelog` entry.
2. `powershell publish.ps1` → produces `MOIRAI-<version>-portable-win-x64.zip`.
3. **GitHub:** create a release tagged `v<version>`, attach the zip, then update
   `latest-version.json` (new `version`, `changelog`).
4. **GitLab:** create a release tagged `v<version>` with the changelog in the description and
   the zip attached as a release **asset link** (a `.zip`). Nothing else to edit — the client
   reads the tag/description/asset directly. Clients offer the update at next startup.

## Antivirus / SmartScreen

The build is **unsigned** (no code-signing certificate yet). The version *check* only reads a
string, but **Apply & restart downloads a zip and runs a small batch helper that overwrites
the install** — legitimate, but the kind of behaviour antivirus heuristics watch. Mitigations:
- Distribute via the internal GitLab; **Unblock** the zip before extracting.
- Ship as a folder (already the case), not an installer; the helper uses only in-box Windows tools (`tar`, `cmd`).
- If a machine blocks the self-update, use the manual path: download the new zip and extract it over the folder yourself.
- When a code-signing certificate is available, sign `Moirai.App.exe` with `signtool` after publish (add the step to `publish.ps1`); that clears the warning and raises antivirus trust.
