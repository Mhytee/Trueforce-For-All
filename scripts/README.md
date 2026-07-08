# scripts/

## archive/

One-shot data-generation artifacts from the 2026-05 car-catalog bake pipeline
(AC cylinder probing, FH5 catalog parsing from ManteoMax's spreadsheet, engine-config
rebakes). They already ran; their output is baked into
`src/TrueforceForAll.Plugin/BuiltinCarCylinders.cs`. Nothing in the build, docs,
or installer references them. Kept for provenance; treat as read-only history,
not live code.

One caveat: `archive/probe_ac_cylinders.ps1` contains copies of the heuristic
pattern tables in `src/TrueforceForAll.Plugin/CarCylinderResolver.cs`
(EngineCodenames / ChassisLookup / EngineConfigCodenames). If those resolver
tables change and the probe is ever re-run, re-sync them first (or single-source
them; see D3 in docs/agent-audit.md).
