# Sublimation model input checks

The runner links the actual model planner. It does not start Corel, read saved customer jobs, send network requests or create a paid generation. It checks explicit model IDs, legacy metadata defaults, native input field names, model dimensions and aspect ratios, the Seedream prompt limit, single-image output and retention of provider filter defaults.

```powershell
dotnet run --project tests/VanyaTools.SublimationModelsChecks/VanyaTools.SublimationModelsChecks.csproj --configuration Release
```

Input fields were checked against Replicate's public schemas: [FLUX.2 Pro](https://replicate.com/black-forest-labs/flux-2-pro/api/schema), [Nano Banana 2](https://replicate.com/google/nano-banana-2/api/schema), [Nano Banana Pro](https://replicate.com/google/nano-banana-pro/api/schema), [Seedream 4.5](https://replicate.com/bytedance/seedream-4.5/api/schema). Nano uses the closest supported ratio; a difference above 10% is rejected before capture or analysis. Final physical sizing remains the existing 300 DPI output stage. These checks do not establish that any model will accept a previously rejected input or reproduce garment artwork accurately.
