"""Write runtime assets from the offline Blender bake (no order artwork)."""
import pathlib
import shutil
import sys

import numpy as np
from PIL import Image
from scipy.ndimage import distance_transform_edt

staging = pathlib.Path(sys.argv[1]).resolve()
destination = pathlib.Path(sys.argv[2]).resolve()
destination.mkdir(parents=True, exist_ok=True)
data = np.load(staging / "shade-prepared.npz")
shade, covered = data["shade"], data["covered"]
assert shade.shape == covered.shape == (6, 512, 512)
for part in range(6):
    assert covered[part].any(), "Missing print chart"
    nearest = distance_transform_edt(
        ~covered[part], return_distances=False, return_indices=True
    )
    shade[part] = shade[part][tuple(nearest)]
assert np.isfinite(shade).all()
Image.fromarray(np.clip(shade.reshape(3072, 512) * 255, 0, 255).astype("uint8")).save(
    destination / "shade.png"
)
shutil.copyfile(staging / "shirt.mesh.gz", destination / "shirt.mesh.gz")
print("Runtime shirt assets written:", destination)
