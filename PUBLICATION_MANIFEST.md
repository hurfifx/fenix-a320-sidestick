# Publication Review Manifest

GitHub owner: `hurfifx`

Repository name: `fenix-a320-sidestick`

Visibility: public

## Included

- `FsCopilot-main/`: FS Copilot source from the r98 handoff, filtered to remove build outputs, logs, compiler tracking files, prebuilt WASM, package outputs, and unrelated preview images.
- `FsCopilot-main/LICENSE`: upstream MIT license retained.
- `UPSTREAM_README.md` copied from the upstream FS Copilot README for original project attribution.
- `release/Definitions/FNX_32X.yaml`: newly prepared minimal sidestick variable fragment; not the full Fenix profile.
- Root `README.md`, `MODULE_SETUP.md`, `.gitignore`, this manifest, and `PUBLICATION_FILE_LIST.txt` (the complete staged file inventory).

## Excluded

- The ready-to-install r98 client, publish directories, bridge package binaries, and all other `artifacts/` contents from the handoff.
- Prebuilt `.wasm`, `.dll`, `.exe`, `.pdb`, `.obj`, `.recipe`, `.tlog`, `.log`, and MSBuild state files.
- The unused `dual_input.wav` client-side sound asset; the feature uses the Fenix in-simulator aural path.
- All complete aircraft profiles and unrelated aircraft definitions.
- The full Fenix aircraft profile and aircraft package/assets.
- SDK files, local Visual Studio targets overlays, personal machine paths, simulator logs, and user configuration.
- `Dual sidestick logic v1.1` Python/MobiFlight reference files and supplied Airbus PDFs.

## Review notes

- The upstream source identifies FS Copilot as the project by Yury Scherbakov and includes its MIT license. Both attribution and license are retained.
- The original workspace has no `.git` metadata at the root or in the r98 handoff, so this staged repository will have a new, clean Git history.
- The r98 throttle hold remains marked experimental and unconfirmed in the README.
- The owner approved this repository name and publication set before creation and push.
