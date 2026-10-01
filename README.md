# Fenix A320 Shared Sidestick for FS Copilot

This repository is a focused source handoff for the experimental Fenix A319/A320/A321 shared-sidestick integration in FS Copilot. The feature is currently integrated into the FS Copilot application; it is not a standalone plugin or a drop-in library for unrelated clients.

The application source is based on the upstream FS Copilot project. Its original MIT license and upstream README are retained in this repository. The A320-specific integration is described in [MODULE_SETUP.md](MODULE_SETUP.md).

## What is included

- The FS Copilot client and bridge source needed to compile and integrate the A320 feature.
- The A320 sidestick UI, profile persistence, joystick capture, network packets, priority/dual-input logic, and bridge interaction from the r98 handoff.
- A minimal Fenix sidestick definition fragment in `release/Definitions/FNX_32X.yaml`.
- Portable build and integration instructions.

## Build requirements

- Windows 10 or 11.
- Visual Studio 2022 with Desktop development with C++ and C++ Clang tools for Windows.
- .NET 9 SDK.
- Microsoft Flight Simulator 2024 SDK, including the WASM and SimConnect SDK components.
- The SimConnect managed/native DLLs required by the FS Copilot project, obtained from the installed SDK and kept local (do not commit them).

See [MODULE_SETUP.md](MODULE_SETUP.md) for client and WASM build steps, integration points, and known limitations.

## Current state and limitations

- The bridge source is the r93 law-safe bridge used by the r98 client handoff. This repository does not include a prebuilt `.wasm` file; build it from `FsCopilot-main/FsCopilot.Bridge.Wasm/FsCopilotWasm.vcxproj`.
- The r93 bridge avoids raw aileron/elevator surface writes in captured master mode so Fenix flight-law protections remain in the sim's control path.
- The r98 attempted slave-side throttle hold did not work in the user's test. Treat the local throttle-hold loop as experimental. The profile's master-only throttle publication is a separate behavior.
- The minimal definition file here is only a sidestick integration fragment. Merge it into a complete, appropriately licensed Fenix profile; it is not a replacement for the complete aircraft profile.
- No simulator runtime test is implied by the presence of source files or successful compilation.

## Attribution

FS Copilot is the upstream project by Yury Scherbakov. See [UPSTREAM_README.md](UPSTREAM_README.md) and [LICENSE](LICENSE). Fenix and Microsoft Flight Simulator are third-party trademarks and products; this repository is not affiliated with or endorsed by their owners.
