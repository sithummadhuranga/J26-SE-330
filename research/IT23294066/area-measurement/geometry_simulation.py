"""How much area error comes from the camera geometry alone, for different ways of using a reference marker.

A model, not a measurement. A pinhole camera looks at a flat plane 35 cm away. A round wound and a square marker lie side by
side on it. The plane is tilted, the marker's corners are found with some noise, and the wound's area is estimated three ways:
  side length   mm per pixel = marker side / average length of its four edges, the same everywhere
  area ratio    wound pixel area / marker pixel area x marker area
  homography    the four marker corners give a perspective map to millimetres, and the wound is measured through it
Assumptions that matter: 640x480 image, 75 degree horizontal field of view (typical for a phone's main camera), 35 cm distance,
wound of 400 mm². Not modelled: lens distortion, curved skin, a marker that is not flat against the wound, glare, and the
error of the segmentation itself (which is larger, see the evaluation results).

    python geometry_simulation.py
"""
import numpy as np

W, H = 640, 480
F = (W / 2) / np.tan(np.radians(75) / 2)
Z = 350.0  # mm from the camera to the middle of the plane
WOUND_MM2 = 400.0


def project(uv, tilt_deg):
    """Plane coordinates in mm (tilted about the vertical axis) to image pixels."""
    t = np.radians(tilt_deg)
    u, v = uv[:, 0], uv[:, 1]
    depth = Z + u * np.sin(t)
    return np.stack([F * u * np.cos(t) / depth + W / 2, F * v / depth + H / 2], 1)


def shoelace(p):
    x, y = p[:, 0], p[:, 1]
    return 0.5 * abs(np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1)))


def homography(src, dst):
    rows = []
    for (x, y), (u, v) in zip(src, dst):
        rows.append([x, y, 1, 0, 0, 0, -u * x, -u * y, -u])
        rows.append([0, 0, 0, x, y, 1, -v * x, -v * y, -v])
    return np.linalg.svd(np.array(rows))[2][-1].reshape(3, 3)


def through(h, pts):
    q = np.c_[pts, np.ones(len(pts))] @ h.T
    return q[:, :2] / q[:, 2:3]


def trial(tilt, marker_mm, gap_mm, corner_noise_px, rng):
    """Percent error of each method for one noisy trial. gap_mm is the distance between the centre of the marker and the
    centre of the wound, measured along the direction the plane is tilted (the worst case)."""
    r = np.sqrt(WOUND_MM2 / np.pi)
    a = np.linspace(0, 2 * np.pi, 360, endpoint=False)
    wound = np.c_[gap_mm / 2 + r * np.cos(a), r * np.sin(a)]
    s = marker_mm
    marker = np.array([[-gap_mm / 2 - s / 2, -s / 2], [-gap_mm / 2 + s / 2, -s / 2], [-gap_mm / 2 + s / 2, s / 2], [-gap_mm / 2 - s / 2, s / 2]])
    wound_px, marker_px = project(wound, tilt), project(marker, tilt)
    found = marker_px + rng.normal(0, corner_noise_px, marker_px.shape)
    wound_area_px = shoelace(wound_px)
    edges = np.linalg.norm(found - np.roll(found, -1, 0), axis=1)
    estimates = {
        "side length": wound_area_px * (s / edges.mean()) ** 2,
        "area ratio": wound_area_px / shoelace(found) * s ** 2,
        "homography": shoelace(through(homography(found, marker), wound_px)),
    }
    return {k: abs(v - WOUND_MM2) / WOUND_MM2 * 100 for k, v in estimates.items()}


def median_error(method, tilt, marker_mm, gap_mm, noise, rng, n=1500):
    return np.median([trial(tilt, marker_mm, gap_mm, noise, rng)[method] for _ in range(n)])


def main():
    rng = np.random.default_rng(0)
    print(f"assumptions: {W}x{H}, 75 degree field of view, plane at {Z / 10:.0f} cm, wound {WOUND_MM2:.0f} mm², "
          f"{Z / F:.2f} mm per pixel in the middle of the image\n")

    print("A) median |error| %, marker 30 mm, centres 50 mm apart, corners found to 0.5 px (on the 640x480 image)")
    print("tilt   side length   area ratio   homography")
    for tilt in (0, 10, 20, 30, 40):
        print(f"{tilt:3d}    " + "      ".join(f"{median_error(m, tilt, 30, 50, 0.5, rng):6.1f}" for m in ("side length", "area ratio", "homography")))

    print("\nB) homography, tilt 20 degrees: marker size against how well its corners are found")
    print("marker    0.5 px corners   0.1 px corners (about what a full-size photo would allow, a guess)")
    for size in (20, 30, 50, 70):
        print(f"{size:3d} mm   {median_error('homography', 20, size, 50, 0.5, rng):10.1f}      {median_error('homography', 20, size, 50, 0.1, rng):10.1f}")

    print("\nC) side length / area ratio with the centres 40 mm apart, corners to 0.1 px: only the tilt matters")
    print("marker 30 mm, centres 40 mm apart:  " + "   ".join(f"tilt {t}: {median_error('area ratio', t, 30, 40, 0.1, rng):.1f}%" for t in (0, 3, 5, 8)))

    print("\nD) the distance between the centres at 20 degrees tilt, marker 30 mm, corners 0.5 px (area ratio / homography)")
    for gap in (30, 50, 80, 120):
        print(f"centres {gap:3d} mm apart: {median_error('area ratio', 20, 30, gap, 0.5, rng):5.1f}% / {median_error('homography', 20, 30, gap, 0.5, rng):5.1f}%")


if __name__ == "__main__":
    main()
