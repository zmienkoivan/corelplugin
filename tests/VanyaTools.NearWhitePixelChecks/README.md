# Near-white correction checks

The isolated .NET Framework 4.8 x64/STA runner links the actual `DtfNearWhitePixels.cs` and `DtfNearWhiteFilter.cs`. It uses small straight-BGRA buffers and creates its own PNG fixtures under a GUID temporary directory. It does not open Corel, create windows, read customer images, send network requests or print.

```powershell
dotnet run --project tests/VanyaTools.NearWhitePixelChecks/VanyaTools.NearWhitePixelChecks.csproj --configuration Release
```

The checks cover exact-white exclusion, inclusive per-channel tolerance, transparent and semitransparent pixels, tinted near-whites, independent diagnostic buffers, exact whitening, alpha preservation, idempotence, marker blending/channel order, cancellation and malformed input. Diagnostic overlays are separate arrays and cannot modify source or export buffers. PNG round trips verify exact RGB/alpha values, dimensions and decoded DPI, opaque RGB input, source immutability, independent output replacement and cancellation without overwriting an earlier result.

This helper operates on RGB pixels that are already decoded. It does not convert CMYK, apply ICC profiles or establish that a capture/import path preserves its original color model; those paths require separate verification.
