#!/usr/bin/env python3
# Must be set before cv2/imageio are imported so OpenCV loads OpenEXR support
import os
os.environ["OPENCV_IO_ENABLE_OPENEXR"] = "1"

"""
Generate a merged colored PLY point cloud from all keyframes saved by KeyFrameManager.

Usage:
  pip install numpy imageio
  python generate_pointcloud.py <keyframes_root> [output.ply]

  Example:
    python generate_pointcloud.py C:\\Users\\danie\\Desktop\\keyframes output.ply
"""

import json
import sys
import numpy as np

try:
    import imageio.v3 as iio
except ImportError:
    import imageio as iio


def quaternion_to_rotation_matrix(qx, qy, qz, qw):
    """Unity quaternion → 3x3 rotation matrix (camera-local → world)."""
    return np.array([
        [1 - 2*(qy*qy + qz*qz),     2*(qx*qy - qz*qw),     2*(qx*qz + qy*qw)],
        [    2*(qx*qy + qz*qw), 1 - 2*(qx*qx + qz*qz),     2*(qy*qz - qx*qw)],
        [    2*(qx*qz - qy*qw),     2*(qy*qz + qx*qw), 1 - 2*(qx*qx + qy*qy)],
    ], dtype=np.float64)


def process_keyframe(keyframe_dir, min_depth=0.1, max_depth=15.0):
    """Load one keyframe and return (N,3) world points + (N,3) uint8 colors."""
    depth = iio.imread(os.path.join(keyframe_dir, 'depth.exr'))
    rgb   = iio.imread(os.path.join(keyframe_dir, 'LeftRGB.png'))

    with open(os.path.join(keyframe_dir, 'intrinsics.json')) as f:
        intr = json.load(f)
    with open(os.path.join(keyframe_dir, 'pose.json')) as f:
        pose = json.load(f)

    # Depth: 2-D float32
    if depth.ndim == 3:
        depth = depth[:, :, 0]
    depth = depth.astype(np.float32)
    H, W = depth.shape

    # Unity EncodeToEXR stores Y=0 at the bottom; imageio reads row-0 at top → flip
    depth = depth[::-1, :]
    rgb   = rgb  [::-1, :, :]

    # Intrinsics (sensor pixel space, Y-up convention)
    fx = float(intr['FocalLength']['x'])
    fy = float(intr['FocalLength']['y'])
    cx = float(intr['PrincipalPoint']['x'])
    cy = float(intr['PrincipalPoint']['y'])
    sx = float(intr['SensorResolution']['x'])
    sy = float(intr['SensorResolution']['y'])

    # Camera pose
    pos = np.array([pose['px'], pose['py'], pose['pz']], dtype=np.float64)
    R   = quaternion_to_rotation_matrix(pose['rx'], pose['ry'], pose['rz'], pose['rw'])

    # Compute crop region (mirrors SDK's CalcSensorCropRegion).
    # Quest 3: sensor=1280×1280, camera at 1280×960 → cropY=160, cropH=960.
    # Without this, sensor_y spans [0,1280] instead of [160,1120] — ~10° error at edges.
    scale_x = W / sx
    scale_y = H / sy
    max_scale = max(scale_x, scale_y)
    scale_x /= max_scale
    scale_y /= max_scale
    crop_x = sx * (1.0 - scale_x) * 0.5
    crop_y = sy * (1.0 - scale_y) * 0.5
    crop_w = sx * scale_x
    crop_h = sy * scale_y

    # Pixel grid → sensor coords (with crop offset) → ray direction
    col_grid, row_grid = np.meshgrid(np.arange(W, dtype=np.float32),
                                     np.arange(H, dtype=np.float32))
    sensor_x = crop_x + (col_grid / W) * crop_w
    sensor_y = crop_y + (row_grid / H) * crop_h

    dx = (sensor_x - cx) / fx
    dy = (sensor_y - cy) / fy

    t = depth.ravel()
    Px = t * dx.ravel()
    Py = t * dy.ravel()
    Pz = t

    valid = (Pz > min_depth) & (Pz < max_depth)
    P_cam = np.stack([Px, Py, Pz], axis=1)[valid]
    colors = rgb[:, :, :3].reshape(-1, 3)[valid]

    P_world = (R @ P_cam.T).T + pos
    return P_world.astype(np.float32), colors.astype(np.uint8)


def write_ply(output_path, all_points, all_colors):
    n = len(all_points)
    header = (
        "ply\n"
        "format binary_little_endian 1.0\n"
        f"element vertex {n}\n"
        "property float x\n"
        "property float y\n"
        "property float z\n"
        "property uchar red\n"
        "property uchar green\n"
        "property uchar blue\n"
        "end_header\n"
    ).encode()

    dtype = np.dtype([
        ('x', '<f4'), ('y', '<f4'), ('z', '<f4'),
        ('r', 'u1'),  ('g', 'u1'),  ('b', 'u1'),
    ])
    verts = np.empty(n, dtype=dtype)
    verts['x'] = all_points[:, 0]
    verts['y'] = all_points[:, 1]
    verts['z'] = all_points[:, 2]
    verts['r'] = all_colors[:, 0]
    verts['g'] = all_colors[:, 1]
    verts['b'] = all_colors[:, 2]

    with open(output_path, 'wb') as f:
        f.write(header)
        f.write(verts.tobytes())


def diag_keyframe(keyframe_dir):
    """Print depth and pose stats for one keyframe — no PLY output."""
    depth = iio.imread(os.path.join(keyframe_dir, 'depth.exr'))
    if depth.ndim == 3:
        depth = depth[:, :, 0]
    depth = depth.astype(np.float32)

    with open(os.path.join(keyframe_dir, 'pose.json')) as f:
        pose = json.load(f)
    with open(os.path.join(keyframe_dir, 'intrinsics.json')) as f:
        intr = json.load(f)

    valid = depth[(depth > 0.01) & (depth < 50.0)]
    name  = os.path.basename(keyframe_dir)

    print(f"\n── Keyframe {name} ──────────────────────────")
    if valid.size == 0:
        print("  depth: ALL ZERO or invalid — registration produced no output")
    else:
        print(f"  depth (valid px): min={valid.min():.3f}m  mean={valid.mean():.3f}m  "
              f"max={valid.max():.3f}m  coverage={100*valid.size/depth.size:.1f}%")
    print(f"  pose:  pos=({pose['px']:.3f}, {pose['py']:.3f}, {pose['pz']:.3f})  "
          f"rot=({pose['rx']:.3f}, {pose['ry']:.3f}, {pose['rz']:.3f}, {pose['rw']:.3f})")
    print(f"  intr:  fx={intr['FocalLength']['x']:.1f}  fy={intr['FocalLength']['y']:.1f}  "
          f"cx={intr['PrincipalPoint']['x']:.1f}  cy={intr['PrincipalPoint']['y']:.1f}  "
          f"res={intr['SensorResolution']['x']}x{intr['SensorResolution']['y']}")


if __name__ == '__main__':
    args = sys.argv[1:]
    diag_only = '--diag' in args
    args = [a for a in args if a != '--diag']

    if not args:
        print(f"Usage: {os.path.basename(sys.argv[0])} <keyframes_root> [output.ply] [--diag]")
        print("  --diag  print depth/pose stats for every keyframe, skip PLY generation")
        sys.exit(1)

    root    = args[0]
    out_ply = args[1] if len(args) > 1 else os.path.join(root, 'pointcloud.ply')

    # Collect all subdirectories that contain a depth.exr
    kf_dirs = sorted(
        [os.path.join(root, d) for d in os.listdir(root)
         if os.path.isdir(os.path.join(root, d))
         and os.path.exists(os.path.join(root, d, 'depth.exr'))],
        key=lambda p: int(os.path.basename(p)) if os.path.basename(p).isdigit() else 0
    )

    if not kf_dirs:
        print(f"No keyframes found in {root}")
        sys.exit(1)

    print(f"Found {len(kf_dirs)} keyframes")

    if diag_only:
        for kf in kf_dirs:
            diag_keyframe(kf)
        sys.exit(0)

    all_pts, all_cols = [], []
    for kf in kf_dirs:
        try:
            pts, cols = process_keyframe(kf)
            all_pts.append(pts)
            all_cols.append(cols)
            print(f"  {os.path.basename(kf):>4s}  {len(pts):>8,} points")
        except Exception as e:
            print(f"  {os.path.basename(kf):>4s}  SKIP ({e})")

    if not all_pts:
        print("No valid keyframes processed.")
        sys.exit(1)

    merged_pts  = np.concatenate(all_pts,  axis=0)
    merged_cols = np.concatenate(all_cols, axis=0)

    write_ply(out_ply, merged_pts, merged_cols)
    print(f"\nTotal: {len(merged_pts):,} points → {out_ply}")
