# Native raster filter checks

This isolated .NET Framework 4.8 x64 runner links the real native-plane buffer, correction pipeline and almost-white mask. It operates only on small managed RGB, grayscale and CMYK arrays. It never connects to Corel, uses customer images, opens windows or prints.

```powershell
dotnet run --project tests/VanyaTools.NativeRasterFilterChecks/VanyaTools.NativeRasterFilterChecks.csproj --configuration Release
```

Checks cover exact white, byte-preserving alpha threshold, detached input/output buffers, native channel order metadata, grayscale and CMYK whitening, alpha-weighted native-channel interpolation, physical sizes and anisotropic DPI, outline-only exterior changes, crop margins and offsets, pixel budget guards and cancellation. CMYK fixtures use a deliberately nonidentity calibrated channel order. Printable results never round-trip through RGB; the temporary RGB preview used to choose near-white pixels is separate from native color planes.

Native Corel tile compatibility, color-order calibration, source geometry and successful model-preserving writes require separate integration checks in Corel.
