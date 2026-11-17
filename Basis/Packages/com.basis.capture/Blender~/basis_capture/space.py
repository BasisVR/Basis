import numpy as np

C = np.array([[-1.0, 0.0, 0.0, 0.0], [0.0, 0.0, -1.0, 0.0], [0.0, 1.0, 0.0, 0.0], [0.0, 0.0, 0.0, 1.0]])
CT = C.T
LOOK = np.array([[-1.0, 0.0, 0.0, 0.0], [0.0, 0.0, 1.0, 0.0], [0.0, 1.0, 0.0, 0.0], [0.0, 0.0, 0.0, 1.0]])


def to_blender_matrix(m):
    return C @ m @ CT


def to_blender_points(p):
    p = np.asarray(p, np.float64)
    return np.stack([-p[..., 0], -p[..., 2], p[..., 1]], axis=-1)


def quat_to_matrix(q):
    q = np.asarray(q, np.float64)
    n = np.linalg.norm(q, axis=-1, keepdims=True)
    n[n < 1e-12] = 1.0
    x, y, z, w = np.moveaxis(q / n, -1, 0)
    m = np.empty(q.shape[:-1] + (3, 3))
    m[..., 0, 0] = 1 - 2 * (y * y + z * z)
    m[..., 0, 1] = 2 * (x * y - z * w)
    m[..., 0, 2] = 2 * (x * z + y * w)
    m[..., 1, 0] = 2 * (x * y + z * w)
    m[..., 1, 1] = 1 - 2 * (x * x + z * z)
    m[..., 1, 2] = 2 * (y * z - x * w)
    m[..., 2, 0] = 2 * (x * z - y * w)
    m[..., 2, 1] = 2 * (y * z + x * w)
    m[..., 2, 2] = 1 - 2 * (x * x + y * y)
    return m


def trs_to_matrix(trs):
    trs = np.asarray(trs, np.float64)
    m = np.zeros(trs.shape[:-1] + (4, 4))
    r = quat_to_matrix(trs[..., 3:7])
    m[..., :3, :3] = r * trs[..., None, 7:10]
    m[..., :3, 3] = trs[..., 0:3]
    m[..., 3, 3] = 1.0
    return m


def matrix_to_quat_wxyz(r):
    r = np.asarray(r, np.float64)
    t = r[..., 0, 0] + r[..., 1, 1] + r[..., 2, 2]
    q = np.zeros(r.shape[:-2] + (4,))
    c0 = t > 0
    s = np.sqrt(np.maximum(t + 1.0, 1e-12)) * 2
    q[..., 0] = np.where(c0, 0.25 * s, q[..., 0])
    q[..., 1] = np.where(c0, (r[..., 2, 1] - r[..., 1, 2]) / s, q[..., 1])
    q[..., 2] = np.where(c0, (r[..., 0, 2] - r[..., 2, 0]) / s, q[..., 2])
    q[..., 3] = np.where(c0, (r[..., 1, 0] - r[..., 0, 1]) / s, q[..., 3])
    c1 = ~c0 & (r[..., 0, 0] > r[..., 1, 1]) & (r[..., 0, 0] > r[..., 2, 2])
    s = np.sqrt(np.maximum(1.0 + r[..., 0, 0] - r[..., 1, 1] - r[..., 2, 2], 1e-12)) * 2
    q[..., 0] = np.where(c1, (r[..., 2, 1] - r[..., 1, 2]) / s, q[..., 0])
    q[..., 1] = np.where(c1, 0.25 * s, q[..., 1])
    q[..., 2] = np.where(c1, (r[..., 0, 1] + r[..., 1, 0]) / s, q[..., 2])
    q[..., 3] = np.where(c1, (r[..., 0, 2] + r[..., 2, 0]) / s, q[..., 3])
    c2 = ~c0 & ~c1 & (r[..., 1, 1] > r[..., 2, 2])
    s = np.sqrt(np.maximum(1.0 + r[..., 1, 1] - r[..., 0, 0] - r[..., 2, 2], 1e-12)) * 2
    q[..., 0] = np.where(c2, (r[..., 0, 2] - r[..., 2, 0]) / s, q[..., 0])
    q[..., 1] = np.where(c2, (r[..., 0, 1] + r[..., 1, 0]) / s, q[..., 1])
    q[..., 2] = np.where(c2, 0.25 * s, q[..., 2])
    q[..., 3] = np.where(c2, (r[..., 1, 2] + r[..., 2, 1]) / s, q[..., 3])
    c3 = ~c0 & ~c1 & ~c2
    s = np.sqrt(np.maximum(1.0 + r[..., 2, 2] - r[..., 0, 0] - r[..., 1, 1], 1e-12)) * 2
    q[..., 0] = np.where(c3, (r[..., 1, 0] - r[..., 0, 1]) / s, q[..., 0])
    q[..., 1] = np.where(c3, (r[..., 0, 2] + r[..., 2, 0]) / s, q[..., 1])
    q[..., 2] = np.where(c3, (r[..., 1, 2] + r[..., 2, 1]) / s, q[..., 2])
    q[..., 3] = np.where(c3, 0.25 * s, q[..., 3])
    q /= np.linalg.norm(q, axis=-1, keepdims=True)
    return q


def decompose(m):
    m = np.asarray(m, np.float64)
    loc = m[..., :3, 3].copy()
    basis = m[..., :3, :3]
    scale = np.linalg.norm(basis, axis=-2)
    det = np.linalg.det(basis)
    scale[..., 0] *= np.where(det < 0, -1.0, 1.0)
    safe = np.where(np.abs(scale) < 1e-12, 1.0, scale)
    rot = basis / safe[..., None, :]
    return loc, matrix_to_quat_wxyz(rot), scale


def continuous(q):
    q = np.array(q)
    if len(q) < 2:
        return q
    s = np.where(np.sum(q[1:] * q[:-1], axis=-1) < 0, -1.0, 1.0)
    q[1:] *= np.cumprod(s, axis=0)[..., None]
    return q


def strip_scale(m):
    m = np.array(m, np.float64)
    basis = m[..., :3, :3]
    scale = np.linalg.norm(basis, axis=-2)
    scale[scale < 1e-12] = 1.0
    m[..., :3, :3] = basis / scale[..., None, :]
    return m, scale


def srgb_to_linear(c):
    c = np.asarray(c, np.float64)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def resample(times, values, target):
    times = np.asarray(times, np.float64)
    if len(times) == 0:
        return values[:0]
    idx = np.clip(np.searchsorted(times, target, side="right") - 1, 0, len(times) - 1)
    nxt = np.clip(idx + 1, 0, len(times) - 1)
    span = times[nxt] - times[idx]
    t = np.where(span > 1e-9, (target - times[idx]) / np.where(span > 1e-9, span, 1.0), 0.0)
    t = np.clip(t, 0.0, 1.0)
    shape = (len(target),) + (1,) * (values.ndim - 1)
    return values[idx] * (1 - t).reshape(shape) + values[nxt] * t.reshape(shape)


def resample_trs(times, trs, target):
    out = resample(times, trs, target)
    times = np.asarray(times, np.float64)
    if len(times):
        idx = np.clip(np.searchsorted(times, target, side="right") - 1, 0, len(times) - 1)
        nxt = np.clip(idx + 1, 0, len(times) - 1)
        a = trs[idx][..., 3:7]
        b = trs[nxt][..., 3:7]
        sign = np.where(np.sum(a * b, axis=-1, keepdims=True) < 0, -1.0, 1.0)
        span = times[nxt] - times[idx]
        t = np.clip(np.where(span > 1e-9, (target - times[idx]) / np.where(span > 1e-9, span, 1.0), 0.0), 0.0, 1.0)
        shape = (len(target),) + (1,) * (a.ndim - 1)
        q = a * (1 - t).reshape(shape) + b * sign * t.reshape(shape)
        q /= np.maximum(np.linalg.norm(q, axis=-1, keepdims=True), 1e-12)
        out[..., 3:7] = q
    return out
