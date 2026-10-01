# Module Setup and Integration

## Repository layout

- `FsCopilot-main/FsCopilot`: FS Copilot desktop client source, including the A320 UI and view model integration.
- `FsCopilot-main/FsCopilot/Simulation/FenixSidestickCoordinator.cs`: joystick capture, peer input/output packets, summed control, dual-input annunciation, priority takeover, and Fenix bridge writes.
- `FsCopilot-main/FsCopilot/Simulation/FenixSidestickProfileState.cs`: persisted consent, controller, curves, inversion, and module settings.
- `FsCopilot-main/FsCopilot/Simulation/FenixSidestickSessionState.cs`: per-client and remote module state.
- `FsCopilot-main/FsCopilot.Bridge.Wasm`: bridge WASM C++ source and Visual Studio project.
- `FsCopilot-main/FsCopilot.Bridge/PackageSources/HTML_UI`: bridge HTML/JS source.
- `release/Definitions/FNX_32X.yaml`: minimal sidestick variable fragment, not a full aircraft profile.

The feature is woven into existing FS Copilot types (`MainViewModel`, `MainWindow`, `SimClient`, `Coordinator`, `MasterSwitch`, and the networking layer). For another project, port those bindings and lifecycle hooks; copying only `FenixSidestickCoordinator.cs` is insufficient.

## Client build

1. Install the .NET 9 SDK and Visual Studio 2022 C++ components.
2. Obtain the managed and native SimConnect DLLs from the locally installed MSFS SDK.
3. Place local copies where `FsCopilot-main/FsCopilot/FsCopilot.csproj` expects them, or change the project references to point to your SDK installation. Do not check SDK DLLs into Git.
4. From the repository root, publish the client:

```powershell
dotnet publish .\FsCopilot-main\FsCopilot\FsCopilot.csproj `
  -c Release -r win-x64 -o .\artifacts\client --self-contained true
```

The project includes definitions from `release/Definitions`. The fragment in this repository is intentionally minimal and must be merged into a complete Fenix definition for a full FS Copilot installation.

## Build the MSFS 2024 bridge

Build from the project file, not from an output/package folder. Set SDK paths for your local machine before invoking MSBuild. A Visual Studio targets overlay may be required so MSBuild can resolve the custom `MSFS2024` toolset.

```powershell
$env:VCTargetsPath = '<path-to-your-MSBuild-targets-overlay>\'
$env:MSFS2024_SDK = '<path-to-MSFS-2024-SDK>\'
$env:MSFS_SDK = '<path-to-MSFS-2024-SDK>'
$env:MSFS_IncludePath = '<sdk>\WASM\include;<sdk>\SimConnect SDK\include'
$env:MSFS_LibraryPath = '<sdk>\WASM\lib;<sdk>\WASM\wasi-sysroot\lib\wasm32-wasi;<sdk>\SimConnect SDK\lib\static'

& '<path-to-Visual-Studio>\MSBuild\Current\Bin\MSBuild.exe' `
  '.\FsCopilot-main\FsCopilot.Bridge.Wasm\FsCopilotWasm.vcxproj' `
  /p:Configuration=Release /p:Platform=x64 /p:PostBuildEventUseInBuild=false /v:minimal
```

This snapshot declares `Release|x64` in the project; its `MSFS2024` toolset still generates WASM, not a native x64 bridge. Use the `.vcxproj` directly rather than the solution's stale MSFS configuration mappings.

The SDK's `WASM\llvm\bin\clang-cl.exe` and `wasm-ld.exe` can be used as a manual compile/link fallback if the MSBuild toolset import fails. Use the SDK's WASI sysroot, MSFS WASM headers/libraries, SimConnect headers/libraries, and `WASM\WasmVersions\MSFS_WasmVersions.a`. Do not copy the SDK or machine-specific overlay into this repository.

After a successful build, confirm the generated `FsCopilot.wasm`, then put it into the bridge package at `modules/FsCopilot.wasm` and refresh the package `layout.json` size/date fields when packaging. This repository omits compiled WASM and package outputs by design.

## Control behavior

- The module arms only for recognized Fenix A319/A320/A321 aircraft and when enabled on both connected clients with the bridge ready.
- Each client captures its selected local joystick. The master computes merged controls and priority state; the opposite sidestick is ignored while temporary or latched priority is active.
- Priority is temporary while the takeover button is held. Holding it for more than 40 seconds latches priority; pressing either takeover button reactivates or transfers authority according to the implementation.
- When neither pilot has priority, sidestick orders are summed and bounded. Dual input is based on concurrent stick deflection and drives the Fenix annunciation path.
- Captured master mode sends axis events through the bridge and avoids raw aileron/elevator surface writes, preserving Fenix flight-law handling.

## Handoff caveats

- The r98 version includes a local throttle-hold experiment which the user reported did not visually lock the slave throttle. This is unconfirmed and should be reviewed before reuse.
- The master-only throttle definition prevents the slave from publishing its throttle LVARs to the master; it does not mechanically lock the slave's local throttle hardware.
- The full Fenix aircraft definition, Fenix assets, manuals, MobiFlight profile, and simulator-generated package are not included. Merge the snippet into a complete definition that you have rights to use.
