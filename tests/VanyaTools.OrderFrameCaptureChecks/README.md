# Direct order-frame export checks

The Windows .NET Framework 4.8 x64/STA runner links the actual `OrderFrameCapture.cs` and supplies plain fake Corel objects. It does not connect to Corel, use ROT, change user documents, send network requests or print. Its fake PNG export writes only the service's own GUID directory under the system temporary folder, which the service removes after each check.

```powershell
dotnet run --project tests/VanyaTools.OrderFrameCaptureChecks/VanyaTools.OrderFrameCaptureChecks.csproj --configuration Release
```

The checks cover image-size/DPI planning, outline-inclusive bounds, BSTR filename and real option objects for PNG selection export, RGB/antialiasing/opaque/profile settings, detached frozen image decoding and exact RGB bytes, and cleanup after success and injected failures. They also verify unit/optimization/selection restoration, document changes, empty or malformed bounds, missing/corrupt/wrong-sized PNGs and the STA guard before host lookup. Fake Duplicate/Group/mutation methods are tripwires: the service must never call them.

These checks verify the managed export protocol. Native composition, effect rendering and Corel filter compatibility require a separate test in Corel with a local document copy. There is no fallback to the previous duplicate/group pipeline, because that pipeline can reorder mixed artwork and hide elements.
