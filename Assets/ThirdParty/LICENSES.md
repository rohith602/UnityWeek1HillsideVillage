# Third-Party Asset Licences

Every external asset in this project is **CC0 1.0 Universal (public domain)**. No asset here
carries an attribution requirement, a share-alike clause, or any restriction on commercial or
academic use. Credit is given below anyway, because crediting the author is good practice even
when the licence does not demand it.

## Kenney.nl asset kits

| Folder | Pack | Source | Models used |
|---|---|---|---|
| `Kenney_NatureKit/` | Nature Kit | https://kenney.nl/assets/nature-kit | 47 of 329 |
| `Kenney_CityKitSuburban/` | City Kit (Suburban) | https://kenney.nl/assets/city-kit-suburban | 17 of 40 |
| `Kenney_SurvivalKit/` | Survival Kit | https://kenney.nl/assets/survival-kit | 10 of 80 |
| `Kenney_CarKit/` | Car Kit | https://kenney.nl/assets/car-kit | 8 of 50 |

Author: **Kenney** (www.kenney.nl)
Licence: **Creative Commons Zero (CC0 1.0)**, https://creativecommons.org/publicdomain/zero/1.0/

Each folder retains the original unmodified `License.txt` shipped with its pack.

### Why only a subset of each pack is imported

The four packs total 499 models. Importing all of them would add import time and repository
weight for models the scene never instantiates. Only the models actually placed by
`SceneBuilder.cs` are committed. Every pack ships in five formats (FBX, OBJ, GLB, DAE, STL);
only **FBX** is kept, since that is Unity's natively supported format and the other four are
redundant duplicates of the same geometry.

## Terrain textures

`Assets/_Project/Art/Textures/Terrain/T_*.png` are **not** third-party. They are generated
procedurally by `Tools/gen_terrain_textures.py` in this repository, using colour values sampled
from the Kenney kits' own `.mtl` material definitions so the ground matches the models' palette.
They are original work and carry no external licence.
