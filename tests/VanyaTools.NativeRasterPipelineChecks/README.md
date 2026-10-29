# Native raster file pipeline checks

This Windows x64/STA runner links the actual source-model buffers, native model adapter, filters and file pipeline. It writes synthetic RGB, grayscale and permuted-channel CMYK fixtures only into its own checked GUID directory under the system temporary folder. It never contacts Corel, the network, customer files or print queues.

```powershell
dotnet run --project tests/VanyaTools.NativeRasterPipelineChecks/VanyaTools.NativeRasterPipelineChecks.csproj --configuration Release
```

The checks verify exact native channels, alpha, model, DPI and CMYK order through real PNG/companion saves and processing; exact RGB white in decoded previews; independent whitening, alpha threshold and outline; source immutability; PNG hash mismatch, truncated/corrupt headers and recompressed payload tampering; early cancellation; restoration of the previous pair when either destination is locked; and operation beneath a 190-character parent directory with final filenames inside classic MAX_PATH. Staging uses short GUID sibling names rather than extending the already long destination names. File-pair commits cannot be one filesystem transaction: both files are staged before commit, commit ignores cancellation between renames, and an unsuccessful second rename rolls back the first. If an external file lock also prevents rollback, the operation throws and preserves owned backup files for recovery.
