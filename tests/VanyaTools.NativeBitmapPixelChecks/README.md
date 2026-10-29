# Native bitmap pixel reader checks

This Windows .NET Framework 4.8 runner links the actual `NativeBitmapPixelReader.cs`. Plain fake bitmap/tile objects supply pixel bytes. It never opens Corel, uses ROT, accesses a printer, writes files, or sends a network request.

Run:

```powershell
dotnet run --project tests/VanyaTools.NativeBitmapPixelChecks/VanyaTools.NativeBitmapPixelChecks.csproj --configuration Release
```

Checks cover bottom-origin/bottom-up pixel ordering, BGRA output/DPI/freeze, opaque and transparent canvases, byte-exact white compositing, independent color/alpha tile partitions, unaligned tile edges and row padding. They reject missing rows/columns, overlapping tiles that still leave holes, out-of-bounds tile rectangles, mismatched alpha dimensions, unsupported negative strides, incomplete buffers, invalid pixel formats and excessive dimensions/counts.

The coverage sweep stores rectangles and boundary events, with memory proportional to tile count rather than one flag per image pixel. Existing pixel ordering and white compositing are retained. These checks demonstrate that incomplete tile reads cannot silently produce missing regions; they do not prove that incomplete tiles caused a particular user-reported capture problem. Native Corel rendering, effects, grouping and PowerClip are outside this runner.
