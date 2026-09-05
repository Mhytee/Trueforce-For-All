# Arcade publishing plugin

`dinput8_x86.dll` and `dinput8_x64.dll` here are a build of **FFBArcadePlugin**
with one addition: an output backend that publishes a game's decoded force
feedback to a named shared memory block instead of driving a haptic device
directly.

That is what lets an emulated arcade cabinet's own force feedback reach TF4ALL.
The wheel's lights and screen cannot be written while something else is pushing
force at the same endpoint, so taking the force upstream and being the only
writer is what frees them.

## Licence, and why these are separate programs

FFBArcadePlugin is **GPL-3.0-or-later**. TrueForce For All is **GPL-2.0-only**.
Those two cannot be combined into a single work, so they are not combined:

- The DLL here runs inside the **arcade game's** process.
- TF4ALL runs inside **SimHub**.
- The only thing crossing between them is a fixed-layout block of numbers.
- No code, headers or linkage are shared in either direction.

That is aggregation, which both licences permit, and it is a requirement rather
than an implementation detail. Do not merge any of that project's source into
this one, and do not link against it.

Shipping it obliges us to point at its source, which is what this file is for.

## Source

Built from a fork of <https://github.com/Boomslangnz/FFBArcadePlugin>.

- Upstream commit: `85bdf2a`
- Our changes: `Common Files/SharedMemoryOutput.{h,cpp}` (new), plus the wiring
  in `DllMain.cpp` and two project-file entries. Zero game decoders touched.
- Fork repository: **TODO before release** — publish the fork and put its URL
  here. GPL-3.0 §6 requires the corresponding source to be available to anyone
  who receives the binary, and a link from the installer is how we satisfy it.

### Changes we made

1. **`SharedMemoryOutput` backend.** An opt-in output mode selected in
   `FFBPlugin.ini`:

   - `SharedMemoryOutput=0` — off, upstream behaviour unchanged (the default)
   - `SharedMemoryOutput=1` — publish *and* render as normal
   - `SharedMemoryOutput=2` — publish only, leaving the device alone
   - `SharedMemoryOutputFallbackMs=3000` — in mode 2, resume rendering when no
     reader has been present for this long

   The fallback is what makes mode 2 safe to leave installed: a reader announces
   itself by holding a named event, Windows releases that handle if the reader
   exits or crashes, and the game goes back to driving the wheel itself rather
   than being left with no force feedback and nothing to explain why.

2. **A build fix, unrelated to the above.** `Game Files/Cars.cpp` was missing
   from `Dinput8Wrapper.vcxproj` while `DllMain.cpp` constructs `new Cars`, so
   upstream does not link as checked out. Worth its own pull request.

Both changes are written to be upstreamable: the backend is off by default and
adds no cost when unused.

## Rebuilding

```
msbuild Dinput8Wrapper.sln -p:Configuration=Release -p:Platform=x86 -p:SpectreMitigation=Spectre
msbuild Dinput8Wrapper.sln -p:Configuration=Release -p:Platform=x64 -p:SpectreMitigation=Spectre
```

`SpectreMitigation=Spectre` is only needed on a machine where Visual Studio has
the Spectre-mitigated ATL libraries but not the plain ones. Install
"C++ ATL for latest v143 build tools" and it can be dropped.

The x86 output lands in `Release.Win32/<game name>/dinput8.dll` (the post-build
step fans one copy out per supported game); x64 lands in `Release.x64/`.
