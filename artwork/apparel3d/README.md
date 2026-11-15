# Shirt model sources · CC0

The built-in shirt is adapted from `male_casualsuit06` in the official MakeHuman system asset pack. The source OBJ explicitly dedicates the asset to CC0 in September 2020 and names Data Collection AB, Joel Palmius and Jonas Hauquier as the copyright holders at that release.

- [Official pack and individual CC0 listing](https://files2.makehumancommunity.org/asset_packs/makehuman_system_assets/)
- [Source archive](https://files2.makehumancommunity.org/asset_packs/makehuman_system_assets/makehuman_system_assets_cc0.zip), `clothes/male_casualsuit06/`
- [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/)

Files retained here are the original OBJ, ambient-occlusion image and tangent-space normal image. Their SHA256 values are:

| File | SHA256 |
| --- | --- |
| male_casualsuit06.obj | 9A86386435D7557A520320B8DB4CD0FFE4755040044D1925C2B5730B864A603E |
| male_casualsuit06_ao.png | DC27B02602060A8DF4C1117F55BDE7DEA952342D847003AFE2C6E12145301147 |
| male_casualsuit06_normal.png | CCDBF48A86EB922F5CCF38A7D3DC9DE8828A6460C687EB84DACF8315D079A343 |

The adaptation removes the pants, separates the six print charts, scales the shirt, adds a collar material and 1 mm fabric thickness, subdivides the authored surface, and bakes the source normal map into vertex normals. A relaxed geometry endpoint controls the fold relief. The source AO is transferred into the six print charts. No order files, customer artwork or generated photographs are part of these assets.

Rebuild with Python 3.12 (`numpy`, `scipy`, `Pillow`) and Blender 4.5.9 LTS:

```powershell
.\scripts\Build-ApparelShirtAsset.ps1 -BlenderPath 'C:\path\to\blender.exe'
```

The script writes only generated files in `release/apparel-shirt-build` and replaces `native/VanyaTools.Native/Assets/Apparel/shirt.mesh.gz` and `shade.png`. Blender and Python are authoring tools; they are not shipped or needed at runtime. The model is a display asset, with no live cloth or fit simulation.
