# Node.js example 🟢

`naplps_demo.js`, uses the [`koffi`](https://koffi.dev) FFI package. No native build, no node-gyp, no C compiler. `koffi` ships prebuilt native addons for win/mac/linux. 82 lines.

## Prerequisites 📋

- NAPLPS library published to `../publish/` (run `pwsh ../publish.ps1`).
- Node.js 18 or later (tested with 22 LTS).

## Install + run 🚀

```bash
npm install
node naplps_demo.js ../../../Examples/telidraw/hello.nap hello.png
```

## Expected output ✅

```
NAPLPS library version: 0.12.0
Loaded ../../../Examples/telidraw/hello.nap (35 bytes)
Parsed 22 commands, 0 errors
Wrote hello.png (6709 bytes, 1024x768)
```

## Use cases 💡

- Express / Fastify middleware that renders uploaded `.nap` files to PNG on request.
- Electron desktop app with a NAPLPS preview pane backed by the native library.
- Scheduled batch jobs in Node-based pipelines (GitHub Actions, AWS Lambda with a bundled DLL).

See [../README.md](../README.md) for the full comparison table.
