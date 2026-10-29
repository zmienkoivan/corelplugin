# Raster insertion protocol checks

Standalone Windows .NET Framework 4.8 x64/STA checks link the actual `RasterResultInsertion`, `RasterInsertionSettings` and `CorelConstants` sources. A local fake `CorelApp` replaces the host: the runner never opens Corel, uses ROT, or calls a native Corel COM object.

The runner verifies physical PNG size/DPI and stroke offsets, all three placement modes, captured-source identity instead of today's selection, relative movement of multiple originals, late replacement, `ImportEx` BSTR/options arguments, and one-group rollback on injected partial failures. It also checks fresh page automation facades, readable same-identity page reordering, conservative proxy-alias rejection, deleted-page getters, strict document/thread/source guards, active-page StaticID resolution with captured-reference metadata validation, and NewAside without reading original objects. NewAside may tolerate fresh document automation identity only for the exact same nonempty saved path; destructive modes retain strict document identity. It checks persisted-file fingerprints, destructive retry rejection, explicit safe `NewAside` retry after a failed replacement, and diagnostic mode overriding a requested replacement.

The PNG metadata and sidecar serialization use the real WPF/.NET implementations. Native import, grouping, rendering, ICC, crop and Undo behavior are simulated; passing these checks verifies the managed protocol, not native Corel API compatibility or an end-to-end Corel session.

Build and run on Windows with the .NET 4.8 targeting pack:

```powershell
dotnet build tests/VanyaTools.RasterInsertionChecks/VanyaTools.RasterInsertionChecks.csproj -c Release
tests/VanyaTools.RasterInsertionChecks/bin/Release/net48/VanyaTools.RasterInsertionChecks.exe
```

The executable writes generated PNG/CDR fixture files under its own `cases/` output directory and a `results.json` report beside the executable. Exit code 0 means all checks passed; 1 means a protocol assertion failed. It does not change plugin settings, user CDR files, or installed Corel components.