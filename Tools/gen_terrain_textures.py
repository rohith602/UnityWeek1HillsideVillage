"""Generate tileable, stylised terrain textures matching Kenney's flat-shaded palette.

Photoreal ground textures clash with flat-shaded low-poly models, so these are
deliberately low-contrast: just enough value variation to stop the terrain reading
as a flat colour fill, while staying in Kenney's own material colours.
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")

SIZE = 1024
OUT = Path(sys.argv[1])
OUT.mkdir(parents=True, exist_ok=True)

# Sampled straight out of the Kenney Nature Kit .mtl files.
PALETTE = {
    "GrassMeadow": (0.196, 0.451, 0.298),
    "GrassDry":    (0.424, 0.498, 0.267),
    "DirtPath":    (0.600, 0.373, 0.255),
    "RockCliff":   (0.400, 0.447, 0.463),
}


def value_noise(size, freq, rng):
    """Tileable bilinear value noise; lattice indices wrap, so edges match."""
    g = rng.random((freq, freq))
    c = np.linspace(0, freq, size, endpoint=False)
    i0 = np.floor(c).astype(int) % freq
    i1 = (i0 + 1) % freq
    t = c - np.floor(c)
    t = t * t * (3 - 2 * t)  # smoothstep
    ty, tx = t[:, None], t[None, :]
    return (
        g[np.ix_(i0, i0)] * (1 - ty) * (1 - tx)
        + g[np.ix_(i0, i1)] * (1 - ty) * tx
        + g[np.ix_(i1, i0)] * ty * (1 - tx)
        + g[np.ix_(i1, i1)] * ty * tx
    )


def fbm(size, seed):
    rng = np.random.default_rng(seed)
    total = np.zeros((size, size))
    amp, norm = 1.0, 0.0
    for freq in (4, 8, 16, 32, 64):
        total += amp * value_noise(size, freq, rng)
        norm += amp
        amp *= 0.5
    return total / norm


for idx, (name, rgb) in enumerate(PALETTE.items()):
    n = fbm(SIZE, seed=1000 + idx)
    n = (n - n.min()) / (n.max() - n.min())
    shade = 0.82 + 0.30 * n                      # subtle value variation only
    img = np.clip(np.array(rgb)[None, None, :] * shade[:, :, None], 0, 1)
    Image.fromarray((img * 255).astype(np.uint8)).save(OUT / f"T_{name}.png")
    print(f"  T_{name}.png  base={tuple(round(c,3) for c in rgb)}")

# --- self-check: textures must actually tile and not be flat fills ---
for f in OUT.glob("T_*.png"):
    a = np.asarray(Image.open(f)).astype(float)
    assert a.shape == (SIZE, SIZE, 3), f"{f.name} wrong shape {a.shape}"
    assert a.std() > 2.0, f"{f.name} is a flat fill (std={a.std():.2f})"
    # wrap seam: last column should be close to a continuation of the first
    seam = np.abs(a[:, -1] - a[:, 0]).mean()
    interior = np.abs(a[:, 1] - a[:, 0]).mean()
    assert seam < interior * 6 + 3, f"{f.name} seam discontinuity {seam:.2f} vs {interior:.2f}"
print(f"OK: {len(list(OUT.glob('T_*.png')))} tileable textures verified")
