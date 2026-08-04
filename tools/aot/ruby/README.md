# Ruby example 💎

`naplps_demo.rb`, uses the [`ffi`](https://rubygems.org/gems/ffi) gem. `attach_function` declarations mirror the C header. 86 lines.

## Prerequisites 📋

- NAPLPS library published to `../publish/` (run `pwsh ../publish.ps1`).
- Ruby 3 or later (tested with 4.0).

## Install + run 🚀

```bash
gem install ffi
ruby naplps_demo.rb ../../../Examples/telidraw/hello.nap hello.png
```

## Expected output ✅

```
NAPLPS library version: 0.12.0
Loaded ../../../Examples/telidraw/hello.nap (35 bytes)
Parsed 22 commands, 0 errors
Wrote hello.png (6709 bytes, 1024x768)
```

## Use cases 💡

- Rails controller that renders `.nap` attachments to PNG on upload.
- Jekyll / Middleman static-site plugin that generates thumbnails for a `.nap` archive.
- Ruby-based automation (rake tasks, Chef / Puppet recipes).

See [../README.md](../README.md) for the full comparison table.
