#!/usr/bin/env python3
"""Generate every Project Brainwash sound effect from scratch.

Everything is synthesized here with numpy/scipy (filtered noise, oscillators,
modal resonators, a glottal-pulse + formant voice model, synthetic reverbs) and
encoded to mono 44.1 kHz Ogg Vorbis with ffmpeg/libvorbis.  No recorded samples
or sound libraries are used.  espeak-ng's built-in formant synthesizer (no MBROLA
voices) supplies two gibberish texture layers (a quiet goblin snicker, a plague-nurse
whisper) that are resampled and filtered; they are nonsense phonemes, never words.

Usage:
    python generate_sfx.py                 # regenerate everything + sfx.json
    python generate_sfx.py --only obey,cast_loop
    python generate_sfx.py --out /tmp/sfx --jobs 4
    python generate_sfx.py --list

Every sound uses a random generator seeded from its id and variant number, so
the output is reproducible (bit-exact with the same numpy/scipy/ffmpeg/espeak-ng
build).  Each file is decoded again after encoding to check peaks and, for
loops, that the wrap point stays seamless.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import subprocess
import sys
import tempfile
import zlib
from concurrent.futures import ProcessPoolExecutor

import numpy as np
from scipy import signal
from scipy.io import wavfile
from scipy.ndimage import maximum_filter1d, minimum_filter1d

SR = 44100
TAU = 2.0 * math.pi
HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_OUT = os.path.normpath(os.path.join(HERE, "..", "..", "unity", "Assets", "Brainwash", "Resources", "Audio"))

# ---------------------------------------------------------------------------
# Basic helpers
# ---------------------------------------------------------------------------


def ns(d):
    """Seconds -> samples."""
    return max(1, int(round(d * SR)))


def tl(n):
    """Time line (seconds) for n samples."""
    return np.arange(n) / SR


def curve(dur, pts, kind="lin"):
    """Piecewise curve over `dur` seconds sampled at SR. pts = [(t, v), ...]; kind lin | exp | cos."""
    return curve_n(ns(dur), pts, kind)


def curve_n(n, pts, kind="lin"):
    """Piecewise curve over n samples."""
    t = tl(int(n))
    ts = np.array([p[0] for p in pts], float)
    vs = np.array([p[1] for p in pts], float)
    if kind == "exp":
        return np.exp(np.interp(t, ts, np.log(np.maximum(vs, 1e-9))))
    if kind == "cos":
        if len(ts) == 1:
            return np.full(n, vs[0])
        idx = np.clip(np.searchsorted(ts, t, side="right") - 1, 0, len(ts) - 2)
        t0, t1 = ts[idx], ts[idx + 1]
        u = np.clip((t - t0) / np.maximum(t1 - t0, 1e-12), 0.0, 1.0)
        u = 0.5 - 0.5 * np.cos(np.pi * u)
        return vs[idx] + (vs[idx + 1] - vs[idx]) * u
    return np.interp(t, ts, vs)


def soft_edges(sig, fin=0.001, fout=0.004):
    """Fade in/out only if the signal starts/ends away from zero (prevents insertion clicks)."""
    if len(sig) < 4:
        return sig
    pk = np.max(np.abs(sig))
    if pk == 0:
        return sig
    if abs(sig[0]) > 1e-3 * pk or abs(sig[-1]) > 1e-3 * pk:
        sig = np.array(sig, float, copy=True)
        if abs(sig[0]) > 1e-3 * pk:
            k = min(len(sig), ns(fin))
            sig[:k] *= np.linspace(0.0, 1.0, k)
        if abs(sig[-1]) > 1e-3 * pk:
            k = min(len(sig), ns(fout))
            sig[len(sig) - k:] *= np.linspace(1.0, 0.0, k)
    return sig


def put(buf, sig, t, gain=1.0):
    """Mix sig into buf starting at time t (seconds); edges are click-proofed. Returns buf."""
    i = int(round(t * SR))
    if i < 0:
        sig = sig[-i:]
        i = 0
    e = min(len(buf), i + len(sig))
    if e > i:
        buf[i:e] += gain * soft_edges(np.asarray(sig[: e - i], float))
    return buf


def put_wrap(buf, sig, t, gain=1.0):
    """Mix sig into a loop buffer with wrap-around (for seamless loops)."""
    n = len(buf)
    i = int(round(t * SR)) % n
    sig = soft_edges(np.asarray(sig, float))
    idx = (np.arange(len(sig)) + i) % n
    np.add.at(buf, idx, gain * sig)
    return buf


def mix(*sigs):
    """Sum signals of different lengths (zero-padded to the longest)."""
    n = max(len(a) for a in sigs)
    y = np.zeros(n)
    for a in sigs:
        y[: len(a)] += a
    return y


def fit(x, dur, fade=0.02):
    """Pad/trim x to dur seconds with a raised-cosine fade at the end."""
    n = ns(dur)
    y = np.zeros(n)
    m = min(n, len(x))
    y[:m] = x[:m]
    f = min(ns(fade), n)
    y[n - f:] *= 0.5 + 0.5 * np.cos(np.linspace(0, np.pi, f))
    return y


def fades(x, fin=0.003, fout=0.005):
    x = x.copy()
    a = min(len(x), max(2, ns(fin)))
    b = min(len(x), max(2, ns(fout)))
    x[:a] *= 0.5 - 0.5 * np.cos(np.linspace(0, np.pi, a))
    x[len(x) - b:] *= 0.5 + 0.5 * np.cos(np.linspace(0, np.pi, b))
    return x


def endfade(y, frac=0.12, min_s=0.004):
    """Fade the last part of a fixed-length layer so it never ends with a hard cut."""
    k = min(len(y), max(2, ns(max(min_s, frac * len(y) / SR))))
    y = y.copy()
    y[len(y) - k:] *= 0.5 + 0.5 * np.cos(np.linspace(0, np.pi, k))
    return y


def norm(x, peak=1.0):
    m = np.max(np.abs(x))
    return x * (peak / m) if m > 0 else x


def rms_n(x):
    """Normalise to unit RMS."""
    return x / (np.sqrt(np.mean(x ** 2)) + 1e-12)


def db(v):
    return 10.0 ** (v / 20.0)


def midi(m):
    return 440.0 * 2.0 ** ((m - 69) / 12.0)


NOTE = {}
for _o in range(0, 9):
    for _i, _nm in enumerate(["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]):
        NOTE[f"{_nm}{_o}"] = midi(12 * (_o + 1) + _i)
NOTE.update({k.replace("#", "s"): v for k, v in list(NOTE.items())})
for _o in range(0, 9):
    for _a, _b in [("Db", "C#"), ("Eb", "D#"), ("Gb", "F#"), ("Ab", "G#"), ("Bb", "A#")]:
        NOTE[f"{_a}{_o}"] = NOTE[f"{_b}{_o}"]


def hz(name):
    return NOTE[name]


# ---------------------------------------------------------------------------
# Noise
# ---------------------------------------------------------------------------


def white(n, rng):
    return rng.standard_normal(int(n))


def colored(n, rng, slope=-3.0):
    """Noise with a spectral slope in dB/octave (-3 pink, -6 brown). Periodic in n."""
    n = int(n)
    X = np.fft.rfft(rng.standard_normal(n))
    f = np.fft.rfftfreq(n, 1.0 / SR)
    f[0] = f[1] if n > 1 else 1.0
    X *= (f / 1000.0) ** (slope / 6.0206)
    X[0] = 0.0
    x = np.fft.irfft(X, n)
    s = np.std(x)
    return x / s if s > 0 else x


def smooth_noise(n, rng, rate=20.0):
    """Band-limited random control signal (std ~1) with energy below `rate` Hz. Periodic in n."""
    n = int(n)
    X = np.fft.rfft(rng.standard_normal(n))
    f = np.fft.rfftfreq(n, 1.0 / SR)
    X *= np.exp(-0.5 * (f / rate) ** 2)
    X[0] = 0.0
    x = np.fft.irfft(X, n)
    s = np.std(x)
    return x / s if s > 0 else x


# ---------------------------------------------------------------------------
# Filters
# ---------------------------------------------------------------------------


def _wn(f):
    return float(min(max(f / (SR / 2.0), 1e-5), 0.995))


def lp(x, f, order=2):
    return signal.sosfilt(signal.butter(order, _wn(f), "lowpass", output="sos"), x)


def hp(x, f, order=2):
    return signal.sosfilt(signal.butter(order, _wn(f), "highpass", output="sos"), x)


def bp(x, f1, f2, order=2):
    return signal.sosfilt(signal.butter(order, [_wn(f1), _wn(f2)], "bandpass", output="sos"), x)


def lp0(x, f, order=2):
    """Zero-phase low-pass (for control signals / non-impulsive material)."""
    return signal.sosfiltfilt(signal.butter(order, _wn(f), "lowpass", output="sos"), x)


def hp0(x, f, order=2):
    return signal.sosfiltfilt(signal.butter(order, _wn(f), "highpass", output="sos"), x)


def biquad(kind, f, q=0.707, gain_db=0.0):
    """RBJ cookbook biquad coefficients."""
    f = min(max(f, 5.0), SR * 0.49)
    A = 10 ** (gain_db / 40.0)
    w0 = TAU * f / SR
    cw, sw = math.cos(w0), math.sin(w0)
    al = sw / (2.0 * q)
    if kind == "lp":
        b = [(1 - cw) / 2, 1 - cw, (1 - cw) / 2]
        a = [1 + al, -2 * cw, 1 - al]
    elif kind == "hp":
        b = [(1 + cw) / 2, -(1 + cw), (1 + cw) / 2]
        a = [1 + al, -2 * cw, 1 - al]
    elif kind == "bp":  # 0 dB peak
        b = [al, 0.0, -al]
        a = [1 + al, -2 * cw, 1 - al]
    elif kind == "notch":
        b = [1.0, -2 * cw, 1.0]
        a = [1 + al, -2 * cw, 1 - al]
    elif kind == "peak":
        b = [1 + al * A, -2 * cw, 1 - al * A]
        a = [1 + al / A, -2 * cw, 1 - al / A]
    elif kind in ("ls", "hs"):
        sq = 2 * math.sqrt(A) * al
        if kind == "ls":
            b = [A * ((A + 1) - (A - 1) * cw + sq), 2 * A * ((A - 1) - (A + 1) * cw), A * ((A + 1) - (A - 1) * cw - sq)]
            a = [(A + 1) + (A - 1) * cw + sq, -2 * ((A - 1) + (A + 1) * cw), (A + 1) + (A - 1) * cw - sq]
        else:
            b = [A * ((A + 1) + (A - 1) * cw + sq), -2 * A * ((A - 1) + (A + 1) * cw), A * ((A + 1) + (A - 1) * cw - sq)]
            a = [(A + 1) - (A - 1) * cw + sq, 2 * ((A - 1) - (A + 1) * cw), (A + 1) - (A - 1) * cw - sq]
    else:
        raise ValueError(kind)
    b = np.array(b) / a[0]
    a = np.array(a) / a[0]
    return b, a


def eq(x, kind, f, q=0.707, g=0.0):
    b, a = biquad(kind, f, q, g)
    return signal.lfilter(b, a, x)


def tv_biquad(x, kind, f, q=0.707, g=0.0, block=64):
    """Time-varying biquad, coefficients updated every `block` samples."""
    n = len(x)
    f = np.broadcast_to(np.asarray(f, float), (n,))
    q = np.broadcast_to(np.asarray(q, float), (n,))
    g = np.broadcast_to(np.asarray(g, float), (n,))
    y = np.empty(n)
    zi = np.zeros(2)
    for s in range(0, n, block):
        e = min(n, s + block)
        m = (s + e) // 2
        b, a = biquad(kind, f[m], q[m], g[m])
        y[s:e], zi = signal.lfilter(b, a, x[s:e], zi=zi)
    return y


def reson(x, f, bw):
    """Klatt resonator (unity gain at DC) – fixed."""
    r = math.exp(-math.pi * bw / SR)
    B = 2 * r * math.cos(TAU * f / SR)
    C = -r * r
    A = 1 - B - C
    return signal.lfilter([A], [1, -B, -C], x)


def resonbank(x, modes):
    """Parallel band-pass resonators: modes = [(freq, q, gain), ...]."""
    y = np.zeros(len(x))
    for f, q, g in modes:
        if f < SR * 0.45:
            y += g * eq(x, "bp", f, q)
    return y


def stft_shape(x, gain_fn, nfft=1024, hop=256):
    """Time-varying spectral shaping: gain_fn(freqs[:,None], times[None,:]) -> gains."""
    n = len(x)
    f, t, Z = signal.stft(x, fs=SR, nperseg=nfft, noverlap=nfft - hop, boundary="zeros", padded=True)
    G = gain_fn(f[:, None], t[None, :])
    _, y = signal.istft(Z * G, fs=SR, nperseg=nfft, noverlap=nfft - hop, boundary=True)
    y = y[:n]
    if len(y) < n:
        y = np.pad(y, (0, n - len(y)))
    return y


def comb(x, delay_s, fb=0.5, mix=1.0):
    """Feedback comb (resonant tube colour)."""
    d = max(1, int(round(delay_s * SR)))
    a = np.zeros(d + 1)
    a[0] = 1.0
    a[d] = -fb
    y = signal.lfilter([1.0], a, x) * (1 - fb)
    return (1 - mix) * x + mix * y


def tilt_lp1(x, f):
    """One-pole low-pass (gentle 6 dB/oct tilt)."""
    a = math.exp(-TAU * f / SR)
    return signal.lfilter([1 - a], [1, -a], x)


# ---------------------------------------------------------------------------
# Dynamics / colour
# ---------------------------------------------------------------------------


def sat(x, drive=2.0):
    """tanh saturation normalised so a full-scale input stays near full scale."""
    return np.tanh(drive * x) / math.tanh(drive)


def asym_sat(x, drive=2.0, bias=0.2):
    y = np.tanh(drive * (x + bias)) - math.tanh(drive * bias)
    return hp(y, 20, 1) / math.tanh(drive)


def envelope_follow(x, attack=0.002, release=0.08):
    pk = maximum_filter1d(np.abs(x), size=max(1, ns(attack)))
    a = math.exp(-1.0 / (release * SR))
    return signal.lfilter([1 - a], [1, -a], pk)


def compress(x, thresh_db=-18.0, ratio=4.0, attack=0.003, release=0.08, makeup=True):
    env = envelope_follow(x, attack, release) + 1e-9
    lev = 20 * np.log10(env)
    over = np.maximum(lev - thresh_db, 0.0)
    gain = db(-over * (1 - 1.0 / ratio))
    y = x * gain
    return norm(y, np.max(np.abs(x))) if makeup else y


# ---------------------------------------------------------------------------
# Oscillators
# ---------------------------------------------------------------------------


def phase_of(freq):
    """Phase (cycles) from a per-sample frequency array."""
    return np.cumsum(freq) / SR


def sine(freq, n=None, ph0=0.0):
    if np.isscalar(freq):
        return np.sin(TAU * (freq * tl(n) + ph0))
    return np.sin(TAU * (phase_of(freq) + ph0))


def _blep(t, dt):
    y = np.zeros_like(t)
    m = t < dt
    u = t[m] / dt[m]
    y[m] = u + u - u * u - 1.0
    m2 = t > 1.0 - dt
    u = (t[m2] - 1.0) / dt[m2]
    y[m2] = u * u + u + u + 1.0
    return y


def saw(freq, n=None, ph0=0.0):
    """PolyBLEP band-limited sawtooth."""
    if np.isscalar(freq):
        freq = np.full(n, float(freq))
    ph = (phase_of(freq) + ph0) % 1.0
    dt = np.abs(freq) / SR
    return 2.0 * ph - 1.0 - _blep(ph, dt)


def square(freq, n=None, ph0=0.0, duty=0.5):
    if np.isscalar(freq):
        freq = np.full(n, float(freq))
    ph = (phase_of(freq) + ph0) % 1.0
    dt = np.abs(freq) / SR
    y = np.where(ph < duty, 1.0, -1.0)
    y += _blep(ph, dt)
    y -= _blep((ph + 1.0 - duty) % 1.0, dt)
    return y


def additive(freq, n, amps, ph=None, maxf=9000.0):
    """Harmonic additive tone: amps[k] for harmonic k+1 (scalar or per-sample arrays)."""
    if np.isscalar(freq):
        freq = np.full(n, float(freq))
    p = phase_of(freq)
    y = np.zeros(n)
    for k, a in enumerate(amps):
        h = k + 1
        lim = (h * freq < maxf).astype(float)
        y += np.asarray(a) * lim * np.sin(TAU * h * p + (0.0 if ph is None else ph[k]))
    return y


def fm_tone(fc, n, ratio, index, mod_fb=0.0):
    """Simple 2-op FM: index may be an array (per-sample)."""
    if np.isscalar(fc):
        fc = np.full(n, float(fc))
    pc = phase_of(fc)
    pm = phase_of(fc * ratio)
    return np.sin(TAU * pc + np.asarray(index) * np.sin(TAU * pm))


# ---------------------------------------------------------------------------
# Synthetic spaces
# ---------------------------------------------------------------------------

_IR_CACHE = {}

SPACES = {
    # name: (t60, length, predelay, hf_ratio, lf_ratio, early reflections, er spread)
    "small": (0.45, 0.7, 0.004, 0.45, 1.0, 8, 0.025),
    "room": (0.8, 1.2, 0.006, 0.5, 1.1, 10, 0.04),
    "stone": (1.3, 1.8, 0.010, 0.45, 1.15, 12, 0.06),
    "hall": (2.4, 3.2, 0.018, 0.4, 1.2, 14, 0.08),
    "chapel": (3.2, 4.0, 0.022, 0.38, 1.25, 14, 0.09),
    "arena": (1.1, 1.6, 0.020, 0.35, 0.9, 6, 0.10),
    "outdoor": (0.6, 1.0, 0.030, 0.3, 0.8, 4, 0.12),
    "chasm": (3.8, 4.5, 0.060, 0.3, 1.3, 6, 0.2),
    "magic": (1.8, 2.6, 0.012, 0.75, 1.0, 10, 0.05),
    "plate": (1.2, 1.6, 0.002, 0.65, 0.9, 16, 0.015),
}


def get_ir(space):
    if space in _IR_CACHE:
        return _IR_CACHE[space]
    t60, length, pre, hf, lf, ner, spread = SPACES[space]
    rng = np.random.default_rng(zlib.crc32(("ir:" + space).encode()))
    n = ns(length)
    t = tl(n)
    nz = rng.standard_normal(n)
    out = np.zeros(n)
    edges = [(None, 250.0, lf), (250.0, 1200.0, 1.0), (1200.0, 4000.0, 0.5 + 0.5 * hf), (4000.0, None, hf)]
    for lo, hi, k in edges:
        if lo is None:
            b = lp(nz, hi, 4)
        elif hi is None:
            b = hp(nz, lo, 4)
        else:
            b = bp(nz, lo, hi, 3)
        out += b * 10 ** (-3.0 * t / (t60 * k))
    out *= 1 - np.exp(-t / 0.012)
    for i in range(ner):
        d = rng.uniform(0.003, spread)
        k = ns(d)
        if k < n:
            out[k] += rng.choice([-1, 1]) * rng.uniform(0.6, 1.4) * 2.0 * math.exp(-d / (spread * 1.5)) * np.std(out[:ns(0.05)]) * 6
    out = np.concatenate([np.zeros(ns(pre)), out])
    fl = ns(0.05)
    out[-fl:] *= np.linspace(1, 0, fl)
    out /= math.sqrt(np.sum(out ** 2))
    _IR_CACHE[space] = out
    return out


def verb(x, space="room", wet=0.25, dry=1.0, tail=None, pre_lp=None, pre_hp=None):
    """Convolution reverb. Output = dry*x + wet*rev, extended by `tail` seconds (default: full IR)."""
    ir = get_ir(space)
    src = x
    if pre_lp:
        src = lp(src, pre_lp, 2)
    if pre_hp:
        src = hp(src, pre_hp, 2)
    r = signal.fftconvolve(src, ir)
    extra = len(ir) - 1 if tail is None else ns(tail)
    n = len(x) + extra
    y = np.zeros(n)
    y[: len(x)] += dry * x
    m = min(n, len(r))
    y[:m] += wet * r[:m]
    return y


def verb_loop(x, space="room", wet=0.25, dry=1.0, pre_lp=None, pre_hp=None):
    """Circular reverb for seamless loops (tail wraps to the start)."""
    ir = get_ir(space)
    src = x
    if pre_lp:
        src = periodic(src, lambda s: lp(s, pre_lp, 2))
    if pre_hp:
        src = periodic(src, lambda s: hp(s, pre_hp, 2))
    n = len(x)
    L = 1
    while L < n + len(ir):
        L *= 2
    # circular convolution with period n
    irw = np.zeros(n)
    for k in range(0, len(ir), n):
        seg_ = ir[k:k + n]
        irw[: len(seg_)] += seg_
    r = np.fft.irfft(np.fft.rfft(src) * np.fft.rfft(irw), n)
    return dry * x + wet * r


def periodic(x, fn, reps=3):
    """Apply a time-invariant process to a loop so the result is still seamless."""
    n = len(x)
    y = fn(np.tile(x, reps))
    return y[(reps - 1) * n: reps * n]


# ---------------------------------------------------------------------------
# Building blocks: whooshes, thumps, modal impacts, crackle, creaks, bubbles
# ---------------------------------------------------------------------------


def bump(n, peak=0.5, sharp=3.0, tail=1.0):
    """Smooth asymmetric 0..1..0 envelope peaking at fraction `peak`."""
    u = np.linspace(0, 1, n)
    p = min(max(peak, 0.02), 0.98)
    a = np.where(u <= p, (u / p) ** sharp * np.exp(sharp * (1 - u / p)), 0)
    v = np.clip((u - p) / (1 - p), 0, 1)
    b = np.where(u > p, (1 - v) ** (sharp * tail) * np.exp(-0.5 * sharp * tail * v * v), 0)
    e = a + b
    e[0] = 0.0
    e[-1] = 0.0
    return e / max(e.max(), 1e-12)


def whoosh(dur, rng, f_lo=300.0, f_hi=2500.0, peak=0.45, bw=1.2, sharp=2.5, slope=-2.0, tail=1.0, flutter=0.0,
           flutter_rate=18.0, env=None, sweep=None):
    """Band-pass noise whose centre rises to f_hi at the envelope peak then falls (Doppler-ish).

    env/sweep (arrays 0..1) override the default bump-shaped loudness / centre-frequency motion."""
    n = ns(dur)
    x = colored(n, rng, slope)
    shape = bump(n, peak, sharp, tail)
    e = shape if env is None else np.asarray(env, float)
    s = shape if sweep is None else np.asarray(sweep, float)
    if flutter > 0:
        e = e * (1.0 - flutter * (0.5 + 0.5 * np.sin(TAU * flutter_rate * tl(n) + rng.uniform(0, TAU))))

    def g(f, t):
        idx = np.clip(np.round(t * SR).astype(int), 0, n - 1)
        fc = f_lo * (f_hi / f_lo) ** s[idx]
        return np.exp(-0.5 * (np.log2(np.maximum(f, 1.0) / fc) / (bw / 2.0)) ** 2)

    return stft_shape(x, g, nfft=1024, hop=128) * e


def thump(dur, f0=90.0, f1=45.0, decay=0.05, pitch_tau=0.02, rng=None, noise=0.4, noise_lp=600.0,
          click=0.15, noise_decay=None):
    """Low body impact: pitch-dropping sine + low-passed noise burst + tiny click (end-faded)."""
    n = ns(dur)
    t = tl(n)
    f = f1 + (f0 - f1) * np.exp(-t / pitch_tau)
    body = np.sin(TAU * phase_of(f)) * np.exp(-t / decay)
    body *= 1 - np.exp(-t / 0.0015)
    y = body
    if rng is not None and noise > 0:
        nd = noise_decay or decay * 0.45
        y = y + noise * lp(white(n, rng), noise_lp, 2) * np.exp(-t / nd) * 2.0
    if rng is not None and click > 0:
        c = hp(white(n, rng), 1500, 2) * np.exp(-t / 0.0015)
        y = y + click * c
    return endfade(y, 0.2)


def punch(dur, rng, f=500.0, decay=0.02, attack=0.001, order=2):
    """Low-passed noise burst: the meaty, non-tonal part of a hit."""
    n = ns(dur)
    t = tl(n)
    y = lp(white(n, rng), f, order) * np.exp(-t / decay) * (1 - np.exp(-t / attack))
    return endfade(y / 0.3, 0.2)


def modal(dur, freqs, t60s, amps, rng=None, phase_rand=True, attack=0.0005, glide=0.0):
    """Sum of exponentially decaying sinusoids (modal synthesis)."""
    n = ns(dur)
    t = tl(n)
    y = np.zeros(n)
    for f, T, a in zip(freqs, t60s, amps):
        if f >= 9500.0 or a == 0:
            continue
        a = a / (1.0 + (f / 7000.0) ** 6)
        ph = rng.uniform(0, TAU) if (rng is not None and phase_rand) else 0.0
        if glide:
            fr = f * (1 + glide * np.exp(-t / 0.02))
            s = np.sin(TAU * phase_of(fr) + ph)
        else:
            s = np.sin(TAU * f * t + ph)
        y += a * s * 10 ** (-3.0 * t / max(T, 1e-4))
    if attack > 0:
        y *= 1 - np.exp(-t / attack)
    return endfade(y, 0.15)


def material(kind, dur, rng, f0=None, hard=1.0, size=1.0, damp=1.0):
    """Struck-object presets via modal synthesis + excitation noise."""
    n = ns(dur)
    t = tl(n)
    if kind == "wood":
        f0 = f0 or 420.0
        ratios = [1.0, 2.71, 5.2, 8.6, 13.0, 1.52, 3.9]
        fr = [f0 * r * rng.uniform(0.97, 1.03) for r in ratios]
        T = [0.12 * damp, 0.07 * damp, 0.045 * damp, 0.03 * damp, 0.02 * damp, 0.09 * damp, 0.05 * damp]
        A = [1.0, 0.55, 0.32, 0.18, 0.1, 0.4, 0.25]
        y = modal(dur, fr, T, A, rng)
        ex = bp(white(n, rng), 600 * hard, 4500 * hard, 2) * np.exp(-t / 0.004)
        y = y + 0.6 * ex
    elif kind == "box":  # hollow crate/barrel
        f0 = f0 or 180.0
        fr = [f0 * r * rng.uniform(0.96, 1.04) for r in [1.0, 1.58, 2.3, 3.1, 4.6, 6.2, 8.9, 12.4]]
        T = [d * damp for d in [0.16, 0.12, 0.09, 0.07, 0.05, 0.04, 0.03, 0.02]]
        A = [1.0, 0.8, 0.6, 0.5, 0.35, 0.25, 0.15, 0.08]
        y = modal(dur, fr, T, A, rng)
        ex = bp(white(n, rng), 500 * hard, 4000 * hard, 2) * np.exp(-t / 0.005)
        y = y + 0.7 * ex
    elif kind == "metal":
        f0 = f0 or 600.0
        ratios = [1.0, 2.756, 5.404, 8.933, 13.34, 1.42, 3.6, 6.9]
        fr = [f0 * r * rng.uniform(0.99, 1.01) for r in ratios]
        T = [d * damp for d in [1.2, 0.9, 0.6, 0.4, 0.25, 1.0, 0.7, 0.35]]
        A = [1.0, 0.7, 0.45, 0.3, 0.15, 0.5, 0.35, 0.2]
        y = modal(dur, fr, T, A, rng)
        ex = hp(white(n, rng), 2000, 2) * np.exp(-t / 0.002)
        y = y + 0.3 * hard * ex
    elif kind == "stone":
        f0 = f0 or 900.0
        fr = [f0 * r * rng.uniform(0.9, 1.1) for r in [1.0, 1.37, 1.93, 2.6, 3.4, 4.3, 5.5, 7.0]]
        T = [d * damp for d in [0.05, 0.04, 0.035, 0.03, 0.025, 0.02, 0.016, 0.012]]
        A = [1.0, 0.9, 0.8, 0.7, 0.55, 0.45, 0.3, 0.2]
        y = modal(dur, fr, T, A, rng)
        ex = bp(white(n, rng), 700 * hard, 6000 * hard, 2) * np.exp(-t / 0.003)
        y = 0.6 * y + 1.0 * ex
    elif kind == "glass":
        f0 = f0 or 2400.0
        fr = [f0 * r * rng.uniform(0.98, 1.02) for r in [1.0, 2.32, 4.25, 6.63]]
        T = [d * damp for d in [0.35, 0.22, 0.12, 0.08]]
        A = [1.0, 0.5, 0.3, 0.15]
        y = modal(dur, fr, T, A, rng)
        ex = hp(white(n, rng), 3000, 2) * np.exp(-t / 0.0015)
        y = y + 0.25 * ex
    else:
        raise ValueError(kind)
    return endfade(y, 0.15)


def impulses(dur, rng, rate, t0=0.0, decay_rate=None, jitter=1.0):
    """Poisson event times in [t0, dur) with optional exponentially decaying rate."""
    times = []
    t = t0
    while True:
        r = rate * (math.exp(-(t - t0) / decay_rate) if decay_rate else 1.0)
        if r < 0.05:
            break
        t += rng.exponential(1.0 / r) if jitter else 1.0 / r
        if t >= dur:
            break
        times.append(t)
    return times


def crackle(dur, rng, rate=40.0, band=(1200.0, 6000.0), alpha=1.6, decay_rate=None, t0=0.0, clen=0.0012,
            wrap=False, cap=8.0):
    """Fire-like crackle: Poisson clicks with power-law amplitudes."""
    n = ns(dur)
    y = np.zeros(n)
    for tt in impulses(dur, rng, rate, t0, decay_rate):
        a = min(rng.pareto(alpha) + 0.2, cap)
        L = ns(clen * rng.uniform(0.5, 2.5))
        k = white(L, rng) * np.exp(-np.arange(L) / (L / 4.0))
        if wrap:
            put_wrap(y, k, tt, a)
        else:
            put(y, k, tt, a)
    if wrap:
        return periodic(y, lambda s: bp(s, band[0], band[1], 2))
    return bp(y, band[0], band[1], 2)


def bubble(f0, dur=0.06, rise=0.6, tau=None, rng=None):
    """Single bubble 'bloop': sine whose pitch rises as it decays."""
    n = ns(dur)
    t = tl(n)
    tau = tau or dur / 3.5
    f = f0 * (1 + rise * t / dur)
    y = np.sin(TAU * phase_of(f)) * np.exp(-t / tau)
    y *= 1 - np.exp(-t / 0.0008)
    return y


def drip(f0=1400.0, rng=None, dur=0.25):
    """Water drip 'plink': fast upward chirp + small splash."""
    n = ns(dur)
    t = tl(n)
    f = f0 * (1 + 1.4 * (1 - np.exp(-t / 0.02)))
    y = np.sin(TAU * phase_of(f)) * np.exp(-t / 0.035) * (1 - np.exp(-t / 0.0006))
    if rng is not None:
        sp = bp(white(n, rng), 2000, 6000, 2) * np.exp(-t / 0.004) * 0.15
        y = y + sp
    return y


def creak(dur, rng, rate, modes, jitter=0.25, pulse_len=0.0015, amp=None, irregular=0.3):
    """Stick-slip friction creak: jittery pulse train (rate Hz array) through a resonant body."""
    n = ns(dur)
    rate = np.broadcast_to(np.asarray(rate, float), (n,))
    jit = 1.0 + jitter * smooth_noise(n, rng, 40.0)
    ph = np.cumsum(np.maximum(rate * jit, 0.0)) / SR
    idx = np.nonzero(np.diff(np.floor(ph)) > 0)[0] + 1
    pulses = np.zeros(n)
    amps = 1.0 + irregular * rng.standard_normal(len(idx))
    pulses[idx] = np.clip(amps, 0.1, 3.0)
    L = max(2, ns(pulse_len))
    kern = np.exp(-np.arange(L) / (L / 3.0)) * np.hanning(2 * L)[L:] if L > 2 else np.ones(L)
    src = signal.lfilter(kern, [1.0], pulses)
    y = resonbank(src, modes)
    if amp is not None:
        y *= amp
    return y


def rustle(dur, rng, density=120.0, band=(1500.0, 7000.0), grain=(0.004, 0.02), env=None):
    """Cloth/leather/feather rustle: many short noise grains."""
    n = ns(dur)
    y = np.zeros(n)
    for tt in impulses(dur, rng, density):
        L = ns(rng.uniform(*grain))
        g = white(L, rng) * np.hanning(L)
        put(y, g, tt, rng.uniform(0.2, 1.0))
    y = lp(bp(y, band[0], band[1], 2), band[1] * 1.25, 2)
    if env is not None:
        y *= env
    return y


def debris(dur, rng, t0=0.0, rate=60.0, decay_rate=0.25, kinds=("stone",), f_range=(600, 3000), size=0.6,
           amp_decay=0.6):
    """Falling/bouncing small debris after a break: many tiny modal impacts."""
    n = ns(dur)
    y = np.zeros(n)
    for tt in impulses(dur, rng, rate, t0, decay_rate):
        k = kinds[rng.integers(len(kinds))]
        f0 = math.exp(rng.uniform(math.log(f_range[0]), math.log(f_range[1])))
        d = 0.08 if k != "metal" else 0.25
        s = material(k, d, rng, f0=f0, damp=0.6 * size)
        a = rng.uniform(0.15, 1.0) * math.exp(-(tt - t0) / amp_decay)
        put(y, s, tt, a)
    return y


# ---------------------------------------------------------------------------
# Source-filter voice model (for every creature vocal)
# ---------------------------------------------------------------------------

VOWELS = {
    #      F1   F2    F3    F4    F5          B1  B2   B3   B4   B5
    "i": ([270, 2290, 3010, 3500, 4200], [60, 90, 150, 200, 260]),
    "I": ([390, 1990, 2550, 3500, 4200], [70, 100, 140, 200, 260]),
    "e": ([530, 1840, 2480, 3500, 4200], [70, 100, 140, 200, 260]),
    "ae": ([660, 1720, 2410, 3450, 4200], [80, 100, 140, 200, 260]),
    "a": ([730, 1090, 2440, 3400, 4200], [90, 110, 140, 200, 260]),
    "o": ([570, 840, 2410, 3400, 4200], [80, 100, 140, 200, 260]),
    "U": ([440, 1020, 2240, 3400, 4200], [70, 100, 140, 200, 260]),
    "u": ([300, 870, 2240, 3400, 4200], [60, 90, 140, 200, 260]),
    "A": ([640, 1190, 2390, 3400, 4200], [80, 100, 140, 200, 260]),
    "er": ([490, 1350, 1690, 3300, 4200], [70, 100, 140, 200, 260]),
    "@": ([500, 1500, 2500, 3500, 4200], [70, 100, 140, 200, 260]),
    "N": ([250, 1100, 2300, 3400, 4200], [60, 200, 250, 300, 300]),  # nasal-ish murmur
}


def _tracks(n, vowel):
    """vowel: name | [(t, name), ...] -> (F[n,5], B[n,5])."""
    if isinstance(vowel, str):
        F, B = VOWELS[vowel]
        return np.tile(np.array(F, float), (n, 1)), np.tile(np.array(B, float), (n, 1))
    ts = np.array([p[0] for p in vowel], float)
    t = tl(n)
    F = np.zeros((n, 5))
    B = np.zeros((n, 5))
    for k in range(5):
        fv = np.array([VOWELS[p[1]][0][k] for p in vowel], float)
        bv = np.array([VOWELS[p[1]][1][k] for p in vowel], float)
        F[:, k] = np.exp(np.interp(t, ts, np.log(fv)))
        B[:, k] = np.interp(t, ts, bv)
    return F, B


def formant_filter(src, F, B, block=64, nform=5):
    """Cascade of Klatt resonators with time-varying formants."""
    n = len(src)
    y = src.copy()
    for k in range(nform):
        out = np.empty(n)
        zi = np.zeros(2)
        for s in range(0, n, block):
            e = min(n, s + block)
            m = (s + e) // 2
            f = min(F[m, k], SR * 0.45)
            bw = B[m, k]
            r = math.exp(-math.pi * bw / SR)
            Bc = 2 * r * math.cos(TAU * f / SR)
            Cc = -r * r
            A = 1 - Bc - Cc
            out[s:e], zi = signal.lfilter([A], [1.0, -Bc, -Cc], y[s:e], zi=zi)
        y = out
    return y


def glottal(f0, rng, oq=0.6, jitter=0.01, shimmer=0.06, sub=0.0, os_factor=2, sub_pattern=None):
    """Band-limited glottal flow derivative (KLGLOTT88-style) for a per-sample f0 array.

    Returns (pulse_train, open_mask) at SR."""
    n = len(f0)
    f = f0 * (1.0 + jitter * smooth_noise(n, rng, 12.0) + 0.5 * jitter * smooth_noise(n, rng, 4.0))
    f = np.maximum(f, 5.0)
    n2 = n * os_factor
    f2 = np.interp(np.arange(n2) / os_factor, np.arange(n), f)
    ph0 = rng.uniform(0, 1)
    k1 = np.floor(np.cumsum(f2) / (SR * os_factor) + ph0).astype(np.int64)
    k1 -= k1.min()
    cyc = 1.0 + 0.6 * jitter * rng.standard_normal(int(k1.max()) + 2)  # cycle-to-cycle jitter
    f2 = f2 * np.clip(cyc[k1], 0.7, 1.3)
    ph = np.cumsum(f2) / (SR * os_factor) + ph0
    tau = ph % 1.0
    k = np.floor(ph).astype(np.int64)
    k -= k.min()
    oqa = np.broadcast_to(np.asarray(oq, float), (n,))
    oq2 = np.interp(np.arange(n2) / os_factor, np.arange(n), oqa)
    x = tau / oq2
    g = np.where(tau < oq2, 2 * x - 3 * x * x, 0.0)
    K = int(k.max()) + 1
    amp_k = np.clip(1.0 + shimmer * rng.standard_normal(K), 0.2, 2.0)
    if np.ndim(sub) == 0 and sub > 0 or np.ndim(sub) > 0:
        suba = np.broadcast_to(np.asarray(sub, float), (n,))
        sub2 = np.interp(np.arange(n2) / os_factor, np.arange(n), suba)
        patt = (k % 2).astype(float) if sub_pattern is None else sub_pattern[k % len(sub_pattern)]
        g = g * (1.0 - sub2 * patt)
    g = g * amp_k[k]
    g = signal.resample_poly(g, 1, os_factor)[:n]
    open_mask = np.interp(np.arange(n), np.arange(n2) / os_factor, (tau < oq2).astype(float))
    return g, open_mask


def voice(dur, rng, f0, amp, vowel="a", scale=1.0, bw=1.0, oq=0.6, jitter=0.012, shimmer=0.08, sub=0.0,
          breath=0.05, aspir=None, tilt=None, growl=0.0, growl_rate=35.0, nasal=0.0, block=64, edge_fade=True):
    """Source-filter vocal.

    f0/amp: arrays (len n) or breakpoint lists [(t, v)]. vowel: name or [(t, name)].
    scale: formant scaling (vocal tract size, <1 = bigger creature).
    aspir: pure noise excitation level (array/pts) for 'h' and whispers.
    growl: random amplitude roughness at ~growl_rate Hz.  sub: subharmonic depth.
    """
    n = ns(dur)
    f0a = curve(dur, f0, "exp") if isinstance(f0, list) else np.broadcast_to(np.asarray(f0, float), (n,)).copy()
    ampa = curve(dur, amp, "cos") if isinstance(amp, list) else np.broadcast_to(np.asarray(amp, float), (n,)).copy()
    g, om = glottal(f0a, rng, oq, jitter, shimmer, sub)
    if growl > 0:
        gm = 1.0 + growl * smooth_noise(n, rng, growl_rate)
        g = g * np.clip(gm, 0.0, 3.0)
    nz = white(n, rng)
    nzh = hp(nz, 400, 2)
    src = ampa * (g * 3.0 + breath * nzh * (0.35 + 0.65 * om))
    if aspir is not None:
        asp = curve(dur, aspir, "cos") if isinstance(aspir, list) else np.asarray(aspir)
        src = src + asp * nzh * 0.6
    if tilt:
        src = tilt_lp1(src, tilt)
    F, B = _tracks(n, vowel)
    F = F * scale
    B = B * bw * (0.6 + 0.4 * scale)
    y = formant_filter(src, F, B, block=block)
    if nasal > 0:
        y = (1 - nasal) * y + nasal * eq(eq(y, "notch", 900 * scale, 1.2), "peak", 2200 * scale, 1.5, 6)
    return endfade(y, 0.03, 0.006) if edge_fade else y


# ---------------------------------------------------------------------------
# espeak-ng gibberish source (built-in formant voices only; resampled, never words)
# ---------------------------------------------------------------------------


def espeak(text, voice_name="en-us+m3", pitch=50, speed=170, phonemes=True):
    """Render gibberish phoneme strings with espeak-ng's built-in formant synthesizer.

    Only espeak-ng's own (non-MBROLA) voices are used, so no recorded speech enters the output.
    Returns mono float at SR."""
    with tempfile.TemporaryDirectory() as td:
        p = os.path.join(td, "v.wav")
        txt = f"[[{text}]]" if phonemes else text
        subprocess.run(["espeak-ng", "-v", voice_name, "-p", str(pitch), "-s", str(speed), "-w", p, txt],
                       check=True, capture_output=True)
        sr, x = wavfile.read(p)
    x = x.astype(np.float64) / 32768.0
    if x.ndim > 1:
        x = x.mean(axis=1)
    g = math.gcd(SR, sr)
    x = signal.resample_poly(x, SR // g, sr // g)
    nz = np.nonzero(np.abs(x) > 1e-3)[0]
    if len(nz):
        x = x[max(0, nz[0] - 100): nz[-1] + 100]
    return x


def resample_speed(x, ratio):
    """Play back `ratio` times faster (pitch and formants up, shorter)."""
    from fractions import Fraction
    fr = Fraction(ratio).limit_denominator(200)
    return signal.resample_poly(x, fr.denominator, fr.numerator)


def ffmpeg_af(x, af):
    """Run an ffmpeg audio filter graph on a mono float signal (e.g. rubberband)."""
    p = subprocess.run(
        ["ffmpeg", "-v", "error", "-f", "f32le", "-ar", str(SR), "-ac", "1", "-i", "pipe:0", "-af", af,
         "-f", "f32le", "-ar", str(SR), "-ac", "1", "pipe:1"],
        input=x.astype(np.float32).tobytes(), capture_output=True, check=True)
    return np.frombuffer(p.stdout, dtype=np.float32).astype(np.float64)


# ---------------------------------------------------------------------------
# Musical helpers (brass, strings, choir, bells, glass)
# ---------------------------------------------------------------------------


def adsr(n, a=0.01, d=0.1, s=0.7, r=0.2, hold=None):
    """ADSR envelope over n samples; release starts at `hold` seconds (default n - r)."""
    t = tl(n)
    total = n / SR
    h = total - r if hold is None else hold
    e = np.where(t < a, t / max(a, 1e-6), s + (1 - s) * np.exp(-(t - a) / max(d, 1e-6)))
    rel = np.clip((t - h) / max(r, 1e-6), 0, 1)
    e = e * np.where(t > h, (1 - rel) ** 2, 1.0)
    return e


def brass(dur, rng, f0, amp, bright=1.0, vib=0.004, scoop=0.0, breath=0.03):
    """Brass-like tone: additive harmonics whose brightness follows loudness."""
    n = ns(dur)
    t = tl(n)
    f0a = np.broadcast_to(np.asarray(f0, float), (n,)).copy() if not isinstance(f0, list) else curve(dur, f0, "exp")
    ampa = np.broadcast_to(np.asarray(amp, float), (n,)) if not isinstance(amp, list) else curve(dur, amp, "cos")
    if scoop:
        f0a = f0a * 2 ** (-scoop * np.exp(-t / 0.05) / 12.0)
    f0a = f0a * (1 + vib * np.sin(TAU * 5.2 * t + rng.uniform(0, TAU)) * np.clip(t / 0.3, 0, 1))
    f0a = f0a * (1 + 0.002 * smooth_noise(n, rng, 8))
    p = phase_of(f0a)
    y = np.zeros(n)
    lvl = np.clip(ampa, 0, 1)
    for h in range(1, 40):
        lim = (h * f0a < 8000).astype(float)
        if not lim.any():
            break
        roll = np.exp(-(h - 1) / (1.2 + 7.0 * bright * lvl ** 1.5))
        y += lim * roll * np.sin(TAU * h * p + rng.uniform(0, TAU)) / (1 + 0.15 * h)
    y = eq(y, "peak", 1200, 1.0, 4)
    y = y * ampa
    y += breath * bp(white(n, rng), 1000, 4000, 2) * ampa
    return y


def strings(dur, rng, freqs, amp, voices=3, detune=0.006, bright=2200.0):
    """Ensemble pad: detuned saws, gentle vibrato, low-passed."""
    n = ns(dur)
    t = tl(n)
    y = np.zeros(n)
    for f in freqs:
        for v in range(voices):
            d = 1 + detune * (v - (voices - 1) / 2) + 0.001 * rng.standard_normal()
            vib = 1 + 0.003 * np.sin(TAU * rng.uniform(4.5, 6.0) * t + rng.uniform(0, TAU))
            y += saw(f * d * vib, ph0=rng.uniform(0, 1))
    y = lp(y, bright, 2)
    y = lp(y, bright * 1.5, 2)
    ampa = np.broadcast_to(np.asarray(amp, float), (n,)) if not isinstance(amp, list) else curve(dur, amp, "cos")
    return y * ampa / (len(freqs) * voices)


def choir(dur, rng, freqs, amp, vowel="a", scale=1.0, breath=0.12, vib=0.008, per_note=2):
    """Synthetic choir: several formant voices per note with vibrato and breath."""
    n = ns(dur)
    t = tl(n)
    y = np.zeros(n)
    ampa = np.broadcast_to(np.asarray(amp, float), (n,)) if not isinstance(amp, list) else curve(dur, amp, "cos")
    for f in freqs:
        for v in range(per_note):
            rate = rng.uniform(4.6, 5.8)
            fv = f * (1 + rng.uniform(-0.004, 0.004)) * (1 + vib * np.sin(TAU * rate * t + rng.uniform(0, TAU)))
            sc = scale * rng.uniform(0.96, 1.05) * (1.12 if np.mean(f) > 330 else 1.0)
            y += voice(dur, rng, fv, ampa, vowel, scale=sc, oq=0.7, jitter=0.006, shimmer=0.04, breath=breath,
                       tilt=3500)
    return y / (len(freqs) * per_note)


def bell(dur, rng, f, strike=1.0, decay=1.0, bright=1.0):
    """Church bell: hum, prime, tierce, quint, nominal + upper partials, each a beating doublet."""
    parts = [(0.5, 1.0, 1.0), (1.0, 0.8, 0.75), (1.2, 0.55, 0.6), (1.5, 0.35, 0.45), (2.0, 0.9, 0.5),
             (2.5, 0.3 * bright, 0.32), (2.67, 0.35 * bright, 0.3), (3.0, 0.22 * bright, 0.25),
             (4.0, 0.15 * bright, 0.18), (5.33, 0.08 * bright, 0.12)]
    n = ns(dur)
    t = tl(n)
    y = np.zeros(n)
    for r, a, dk in parts:
        fr = f * r
        if fr > 9000:
            continue
        T = 2.5 * decay * dk * (600.0 / max(fr, 100)) ** 0.25
        beat = rng.uniform(0.6, 2.2)
        y += a * (np.sin(TAU * fr * t + rng.uniform(0, TAU)) + 0.6 * np.sin(TAU * (fr + beat) * t)) * 10 ** (-3 * t / T)
    y *= 1 - np.exp(-t / 0.001)
    clank = bp(white(n, rng), 1500, 6000, 2) * np.exp(-t / 0.006) * 0.8 * strike
    return y + clank


def glass_ping(dur, rng, f, bright=1.0, decay=1.0, beat=2.5):
    """Glassy ping: sine with detuned partner + inharmonic glass partials + soft FM tick."""
    n = ns(dur)
    t = tl(n)
    y = np.sin(TAU * f * t) * 10 ** (-3 * t / (1.1 * decay))
    y += 0.5 * np.sin(TAU * (f + beat) * t + 1.0) * 10 ** (-3 * t / (0.9 * decay))
    for r, a, T in [(2.0, 0.28, 0.55), (2.76, 0.22 * bright, 0.35), (4.07, 0.12 * bright, 0.2), (5.4, 0.06 * bright, 0.12)]:
        if f * r < 9500:
            y += a * np.sin(TAU * f * r * 1.0015 * t + rng.uniform(0, TAU)) * 10 ** (-3 * t / (T * decay))
    tick = np.sin(TAU * f * 3.5 * t + 2 * np.sin(TAU * f * 1.0 * t)) * np.exp(-t / 0.004) * 0.25 * bright
    y = y + tick
    y *= 1 - np.exp(-t / 0.0012)
    return y


def fm_bell(dur, rng, f, ratio=3.5, index=3.0, decay=1.0, idx_decay=0.15):
    n = ns(dur)
    t = tl(n)
    idx = index * np.exp(-t / idx_decay)
    y = fm_tone(f, n, ratio, idx) * 10 ** (-3 * t / (1.6 * decay))
    return y * (1 - np.exp(-t / 0.001))


def drum(dur, rng, f0=110.0, f1=70.0, decay=0.35, skin=0.4, bright=1500.0):
    """Frame drum / timpani-ish hit."""
    n = ns(dur)
    t = tl(n)
    f = f1 + (f0 - f1) * np.exp(-t / 0.04)
    y = np.sin(TAU * phase_of(f)) * np.exp(-t / decay)
    y += 0.5 * np.sin(TAU * phase_of(f * 1.5)) * np.exp(-t / (decay * 0.6))
    y += 0.3 * np.sin(TAU * phase_of(f * 2.24)) * np.exp(-t / (decay * 0.4))
    y += skin * lp(white(n, rng), bright, 2) * np.exp(-t / 0.03)
    return y * (1 - np.exp(-t / 0.001))


# ---------------------------------------------------------------------------
# Loudness (K-weighted momentary maximum) for the manifest's volume suggestion
# ---------------------------------------------------------------------------


def k_weight(x):
    # BS.1770 pre-filter (high shelf) + RLB high-pass, RBJ approximations at 44.1 kHz
    b1, a1 = biquad("hs", 1681.97, 0.7072, 3.9998)
    b2, a2 = biquad("hp", 38.135, 0.5003)
    return signal.lfilter(b2, a2, signal.lfilter(b1, a1, x))


def loudness(x, win=0.15):
    """Max short-window K-weighted loudness (LUFS-like). 150 ms is close to the ear's integration
    time, so short impacts and long sounds compare fairly."""
    y = k_weight(np.concatenate([x, np.zeros(ns(win))]))
    w = ns(win)
    c = np.concatenate([[0.0], np.cumsum(y * y)])
    p = (c[w:] - c[:-w]) / w
    return -0.691 + 10 * math.log10(max(p.max(), 1e-12))


# ---------------------------------------------------------------------------
# Sound registry
# ---------------------------------------------------------------------------

REGISTRY = []
_BY_ID = {}


def sfx(sid, cat, desc, variants=1, loop=False, level=-20.0, jitter=0.05, dist=(1.5, 25.0), q=5, limit=False):
    """Register a generator fn(rng, v) -> samples.

    level: target short-window K-weighted loudness (LUFS-like) at the suggested volume, which
    sets the manifest `volume` so everything sits together in the mix.
    limit: allow the look-ahead limiter to trade a few dB of transient peak for body (impacts)."""

    def deco(fn):
        entry = dict(id=sid, category=cat, description=desc, variants=variants, loop=loop, level=level,
                     jitter=jitter, dist=dist, q=q, limit=limit, fn=fn)
        REGISTRY.append(entry)
        _BY_ID[sid] = entry
        return fn

    return deco


def file_names(e):
    if e["variants"] == 1:
        return [e["id"]]
    return [f"{e['id']}_{i + 1}" for i in range(e["variants"])]


def seed_for(name):
    return zlib.crc32(("brainwash-sfx:" + name).encode())


# ===========================================================================
# SOUND DEFINITIONS
# ===========================================================================

# ---------------------------------------------------------------------------
# Player
# ---------------------------------------------------------------------------


@sfx("staff_swing", "player", "Wooden staff whooshing through the air; play on every melee swing.",
     variants=3, level=-19, jitter=0.07, dist=(1.0, 15))
def _staff_swing(rng, v):
    dur = [0.30, 0.27, 0.33][v]
    pk = [0.42, 0.5, 0.36][v]
    lo, hi = [(260, 1500), (310, 1900), (220, 1250)][v]
    body = whoosh(dur, rng, lo, hi, peak=pk, bw=1.5, sharp=2.6, slope=-2.5)
    edge = 0.3 * whoosh(dur, rng, lo * 2.6, hi * 2.3, peak=pk + 0.04, bw=0.9, sharp=4.0, slope=-1.0)
    low = 0.7 * whoosh(dur, rng, 80, 240, peak=pk - 0.03, bw=1.6, sharp=2.0, slope=-4.0)
    x = body + edge + low
    if v == 2:  # a slightly rolling staff: subtle rotation flutter
        x *= 1 - 0.3 * (0.5 + 0.5 * np.sin(TAU * 21 * tl(len(x))))
    return verb(x, "small", 0.07, tail=0.04)


@sfx("staff_hit_flesh", "player", "Blunt staff thud landing on a creature's body.",
     variants=3, level=-14, jitter=0.07, dist=(1.0, 20), limit=True)
def _staff_hit_flesh(rng, v):
    dur = 0.25
    n = ns(dur)
    t = tl(n)
    x = punch(dur, rng, f=[650, 520, 760][v], decay=0.022)
    x += 0.7 * thump(dur, f0=[125, 105, 140][v], f1=[62, 55, 68][v], decay=0.035, pitch_tau=0.014)
    slap = bp(white(n, rng), 700, 3000, 2) * np.exp(-t / 0.009) * (1 - np.exp(-t / 0.0006))
    x += [0.9, 1.0, 0.7][v] * slap
    x += 0.3 * material("wood", dur, rng, f0=[520, 610, 470][v], damp=0.5)
    sq = tv_biquad(white(n, rng), "bp", curve(dur, [(0, 1500), (0.07, 480), (0.25, 280)], "exp"), 2.5)
    x += [0.25, 0.4, 0.3][v] * endfade(sq * np.exp(-t / 0.04) * (1 - np.exp(-t / 0.004)), 0.2)
    x = sat(norm(x) * 1.3, 1.3)
    return verb(x, "small", 0.07, tail=0.06)


@sfx("staff_hit_hard", "player", "Staff cracking against a hard surface (stone, wood, ice, armour).",
     variants=2, level=-14, jitter=0.07, dist=(1.0, 22), limit=True)
def _staff_hit_hard(rng, v):
    dur = 0.25
    n = ns(dur)
    t = tl(n)
    staff = material("wood", dur, rng, f0=[560, 650][v], hard=1.3, damp=0.9)
    if v == 0:  # stone
        surf = 0.9 * material("stone", dur, rng, f0=1050, hard=1.2, damp=0.8)
        surf += 0.2 * debris(dur, rng, t0=0.01, rate=60, decay_rate=0.05, kinds=("stone",), f_range=(1500, 4000),
                             size=0.4)
    else:  # hollow wood / ice
        surf = 0.8 * material("box", dur, rng, f0=230, hard=1.1, damp=0.7)
        surf += 0.25 * material("glass", dur, rng, f0=2300, damp=0.4)
    crack = hp(white(n, rng), 1800, 2) * np.exp(-t / 0.0022)
    x = staff + surf + 0.6 * crack + 0.5 * punch(dur, rng, f=900, decay=0.012)
    x = sat(norm(x) * 1.3, 1.4)
    return verb(x, "room", 0.12, tail=0.12)


@sfx("kick_whoosh", "player", "Quick leg whoosh at the start of a kick.",
     variants=2, level=-21, jitter=0.08, dist=(1.0, 12))
def _kick_whoosh(rng, v):
    dur = [0.2, 0.22][v]
    n = ns(dur)
    w = whoosh(dur, rng, [190, 160][v], [850, 700][v], peak=[0.45, 0.5][v], bw=1.7, sharp=2.2, slope=-3.0)
    cl = rustle(dur, rng, density=260, band=(1300, 5200), grain=(0.003, 0.012), env=bump(n, 0.4, 2.0))
    return w + 0.35 * cl


@sfx("kick_impact", "player", "Punchy boot thump that launches the target.",
     variants=2, level=-13, jitter=0.06, dist=(1.0, 25), limit=True)
def _kick_impact(rng, v):
    dur = 0.3
    n = ns(dur)
    t = tl(n)
    x = punch(dur, rng, f=[850, 700][v], decay=0.03)
    x += 0.9 * thump(dur, f0=[100, 88][v], f1=[46, 42][v], decay=0.055, pitch_tau=0.025)
    x += 0.6 * bp(white(n, rng), 900, 3200, 2) * np.exp(-t / 0.007) * (1 - np.exp(-t / 0.0005))
    x += 0.3 * material("box", dur, rng, f0=[150, 135][v], damp=0.45)
    put(x, whoosh(0.24, rng, 120, 520, peak=0.25, bw=1.6, sharp=2.0, slope=-3.5), 0.03, 0.4)
    x = sat(norm(x) * 1.5, 1.4)
    return verb(x, "small", 0.06, tail=0.05)


def _boot_step(rng, dur=0.15, weight=1.0, scuff=0.35, grit=0.12, body_f=550.0):
    n = ns(dur)
    t = tl(n)
    x = punch(dur, rng, f=body_f * rng.uniform(0.85, 1.15), decay=0.011 * weight)
    x += 0.35 * thump(dur, f0=rng.uniform(130, 160), f1=rng.uniform(75, 90), decay=0.014 * weight, pitch_tau=0.008)
    x += 0.15 * hp(white(n, rng), 2000, 2) * np.exp(-t / 0.0015)
    sc = bp(white(n, rng), 900, 3600, 2) * np.clip(t / 0.008, 0, 1) * np.exp(-t / 0.022)
    put(x, endfade(sc, 0.3), rng.uniform(0.02, 0.04), scuff * rng.uniform(0.7, 1.2))
    x += grit * crackle(dur, rng, rate=70, band=(2200, 6500), alpha=2.5, decay_rate=0.04, cap=2.5)
    return endfade(x, 0.15)


@sfx("footstep_player", "player", "Soft leather boot step on stone (player footsteps, alternate variants).",
     variants=4, level=-27, jitter=0.06, dist=(0.8, 12))
def _footstep_player(rng, v):
    x = _boot_step(rng, 0.15, weight=[1.0, 0.9, 1.1, 0.95][v], scuff=[0.18, 0.24, 0.15, 0.21][v])
    return verb(x, "small", 0.06, tail=0.03)


@sfx("jump", "player", "Jump take-off: cloth rustle and a light push off the ground.",
     level=-24, jitter=0.06, dist=(1.0, 12))
def _jump(rng, v):
    dur = 0.2
    n = ns(dur)
    x = 0.7 * _boot_step(rng, dur, weight=1.2, scuff=0.6, grit=0.1)
    x += 0.45 * rustle(dur, rng, density=320, band=(1400, 6000), grain=(0.004, 0.015), env=bump(n, 0.35, 2.0))
    x += 0.4 * whoosh(dur, rng, 250, 1100, peak=0.4, bw=1.6, sharp=2.0, slope=-3.0)
    return x


@sfx("land", "player", "Boots landing after a jump or fall.", level=-20, jitter=0.06, dist=(1.0, 15), limit=True)
def _land(rng, v):
    dur = 0.25
    n = ns(dur)
    x = np.zeros(n)
    for dt, g in [(0.0, 1.0), (0.019, 0.75)]:
        b = punch(0.2, rng, f=750, decay=0.022) + 0.6 * thump(0.2, rng.uniform(100, 115), 52, 0.035, 0.015)
        put(x, b, dt, g)
        put(x, _boot_step(rng, 0.12, scuff=0.6, grit=0.2) * 0.35, dt + 0.008)
    x += 0.3 * rustle(dur, rng, density=200, band=(1500, 6000), env=np.exp(-tl(n) / 0.07))
    x = sat(norm(x) * 1.3, 1.3)
    return verb(x, "small", 0.08, tail=0.05)


def _grunt(rng, dur, f0, amp, vowel, scale=1.0, oq=0.45, rough=0.0, breath_end=0.4, tilt=3200.0, growl=0.15):
    n = ns(dur)
    sub = curve(dur, [(0, 0), (dur * 0.55, 0), (dur, rough)])
    asp = curve(dur, [(0, 0.5), (0.018, 0.0), (dur * 0.6, 0.0), (dur * 0.85, breath_end), (dur, 0.0)], "cos")
    x = voice(dur, rng, f0, amp, vowel, scale=scale, oq=oq, jitter=0.025, shimmer=0.12, sub=sub, breath=0.09,
              aspir=asp * curve(dur, amp, "cos").clip(0.2, 1), tilt=tilt, growl=growl)
    return x[:n]


@sfx("player_hurt", "player", "Short pained grunt from the player (no words).",
     variants=3, level=-16, jitter=0.04, dist=(1.0, 20))
def _player_hurt(rng, v):
    dur = 0.35
    if v == 0:  # "uh!"
        x = _grunt(rng, dur, [(0, 150), (0.035, 192), (0.12, 172), (0.32, 112)],
                   [(0, 0), (0.012, 1), (0.1, 0.8), (0.22, 0.35), (0.34, 0)], [(0, "A"), (0.2, "@")], rough=0.4)
    elif v == 1:  # strained "hnngh"
        x = _grunt(rng, dur, [(0, 138), (0.05, 176), (0.18, 162), (0.33, 108)],
                   [(0, 0), (0.02, 0.9), (0.15, 1.0), (0.26, 0.4), (0.34, 0)], [(0, "N"), (0.08, "U"), (0.3, "N")],
                   oq=0.38, rough=0.5)
    else:  # sharp "agh!"
        x = _grunt(rng, dur, [(0, 185), (0.03, 232), (0.1, 212), (0.31, 124)],
                   [(0, 0), (0.008, 1), (0.09, 0.85), (0.2, 0.3), (0.34, 0)], [(0, "a"), (0.14, "A"), (0.3, "@")],
                   rough=0.35)
    x = sat(norm(x) * 1.4, 1.5)
    return verb(x, "small", 0.06, tail=0.05)


@sfx("player_death", "player", "Player death: falling groan, then the body drops.", level=-15, jitter=0.0, dist=(1.0, 25))
def _player_death(rng, v):
    dur = 1.2
    n = ns(dur)
    g = _grunt(rng, 0.95, [(0, 158), (0.06, 188), (0.3, 152), (0.72, 96), (0.95, 78)],
               [(0, 0), (0.03, 1), (0.25, 0.85), (0.6, 0.5), (0.85, 0.15), (0.95, 0)],
               [(0, "a"), (0.35, "o"), (0.8, "U")], rough=0.6, breath_end=0.6, growl=0.25)
    x = np.zeros(n)
    put(x, g, 0.0)
    drop = thump(0.35, 92, 44, 0.1, 0.03, rng, noise=0.9, noise_lp=1100, click=0.08)
    drop += 0.25 * rustle(0.35, rng, density=260, band=(1400, 6000), env=np.exp(-tl(ns(0.35)) / 0.08))
    drop += 0.15 * debris(0.35, rng, t0=0.01, rate=40, decay_rate=0.06, kinds=("metal",), f_range=(1800, 3500), size=0.3)
    put(x, drop, 0.82, 0.9)
    x = sat(norm(x) * 1.2, 1.2)
    return fit(verb(x, "room", 0.1), dur, 0.1)


@sfx("item_pickup", "player", "Picking up a small item: pouch rustle and a light clink.",
     level=-21, jitter=0.05, dist=(1.0, 10))
def _item_pickup(rng, v):
    dur = 0.25
    n = ns(dur)
    x = 0.5 * rustle(dur, rng, density=300, band=(1000, 5000), grain=(0.004, 0.02), env=bump(n, 0.3, 2.0))
    clink = material("metal", 0.2, rng, f0=2100, damp=0.12) + 0.6 * material("metal", 0.2, rng, f0=2750, damp=0.1)
    put(x, clink, 0.07, 0.45)
    put(x, material("metal", 0.15, rng, f0=3300, damp=0.08), 0.115, 0.2)
    return verb(x, "small", 0.07, tail=0.04)


@sfx("item_drop", "player", "A small item dropped on the floor: light clatter with a bounce.",
     level=-21, jitter=0.07, dist=(1.0, 15))
def _item_drop(rng, v):
    dur = 0.3
    n = ns(dur)
    x = np.zeros(n)
    times = [0.0, 0.085, 0.14, 0.17, 0.188]
    for i, tt in enumerate(times):
        a = 0.85 ** i * (1.0 if i == 0 else rng.uniform(0.35, 0.6))
        hit = material("wood", 0.12, rng, f0=rng.uniform(750, 950), damp=0.35, hard=1.2)
        hit += 0.4 * material("metal", 0.12, rng, f0=rng.uniform(2200, 2900), damp=0.06)
        put(x, hit, tt, a)
    return verb(x, "small", 0.1, tail=0.05)


@sfx("grab", "player", "Hands gripping a heavy object.", level=-23, jitter=0.06, dist=(1.0, 10))
def _grab(rng, v):
    dur = 0.2
    n = ns(dur)
    grip = creak(dur, rng, curve(dur, [(0, 70), (0.08, 140), (0.2, 90)]),
                 [(600, 3, 1.0), (1300, 4, 0.7), (2600, 5, 0.4)], jitter=0.5, irregular=0.6)
    grip *= bump(n, 0.25, 2.5) * 0.5
    x = norm(grip) * 0.5
    x += 0.5 * rustle(dur, rng, density=220, band=(900, 4000), env=bump(n, 0.2, 2.0))
    shift = thump(0.12, 150, 95, 0.03, 0.012, rng, noise=0.6, noise_lp=800, click=0.0)
    shift += 0.3 * material("box", 0.12, rng, f0=200, damp=0.4)
    put(x, shift, 0.045, 0.6)
    return x


@sfx("throw_whoosh", "player", "Heavy object thrown: big, low whoosh.", level=-19, jitter=0.06, dist=(1.0, 18))
def _throw_whoosh(rng, v):
    dur = 0.35
    n = ns(dur)
    x = whoosh(dur, rng, 130, 900, peak=0.38, bw=1.8, sharp=2.4, slope=-3.0)
    x += 0.5 * whoosh(dur, rng, 60, 220, peak=0.36, bw=1.4, sharp=2.0, slope=-4.5)
    x += 0.25 * whoosh(dur, rng, 700, 2600, peak=0.42, bw=1.0, sharp=3.0, slope=-1.5)
    x += 0.15 * rustle(dur, rng, density=200, band=(1500, 5000), env=bump(n, 0.2, 2.0))
    return x


# ---------------------------------------------------------------------------
# Brainwash: the signature family. Everything sits in D (D/A/F/E colours),
# glassy FM/additive bells, beating detuned pairs and reverse swells.
# ---------------------------------------------------------------------------


def snapf(f, period):
    """Snap a frequency so it completes an integer number of cycles per loop period."""
    return max(1, round(f * period)) / period


def reverse_swell(src, space="magic", length=0.35, wet_only=True):
    """Reverse of a reverberated source: a crescendo that ends exactly at its last sample."""
    r = verb(src, space, wet=1.0, dry=0.0 if wet_only else 1.0)
    r = r[ns(SPACES[space][2] + 0.012):]  # drop the pre-delay so the swell peaks right at its end
    r = r[::-1]
    r = r[-ns(length):]
    r = r * np.linspace(0, 1, len(r)) ** 1.5
    return r


@sfx("cast_start", "brainwash", "Brainwash eye opening: rising mystical shimmer when the player starts a cast (2D).",
     level=-16, jitter=0.0, dist=(1.0, 30))
def _cast_start(rng, v):
    dur = 0.85
    n = ns(dur)
    t = tl(n)
    raw = strings(dur, rng, [hz("D3"), hz("A3"), hz("D4"), hz("A4")], 1.0, voices=3, detune=0.009, bright=16000)
    cut = curve(dur, [(0, 140), (0.45, 4200), (0.85, 1100)], "exp")
    pad = tv_biquad(raw, "lp", cut, q=3.2) * curve(dur, [(0, 0), (0.15, 0.3), (0.45, 1.0), (0.85, 0.0)], "cos")
    sh = whoosh(dur, rng, 700, 6500, peak=0.5, bw=0.9, sharp=2.2, slope=-1.0)
    arp = np.zeros(n)
    for i, nm in enumerate(["D5", "F5", "A5", "D6", "E6", "A6"]):
        put(arp, glass_ping(0.5, rng, hz(nm), decay=0.35, bright=0.7), 0.15 + i * 0.05, 0.8 + 0.2 * i / 5)
    bloom = (np.sin(TAU * hz("D2") * t) + 0.6 * np.sin(TAU * hz("D3") * t + 1.0)) * \
        curve(dur, [(0, 0), (0.3, 0.5), (0.5, 0.8), (0.85, 0.0)], "cos")
    x = 0.9 * norm(pad) + 0.4 * norm(sh) + 0.38 * norm(arp) + 0.3 * bloom
    return fit(verb(x, "magic", 0.28), 1.0, 0.12)


@sfx("cast_loop", "brainwash", "Soft hypnotic drone held while the player speaks a command; loop=true, keep quiet. "
     "Low and steady with no transients so it does not disturb speech recognition.",
     loop=True, level=-31, jitter=0.0, dist=(1.0, 30))
def _cast_loop(rng, v):
    T = 3.0
    n = ns(T)
    t = tl(n)
    f1 = snapf(73.42, T)
    x = 0.9 * np.sin(TAU * f1 * t) + 0.6 * np.sin(TAU * (f1 + 2 / T) * t + 0.5)  # 0.67 Hz 'wub'
    x += 0.32 * np.sin(TAU * snapf(146.83, T) * t + 1.0)
    f5 = snapf(110.0, T)
    x += 0.3 * np.sin(TAU * f5 * t + 2.0) + 0.2 * np.sin(TAU * (f5 + 1 / T) * t)
    x += 0.1 * np.sin(TAU * snapf(220.0, T) * t + 0.3) * (0.6 + 0.4 * np.sin(TAU * (2 / T) * t))
    x += 0.05 * np.sin(TAU * snapf(293.66, T) * t + 0.9) * (0.5 + 0.5 * np.sin(TAU * (1 / T) * t + 1.3))
    nz = periodic(colored(n, rng, -4.0), lambda s: bp(s, 110, 420, 2))
    x += 0.14 * norm(nz) * (0.5 + 0.5 * np.sin(TAU * (2 / T) * t - np.pi / 2))
    x *= 1 - 0.12 * np.cos(TAU * (2 / T) * t)
    x = np.tanh(0.7 * x)
    return verb_loop(x, "magic", 0.18, pre_lp=900)


@sfx("listener_join", "brainwash", "Glassy ping when a monster starts listening; pitch-shift per listener in code.",
     level=-17, jitter=0.0, dist=(1.0, 30))
def _listener_join(rng, v):
    dur = 0.5
    n = ns(dur)
    t = tl(n)
    f = hz("A5")
    x = glass_ping(dur, rng, f, bright=1.0, decay=0.45)
    x += 0.3 * lp(fm_bell(dur, rng, f, ratio=3.0, index=1.4, decay=0.25, idx_decay=0.03), 6000, 4)
    x = lp(x, 7500, 2)
    x += 0.22 * np.sin(TAU * f / 2 * t) * np.exp(-t / 0.12) * (1 - np.exp(-t / 0.004))
    gl = np.sin(TAU * phase_of(curve(dur, [(0, f * 1.5), (0.03, f * 2.0)], "exp"))) * np.exp(-t / 0.012)
    x += 0.12 * gl
    return fit(verb(x, "magic", 0.25), 0.6, 0.12)


@sfx("listener_lost", "brainwash", "Short descending blip when a monster stops listening.",
     level=-20, jitter=0.0, dist=(1.0, 30))
def _listener_lost(rng, v):
    dur = 0.35
    n = ns(dur)
    t = tl(n)
    f = curve(dur, [(0, hz("A5")), (0.16, hz("D5")), (0.35, hz("C5"))], "exp")
    p = phase_of(f)
    x = (np.sin(TAU * p) + 0.3 * np.sin(TAU * 2 * p + 0.4) + 0.12 * np.sin(TAU * 3 * p)) * np.exp(-t / 0.09)
    x *= 1 - np.exp(-t / 0.004)
    x = tv_biquad(x, "lp", curve(dur, [(0, 6000), (0.3, 900)], "exp"), 0.8)
    x += 0.2 * glass_ping(dur, rng, hz("A5"), decay=0.15, bright=0.5)
    return fit(verb(x, "magic", 0.22), 0.45, 0.1)


@sfx("cast_send", "brainwash", "Thought whoosh sent from the player toward a monster (command dispatched).",
     level=-16, jitter=0.02, dist=(1.0, 30))
def _cast_send(rng, v):
    dur = 0.6
    n = ns(dur)
    t = tl(n)
    w = whoosh(dur, rng, 380, 3300, peak=0.3, bw=1.1, sharp=2.0, tail=1.5, slope=-1.8)
    fz = curve(dur, [(0, hz("D4")), (0.22, hz("D6")), (0.6, hz("A6"))], "exp")
    zap = fm_tone(fz, n, 2.0, 0.8 * np.exp(-t / 0.2)) + 0.5 * np.sin(TAU * phase_of(fz * 1.5))
    zap *= curve(dur, [(0, 0), (0.04, 0.6), (0.2, 1.0), (0.6, 0)], "cos")
    zap = tv_biquad(zap, "lp", curve(dur, [(0, 5000), (0.25, 7000), (0.6, 1200)], "exp"), 0.7)
    spirit = voice(dur, rng, 200.0, curve(dur, [(0, 0), (0.12, 1), (0.6, 0)], "cos") * 0.0, [(0, "u"), (0.3, "i")],
                   scale=1.3, aspir=curve(dur, [(0, 0), (0.12, 1.0), (0.55, 0)], "cos"))
    x = 0.8 * norm(w) + 0.5 * norm(zap) + 0.3 * norm(spirit)
    echo = np.zeros(n + ns(0.3))
    put(echo, x, 0.0)
    put(echo, lp(x, 2500, 2), 0.11, 0.35)
    put(echo, lp(x, 1500, 2), 0.22, 0.15)
    return fit(verb(echo, "magic", 0.25), 0.8, 0.15)


@sfx("thinking", "brainwash", "Warbling, bubbling 'processing' sound while the command is interpreted. "
     "Starts and ends silent and is built periodic, so it can also be looped while waiting.",
     level=-22, jitter=0.0, dist=(1.0, 30))
def _thinking(rng, v):
    T = 0.9
    n = ns(T)
    t = tl(n)
    x = np.zeros(n)
    fw = snapf(hz("D5"), T)
    vib = 1 + 0.035 * np.sin(TAU * (7 / T) * t)
    warble = np.sin(TAU * phase_of(fw * vib)) + 0.25 * np.sin(TAU * 2 * phase_of(fw * vib) + 0.6)
    x += 0.35 * warble * (0.55 + 0.45 * np.sin(TAU * (2 / T) * t))
    scale = [hz(s) for s in ["D5", "E5", "F5", "A5", "C6", "D6", "E6"]]
    k = 0
    for i in range(14):
        tt = (i + rng.uniform(0.1, 0.8)) * T / 14
        f0 = scale[int(rng.integers(len(scale)))] * 0.8
        put_wrap(x, bubble(f0, rng.uniform(0.04, 0.07), rise=rng.uniform(0.5, 0.9)), tt, rng.uniform(0.5, 1.0))
        k += 1
    x = periodic(x, lambda s: lp(s, 5000, 2))
    x *= np.sin(np.pi * t / T) ** 0.7
    return verb(x, "magic", 0.12, tail=0.0)[:n] * np.sin(np.pi * t / T) ** 0.3


@sfx("obey", "brainwash", "Mind snap: reverse swell into a deep hypnotic chord when a monster obeys.",
     level=-13, jitter=0.0, dist=(1.0, 30))
def _obey(rng, v):
    dur = 0.9
    n = ns(dur)
    th = 0.34
    m = ns(dur - th)
    tt = tl(m)
    chord = [hz("D2"), hz("D3"), hz("A3"), hz("D4"), hz("F4"), hz("A4"), hz("E5")]
    raw = strings(dur - th, rng, chord, 1.0, voices=3, detune=0.01, bright=16000)
    stab = tv_biquad(raw, "lp", 400 + 3600 * np.exp(-tt / 0.09), q=1.6) * (np.exp(-tt / 0.32) * (1 - np.exp(-tt / 0.004)))
    sub = np.sin(TAU * phase_of(hz("D2") + 40 * np.exp(-tt / 0.03))) * np.exp(-tt / 0.35) * (1 - np.exp(-tt / 0.002))
    bells = fm_bell(dur - th, rng, hz("D5"), 3.0, 2.5, 0.5, 0.05) + 0.7 * fm_bell(dur - th, rng, hz("A5"), 3.0, 2.0, 0.45, 0.05)
    snap = bp(white(m, rng), 1500, 5500, 2) * np.exp(-tt / 0.004)
    trem = 1 - 0.35 * np.clip(tt / 0.15, 0, 1) * (0.5 + 0.5 * np.cos(TAU * 6.5 * tt))
    hit = endfade(0.9 * norm(stab) * trem + 0.8 * sub + 0.25 * norm(bells) * trem + 0.5 * snap, 0.25)
    swell_src = 0.7 * norm(bells[: ns(0.25)])
    swell_src[: ns(0.05)] += 0.5 * hp(white(ns(0.05), rng), 2000, 2)
    swell = norm(reverse_swell(swell_src, "magic", th))
    swell += 0.45 * norm(whoosh(th, rng, 500, 5200, peak=0.94, bw=1.0, sharp=3.0, slope=-1.2))
    x = np.zeros(n + ns(0.6))
    put(x, 0.85 * swell, 0.0)
    put(x, hit, th)
    return fit(verb(x, "magic", 0.3), 1.2, 0.3)


@sfx("cast_fail", "brainwash", "Dull buzz/thunk: the command was spoken but nobody was listening.",
     level=-18, jitter=0.0, dist=(1.0, 30))
def _cast_fail(rng, v):
    dur = 0.45
    n = ns(dur)
    t = tl(n)
    th = thump(dur, 150, 70, 0.05, 0.015) + 0.6 * punch(dur, rng, f=420, decay=0.03)
    sag = 1 - 0.06 * (t / dur)
    bz = square(92.0 * sag, n) + square(97.5 * sag, n, ph0=0.3)
    bz = lp(lp(bz, 750, 2), 1500, 2) * curve(dur, [(0, 0), (0.02, 1), (0.25, 0.6), (0.45, 0)], "cos")
    x = 0.9 * norm(th) + 0.45 * norm(bz)
    return verb(x, "small", 0.12, tail=0.08)


@sfx("control_end", "brainwash", "Gentle descending release tone when a monster's five seconds of control end.",
     level=-20, jitter=0.0, dist=(1.0, 30))
def _control_end(rng, v):
    dur = 0.7
    n = ns(dur)
    t = tl(n)
    x = np.zeros(n)
    for i, nm in enumerate(["A5", "F5", "D5"]):
        put(x, glass_ping(0.6, rng, hz(nm), decay=0.4, bright=0.5), i * 0.11, 0.8 - 0.15 * i)
    bend = 2 ** (-2.0 * (t / dur) / 12)
    pad = (np.sin(TAU * phase_of(hz("D4") * bend)) + 0.5 * np.sin(TAU * phase_of(hz("A4") * bend) + 1)) * \
        curve(dur, [(0, 0), (0.08, 0.5), (0.7, 0)], "cos")
    air = whoosh(dur, rng, 600, 1800, peak=0.2, bw=1.4, sharp=1.5, tail=2.0, slope=-3)
    x = norm(x) + 0.35 * pad + 0.15 * norm(air)
    return fit(verb(x, "magic", 0.3), 0.9, 0.2)


@sfx("mp_low", "brainwash", "Soft warning pulse: not enough mind power to cast.", level=-21, jitter=0.0, dist=(1.0, 30))
def _mp_low(rng, v):
    dur = 0.4
    n = ns(dur)
    x = np.zeros(n)
    for tt, g in [(0.0, 1.0), (0.17, 0.75)]:
        m = ns(0.2)
        u = tl(m)
        e = (1 - np.exp(-u / 0.012)) * np.exp(-u / 0.06)
        p = np.sin(TAU * hz("A3") * u) + 0.35 * np.sin(TAU * hz("A4") * u) + 0.25 * np.sin(TAU * hz("Eb4") * u)
        p = p * e + 0.6 * np.sin(TAU * phase_of(55 + 30 * np.exp(-u / 0.02))) * np.exp(-u / 0.05)
        put(x, p, tt, g)
    x = lp(x, 1500, 2)
    return verb(x, "small", 0.1, tail=0.1)


# ---------------------------------------------------------------------------
# Monsters: shared helpers
# ---------------------------------------------------------------------------


def syll(dur, ons, lens, f0s, fall=0.1, rise=0.06, attack=0.008, h=0.03, hgain=0.8, decay_in=0.3):
    """Envelopes for a run of syllables -> (f0 breakpoints, voicing amp array, aspiration array)."""
    n = ns(dur)
    t = tl(n)
    amp = np.zeros(n)
    asp = np.zeros(n)
    pts = []
    for on, L, f in zip(ons, lens, f0s):
        u = t - on
        inside = (u >= 0) & (u <= L)
        a = np.where(u < 0, 0.0, np.clip(u / attack, 0, 1))
        a = a * np.where(u > L, np.exp(-np.maximum(u - L, 0) / 0.018), 1.0)
        a = a * np.where(inside, 1 - decay_in * np.clip(u / max(L, 1e-3), 0, 1), 1.0)
        amp = np.maximum(amp, a)
        if h > 0:
            asp = np.maximum(asp, hgain * np.exp(-(((u + h * 0.5) / (h * 0.45)) ** 2)))
        pts += [(max(0.0, on - 0.004), f * (1 - rise)), (on + 0.3 * L, f * (1 + rise)), (on + L, f * (1 - fall))]
    pts.sort()
    clean = []
    for p in pts:
        if clean and p[0] <= clean[-1][0] + 1e-4:
            continue
        clean.append(p)
    if clean[0][0] > 0:
        clean.insert(0, (0.0, clean[0][1]))
    if clean[-1][0] < dur:
        clean.append((dur, clean[-1][1]))
    return clean, amp, asp


def kburst(rng, f1=2000.0, f2=6000.0, L=0.012):
    n = ns(L)
    return bp(white(n, rng), f1, f2, 2) * np.exp(-tl(n) / (L / 4.0))


def scale_pts(pts, k):
    return [(a, b * k) for a, b in pts]


def beast_fx(x, drive=2.0, low=80.0, presence=None, room="small", wet=0.08, tail=0.1, top=6800.0):
    """Shared creature chain: clean up lows, add presence, saturate, tame the top, small space."""
    x = hp(norm(x), low, 2)
    if presence:
        x = eq(x, "peak", presence[0], 1.0, presence[1])
    x = sat(norm(x), drive)
    x = lp(x, top, 4)
    return verb(x, room, wet, tail=tail)


def mask_fx(x):
    """Plague-doctor beak mask: hollow nasal tube resonance, muffled top."""
    x = eq(x, "peak", 1050, 2.5, 6)
    x = eq(x, "peak", 560, 1.6, 3)
    x = comb(x, 0.0014, 0.4, 0.5)
    x = lp(lp(x, 2900, 2), 4200, 2)
    return x


def ice_crack(rng, dur=0.16, f_hi=7000.0, f_lo=450.0, sweep=0.04):
    """Ice fracture: broadband snap + dispersive descending 'pew' chirp."""
    n = ns(dur)
    t = tl(n)
    f = f_lo + (f_hi - f_lo) * np.exp(-t / (sweep / 3.0))
    chirp = np.sin(TAU * phase_of(f)) * np.exp(-t / (sweep * 1.2))
    snap = bp(white(n, rng), 1500, 8000, 2) * np.exp(-t / 0.002)
    return endfade(0.6 * lp(chirp, 7500, 2) + snap + 0.4 * punch(dur, rng, f=1600, decay=0.006), 0.2)


def squeak(dur, rng, fpts, amp_pts, vib=0.0, vib_rate=40.0, rough=0.0):
    """Tiny-creature squeak: FM sine contour with a couple of harmonics."""
    n = ns(dur)
    t = tl(n)
    f = curve(dur, fpts, "exp")
    if vib:
        f = f * (1 + vib * np.sin(TAU * vib_rate * t + rng.uniform(0, TAU)))
    if rough:
        f = f * (1 + rough * smooth_noise(n, rng, 180.0))
    p = phase_of(f)
    y = np.sin(TAU * p) + 0.16 * np.sin(TAU * 2 * p + 0.5) + 0.04 * np.sin(TAU * 3 * p + 1.1)
    y += 0.06 * bp(white(n, rng), 2000, 5000, 2)
    y *= curve(dur, amp_pts, "cos")
    return lp(y, 7000, 4)


def grind(dur, rng, rate=45.0, modes=None, grit=0.3, rumble=0.4):
    """Stone/ice grinding: continuous rough friction noise + stick-slip buzz + fine grit + rumble."""
    n = ns(dur)
    modes = modes or [(160, 1.5, 1.0), (380, 2.0, 0.8), (850, 2.5, 0.6), (1900, 3.0, 0.35)]
    rough = np.abs(smooth_noise(n, rng, 22.0)) + 0.35 * np.abs(smooth_noise(n, rng, 6.0)) + 0.4
    body = resonbank(white(n, rng), [(f, q * 0.7, g) for f, q, g in modes]) * rough
    rr = rate * np.clip(1 + 0.25 * smooth_noise(n, rng, 3.0), 0.4, 2.0)
    buzz = creak(dur, rng, rr, modes, jitter=0.3, pulse_len=0.005, irregular=0.35)
    gr = lp(crackle(dur, rng, rate=500, band=(1800, 5500), alpha=3.0, cap=2.5), 7000, 4)
    rum = lp(colored(n, rng, -6.0), 140, 2)

    def r(a):
        return a / (np.sqrt(np.mean(a ** 2)) + 1e-12)

    return endfade(r(body) + 0.6 * r(buzz) + grit * r(gr) + rumble * r(rum), 0.1)


def splat(dur, rng, f_from=1800.0, f_to=320.0, q=2.5, decay=0.05):
    """Wet squelch: noise through a fast downward-sweeping resonance."""
    n = ns(dur)
    t = tl(n)
    sq = tv_biquad(white(n, rng), "bp", curve(dur, [(0, f_from), (min(dur, 0.06), f_to * 1.4), (dur, f_to)], "exp"), q)
    sq *= np.exp(-t / decay) * (1 - np.exp(-t / 0.003))
    pops = np.zeros(n)
    for tt in impulses(dur * 0.6, rng, 60):
        put(pops, bubble(rng.uniform(300, 900), 0.03, rise=0.8), tt, rng.uniform(0.1, 0.4))
    return endfade(sq + 0.5 * pops * np.exp(-t / decay), 0.2)


# ---------------------------------------------------------------------------
# Goblin: small, nasal, high, cackling
# ---------------------------------------------------------------------------

GOB = dict(scale=1.32, oq=0.5, jitter=0.02, shimmer=0.12, breath=0.14, nasal=0.55, tilt=6500)


def gob_voice(dur, rng, f0, amp, vowel, growl=0.25, sub=0.0, aspir=None, **kw):
    p = dict(GOB)
    p.update(kw)
    return voice(dur, rng, f0, amp, vowel, growl=growl, sub=sub, aspir=aspir, **p)


def gob_cackle(rng, dur, ons, lens, f0s, vowel="e", gain_fall=0.0):
    pts, amp, asp = syll(dur, ons, lens, f0s, fall=0.14, rise=0.05, h=0.03, hgain=1.0)
    if gain_fall:
        amp *= np.linspace(1, 1 - gain_fall, len(amp))
    return gob_voice(dur, rng, pts, amp, vowel, aspir=asp, growl=0.3)


def gob_espeak(rng, ph, gain=1.0, speed=1.45, pitch=88):
    x = espeak(ph, "en-us+m3", pitch=pitch, speed=230)
    x = resample_speed(x, speed)
    x = eq(eq(x, "notch", 950, 1.2), "peak", 2300, 1.4, 6)
    return gain * norm(x)


def gob_fx(x, drive=2.2, wet=0.07):
    return beast_fx(x, drive=drive, low=170, presence=(1900, 4), room="small", wet=wet)


@sfx("goblin_alert", "monster", "Goblin spots the player: nasal 'eh?!' and a nasty little snicker.",
     variants=2, level=-15, jitter=0.06, dist=(1.5, 30))
def _goblin_alert(rng, v):
    if v == 0:
        x = np.zeros(ns(0.72))
        q = gob_voice(0.3, rng, [(0, 330), (0.05, 360), (0.22, 525), (0.3, 545)],
                      [(0, 0), (0.03, 1), (0.22, 1), (0.3, 0)], [(0, "e"), (0.2, "ae")],
                      aspir=[(0, 0.9), (0.03, 0), (0.3, 0)], growl=0.2)
        put(x, norm(q), 0.0)
        c = gob_cackle(rng, 0.34, [0.03, 0.11, 0.18], [0.05, 0.05, 0.05], [480, 445, 410], gain_fall=0.35)
        put(x, norm(c), 0.32, 0.8)
        put(x, gob_espeak(rng, "h'Eh@h@")[: ns(0.36)], 0.33, 0.22)
    else:
        x = np.zeros(ns(0.45))
        q = gob_voice(0.36, rng, [(0, 300), (0.08, 330), (0.29, 565), (0.36, 585)],
                      [(0, 0), (0.025, 0.9), (0.3, 1.0), (0.335, 0)], [(0, "N"), (0.06, "ae"), (0.3, "ae")],
                      growl=0.25)
        put(x, norm(q), 0.0)
        put(x, kburst(rng, 2200, 5500, 0.015), 0.335, 0.4)
        put(x, norm(gob_cackle(rng, 0.12, [0.01], [0.04], [500])), 0.36, 0.35)
    return gob_fx(x)


@sfx("goblin_attack", "monster", "Goblin attack yell: aggressive high 'hyaah!'.",
     variants=2, level=-14, jitter=0.06, dist=(1.5, 30))
def _goblin_attack(rng, v):
    if v == 0:
        dur = 0.45
        x = gob_voice(dur, rng, [(0, 380), (0.06, 470), (0.3, 425), (0.44, 330)],
                      [(0, 0), (0.02, 1), (0.3, 0.9), (0.44, 0)], [(0, "i"), (0.06, "ae"), (0.3, "a")],
                      aspir=[(0, 1.0), (0.03, 0.1), (0.3, 0.1), (0.44, 0.35)], growl=0.4, sub=0.15)
    else:
        dur = 0.5
        x = np.zeros(ns(dur))
        put(x, gob_voice(0.11, rng, [(0, 350), (0.05, 380), (0.11, 340)], [(0, 0), (0.01, 0.8), (0.07, 0.7), (0.1, 0)],
                         "a", aspir=[(0, 0.8), (0.02, 0), (0.11, 0)], growl=0.3), 0.0)
        put(x, gob_voice(0.36, rng, [(0, 440), (0.08, 505), (0.25, 470), (0.36, 360)],
                         [(0, 0), (0.015, 1), (0.25, 0.9), (0.36, 0)], [(0, "ae"), (0.2, "a")],
                         aspir=[(0, 1.0), (0.025, 0.1), (0.3, 0.2), (0.36, 0)], growl=0.45, sub=0.2), 0.13)
    return gob_fx(x, drive=2.8)


@sfx("goblin_hurt", "monster", "Goblin hurt: sharp nasal yelp.", variants=2, level=-15, jitter=0.07, dist=(1.5, 28))
def _goblin_hurt(rng, v):
    if v == 0:
        x = gob_voice(0.3, rng, [(0, 520), (0.03, 705), (0.12, 640), (0.29, 430)],
                      [(0, 0), (0.01, 1), (0.12, 0.85), (0.29, 0)], [(0, "i"), (0.15, "e")],
                      sub=curve(0.3, [(0, 0), (0.12, 0.05), (0.29, 0.35)]), growl=0.45)
    else:
        x = np.zeros(ns(0.3))
        put(x, gob_voice(0.17, rng, [(0, 470), (0.04, 505), (0.17, 385)], [(0, 0), (0.008, 1), (0.13, 0.9), (0.165, 0)],
                         [(0, "ae"), (0.12, "A")], growl=0.4, sub=0.1), 0.0)
        put(x, kburst(rng, 1800, 5000, 0.02), 0.165, 0.6)
    return gob_fx(x, drive=2.6)


@sfx("goblin_death", "monster", "Goblin death: descending wail ending in one feeble 'heh'.",
     level=-14, jitter=0.04, dist=(1.5, 30))
def _goblin_death(rng, v):
    dur = 0.95
    x = np.zeros(ns(dur))
    w = gob_voice(0.78, rng, [(0, 560), (0.08, 605), (0.35, 470), (0.65, 280), (0.78, 235)],
                  [(0, 0), (0.03, 1), (0.4, 0.8), (0.7, 0.25), (0.78, 0)], [(0, "ae"), (0.25, "a"), (0.5, "o"), (0.7, "u")],
                  sub=curve(0.78, [(0, 0), (0.3, 0.1), (0.75, 0.55)]), growl=0.4)
    put(x, norm(w), 0.0)
    put(x, norm(gob_cackle(rng, 0.14, [0.02], [0.05], [275])), 0.8, 0.3)
    return gob_fx(x, drive=2.2, wet=0.09)


# ---------------------------------------------------------------------------
# Imp: tiny red devil, screechy chitter
# ---------------------------------------------------------------------------

IMP = dict(scale=1.62, oq=0.42, jitter=0.03, shimmer=0.16, breath=0.12, nasal=0.25, tilt=7500)


def imp_chitter(rng, dur, ons, f0s, slen=0.034):
    pts, amp, asp = syll(dur, ons, [slen] * len(ons), f0s, fall=0.15, rise=0.08, h=0.0, attack=0.004)
    x = voice(dur, rng, pts, amp, "i", growl=0.35, **IMP)
    for on in ons:
        put(x, kburst(rng, 2800, 6500, 0.008), on - 0.006, 0.25)
    return x


def imp_screech(rng, dur, f0, amp, vowel=(("ae", 0),), growl=0.65, sub=0.3):
    vw = [(0, "i"), (0.04, "ae"), (dur * 0.7, "a")] if vowel == (("ae", 0),) else vowel
    return voice(dur, rng, f0, amp, vw, growl=growl, growl_rate=75, sub=sub, **IMP)


def imp_fx(x, drive=2.8):
    return beast_fx(x, drive=drive, low=380, presence=(3000, 4), room="small", wet=0.07)


@sfx("imp_attack", "monster", "Imp attack: rapid chitter into a screech.", variants=2, level=-15, jitter=0.07,
     dist=(1.5, 28))
def _imp_attack(rng, v):
    if v == 0:
        x = np.zeros(ns(0.6))
        ons = [0.0, 0.055, 0.105, 0.15, 0.192]
        put(x, norm(imp_chitter(rng, 0.26, [o + 0.01 for o in ons], [900, 960, 1000, 1050, 1100])), 0.0, 0.75)
        put(x, norm(imp_screech(rng, 0.32, [(0, 1050), (0.05, 1250), (0.2, 1150), (0.32, 880)],
                                [(0, 0), (0.015, 1), (0.22, 0.85), (0.32, 0)])), 0.25)
    else:
        x = np.zeros(ns(0.55))
        put(x, norm(imp_screech(rng, 0.3, [(0, 980), (0.04, 1230), (0.18, 1180), (0.3, 950)],
                                [(0, 0), (0.012, 1), (0.2, 0.9), (0.3, 0)])), 0.0)
        put(x, norm(imp_chitter(rng, 0.26, [0.01, 0.06, 0.105, 0.145], [1000, 940, 900, 850])), 0.28, 0.65)
    return imp_fx(x)


@sfx("imp_hurt", "monster", "Imp hurt: short piercing squeal.", variants=2, level=-15, jitter=0.07, dist=(1.5, 26))
def _imp_hurt(rng, v):
    if v == 0:
        x = imp_screech(rng, 0.28, [(0, 1150), (0.03, 1420), (0.12, 1300), (0.27, 880)],
                        [(0, 0), (0.008, 1), (0.12, 0.8), (0.27, 0)], growl=0.8, sub=0.35)
    else:
        x = np.zeros(ns(0.34))
        put(x, norm(imp_chitter(rng, 0.09, [0.008], [1150], slen=0.04)), 0.0, 0.7)
        put(x, norm(imp_screech(rng, 0.24, [(0, 1250), (0.04, 1380), (0.24, 900)], [(0, 0), (0.01, 1), (0.24, 0)],
                                growl=0.75)), 0.075)
    return imp_fx(x, drive=3.0)


@sfx("imp_death", "monster", "Imp death: falling, gurgling screech that fizzles out in a puff.",
     level=-14, jitter=0.04, dist=(1.5, 28))
def _imp_death(rng, v):
    dur = 0.85
    n = ns(dur)
    x = np.zeros(n)
    s = imp_screech(rng, 0.62, [(0, 1200), (0.05, 1320), (0.3, 950), (0.62, 470)],
                    [(0, 0), (0.012, 1), (0.35, 0.75), (0.62, 0)], growl=0.9,
                    sub=curve(0.62, [(0, 0.2), (0.62, 0.7)]))
    put(x, norm(s), 0.0)
    put(x, norm(imp_chitter(rng, 0.2, [0.01, 0.07, 0.12], [700, 620, 560])), 0.5, 0.45)
    puff = whoosh(0.3, rng, 300, 1500, peak=0.15, bw=1.6, sharp=2.0, tail=2.0, slope=-3)
    puff += 0.4 * crackle(0.3, rng, rate=80, band=(1500, 5000), decay_rate=0.1)
    put(x, puff, 0.55, 0.5)
    return imp_fx(x, drive=2.6)


# ---------------------------------------------------------------------------
# Ogre: huge, deep growls and bellows
# ---------------------------------------------------------------------------

OGRE = dict(scale=0.62, oq=0.62, jitter=0.022, shimmer=0.16, breath=0.2, tilt=4200, bw=1.3)


def ogre_voice(dur, rng, f0, amp, vowel, sub=0.35, growl=0.45, aspir=None, layers=True):
    main = voice(dur, rng, f0, amp, vowel, sub=sub, growl=growl, growl_rate=30, aspir=aspir, **OGRE)
    if not layers:
        return main
    low = voice(dur, rng, scale_pts(f0, 0.5), amp, vowel, scale=0.55, oq=0.7, jitter=0.03, shimmer=0.2,
                sub=0.5, growl=0.6, growl_rate=22, breath=0.1, tilt=1800)
    roar = voice(dur, rng, 80.0, 0.0, vowel, scale=0.6, aspir=amp)
    return norm(main) + 0.45 * norm(low) + 0.3 * norm(roar)


def ogre_fx(x, drive=2.4, wet=0.14, room="stone"):
    x = eq(hp(norm(x), 45, 2), "ls", 160, 0.7, 3)
    x = sat(norm(x), drive)
    x = lp(x, 6500, 2)
    return verb(x, room, wet, tail=0.35)


@sfx("ogre_roar", "monster", "Ogre roar: huge bellowing growl (alert / taunt).", level=-11, jitter=0.04,
     dist=(3.0, 50))
def _ogre_roar(rng, v):
    dur = 1.5
    x = ogre_voice(dur, rng, [(0, 68), (0.15, 94), (0.6, 106), (1.1, 92), (1.45, 60)],
                   [(0, 0), (0.12, 0.75), (0.3, 1.0), (1.0, 0.92), (1.45, 0)],
                   [(0, "o"), (0.2, "a"), (1.0, "a"), (1.4, "o")], sub=0.42, growl=0.5,
                   aspir=[(0, 0.2), (0.3, 0.45), (1.2, 0.4), (1.5, 0)])
    return fit(ogre_fx(x, drive=2.8), 1.75, 0.2)


@sfx("ogre_attack", "monster", "Ogre attack: heavy effort grunt as it swings.", variants=2, level=-13, jitter=0.05,
     dist=(3.0, 45))
def _ogre_attack(rng, v):
    if v == 0:
        x = ogre_voice(0.45, rng, [(0, 92), (0.05, 122), (0.3, 86), (0.45, 70)],
                       [(0, 0), (0.015, 1), (0.18, 0.8), (0.4, 0)], [(0, "A"), (0.3, "@")], sub=0.3, growl=0.4,
                       aspir=[(0, 0.6), (0.03, 0.15), (0.45, 0.3)])
    else:
        x = ogre_voice(0.55, rng, [(0, 84), (0.08, 116), (0.35, 104), (0.55, 76)],
                       [(0, 0), (0.03, 0.9), (0.3, 1.0), (0.52, 0)], [(0, "ae"), (0.1, "a"), (0.45, "o")],
                       sub=0.45, growl=0.55, aspir=[(0, 0.7), (0.05, 0.2), (0.55, 0.2)])
    return ogre_fx(x, drive=2.6, wet=0.1)


@sfx("ogre_hurt", "monster", "Ogre hurt: pained, bellowing 'oarrgh'.", variants=2, level=-12, jitter=0.05,
     dist=(3.0, 45))
def _ogre_hurt(rng, v):
    if v == 0:
        x = ogre_voice(0.6, rng, [(0, 110), (0.08, 142), (0.35, 120), (0.6, 80)],
                       [(0, 0), (0.02, 1), (0.35, 0.8), (0.58, 0)], [(0, "o"), (0.12, "a"), (0.45, "A")],
                       sub=0.35, growl=0.5)
    else:
        x = ogre_voice(0.55, rng, [(0, 100), (0.1, 128), (0.5, 84)], [(0, 0), (0.03, 0.9), (0.3, 1.0), (0.52, 0)],
                       [(0, "U"), (0.2, "o"), (0.45, "N")], sub=0.4, growl=0.45)
    return ogre_fx(x, drive=2.5, wet=0.12)


@sfx("ogre_death", "monster", "Ogre death: long falling groan with a last gurgling breath.", level=-12,
     jitter=0.03, dist=(3.0, 50))
def _ogre_death(rng, v):
    dur = 1.5
    n = ns(dur)
    x = ogre_voice(dur, rng, [(0, 118), (0.1, 136), (0.6, 96), (1.15, 62), (1.5, 46)],
                   [(0, 0), (0.05, 1), (0.6, 0.8), (1.1, 0.4), (1.45, 0)], [(0, "a"), (0.5, "o"), (1.1, "u")],
                   sub=curve(dur, [(0, 0.2), (0.7, 0.4), (1.5, 0.75)]), growl=0.55,
                   aspir=[(0, 0.1), (0.9, 0.2), (1.25, 0.7), (1.5, 0)])
    gurgle = np.zeros(n)
    for tt in impulses(0.45, rng, 28):
        put(gurgle, bubble(rng.uniform(140, 320), 0.05, rise=0.5), 1.0 + tt, rng.uniform(0.3, 1.0))
    x = norm(x) + 0.25 * norm(gurgle)
    return fit(ogre_fx(x, drive=2.3), 1.8, 0.25)


# ---------------------------------------------------------------------------
# Frost golem: grinding ice, cracks, an inhuman low grunt
# ---------------------------------------------------------------------------


def golem_voice(dur, rng, f0, amp, vowel="o"):
    x = voice(dur, rng, f0, amp, vowel, scale=0.55, oq=0.65, jitter=0.03, shimmer=0.2, sub=0.5, growl=0.5,
              growl_rate=25, breath=0.15, tilt=2500)
    t = tl(len(x))
    ring = x * np.sin(TAU * 31.0 * t)
    return 0.6 * norm(x) + 0.5 * norm(ring)


def ice_shards(dur, rng, t0=0.0, rate=90.0, decay_rate=0.15, f_range=(1800, 6200), amp_decay=0.3):
    return debris(dur, rng, t0=t0, rate=rate, decay_rate=decay_rate, kinds=("glass",), f_range=f_range, size=0.6,
                  amp_decay=amp_decay)


@sfx("golem_attack", "monster", "Frost golem attack: icy grinding grunt.", level=-13, jitter=0.05, dist=(3.0, 45))
def _golem_attack(rng, v):
    dur = 0.65
    n = ns(dur)
    x = np.zeros(n)
    put(x, golem_voice(0.5, rng, [(0, 58), (0.08, 76), (0.45, 54)], [(0, 0), (0.05, 1), (0.3, 0.8), (0.48, 0)]), 0.02,
        0.9)
    g = grind(dur, rng, rate=60, modes=[(220, 1.5, 1.0), (520, 2, 0.8), (1300, 3, 0.6), (3100, 4, 0.4)], grit=0.6,
              rumble=0.3) * bump(n, 0.3, 2.0)
    x += 0.55 * norm(g)
    x += 0.25 * norm(ice_shards(dur, rng, 0.05, rate=40, decay_rate=0.2))
    x = sat(norm(x), 2.0)
    return verb(x, "stone", 0.12, tail=0.3)


@sfx("golem_hurt", "monster", "Frost golem hurt: ice cracking with a low groan.", variants=2, level=-13,
     jitter=0.06, dist=(3.0, 40), limit=True)
def _golem_hurt(rng, v):
    dur = 0.5
    n = ns(dur)
    x = np.zeros(n)
    put(x, ice_crack(rng, 0.2, f_hi=[7000, 6200][v], f_lo=[420, 520][v], sweep=[0.045, 0.035][v]), 0.0)
    if v == 1:
        put(x, ice_crack(rng, 0.16, 6800, 600, 0.03), 0.085, 0.7)
    x += 0.4 * ice_shards(dur, rng, 0.01, rate=[70, 90][v], decay_rate=0.12)
    gr = golem_voice(0.35, rng, [(0, 70), (0.06, 82), (0.35, 58)], [(0, 0), (0.04, 0.6), (0.33, 0)], "U")
    put(x, gr, 0.04, 0.35)
    x = sat(norm(x), 1.6)
    return verb(x, "stone", 0.12, tail=0.25)


@sfx("golem_death", "monster", "Frost golem death: big crack, ice shatter and heavy collapse.", level=-11,
     jitter=0.03, dist=(3.0, 50), limit=True)
def _golem_death(rng, v):
    dur = 1.4
    n = ns(dur)
    x = np.zeros(n)
    put(x, golem_voice(0.45, rng, [(0, 75), (0.1, 88), (0.45, 50)], [(0, 0), (0.05, 0.8), (0.42, 0)], "o"), 0.0, 0.5)
    put(x, ice_crack(rng, 0.25, 7200, 380, 0.06), 0.02, 1.0)
    put(x, ice_crack(rng, 0.2, 6500, 500, 0.04), 0.1, 0.7)
    x += 0.6 * ice_shards(dur, rng, 0.06, rate=170, decay_rate=0.3, amp_decay=0.45)
    for tt, g, f0 in [(0.22, 1.0, 62), (0.43, 0.8, 55), (0.68, 0.6, 50)]:
        hit = thump(0.4, f0 * 1.6, f0, 0.09, 0.02, rng, noise=0.7, noise_lp=500) + 0.4 * material("stone", 0.4, rng, f0=420)
        put(x, hit, tt, g)
    x += 0.25 * debris(dur, rng, 0.25, rate=50, decay_rate=0.4, kinds=("stone", "glass"), f_range=(900, 4000))
    x = sat(norm(x), 1.8)
    return fit(verb(x, "stone", 0.14), 1.65, 0.25)


# ---------------------------------------------------------------------------
# Cheese mouse: tiny squeaks
# ---------------------------------------------------------------------------


@sfx("mouse_squeak", "monster", "Cheese mouse squeak (idle / alert).", variants=2, level=-20, jitter=0.08,
     dist=(0.8, 14))
def _mouse_squeak(rng, v):
    if v == 0:
        x = squeak(0.15, rng, [(0, 2500), (0.04, 3350), (0.1, 3100), (0.15, 2600)],
                   [(0, 0), (0.008, 1), (0.1, 0.8), (0.15, 0)], vib=0.012, vib_rate=55)
    else:
        x = np.zeros(ns(0.26))
        put(x, squeak(0.08, rng, [(0, 2800), (0.03, 3500), (0.08, 3200)], [(0, 0), (0.006, 1), (0.08, 0)], vib=0.01),
            0.0)
        put(x, squeak(0.12, rng, [(0, 3000), (0.03, 3650), (0.12, 2750)], [(0, 0), (0.008, 1), (0.12, 0)],
                      vib=0.015, vib_rate=48), 0.11, 0.9)
    return verb(x, "small", 0.06, tail=0.05)


@sfx("mouse_hurt", "monster", "Cheese mouse hurt: sharp 'eep!'.", level=-18, jitter=0.07, dist=(0.8, 15))
def _mouse_hurt(rng, v):
    x = squeak(0.2, rng, [(0, 2800), (0.02, 4100), (0.08, 3800), (0.2, 3000)], [(0, 0), (0.004, 1), (0.1, 0.7), (0.2, 0)],
               rough=0.02)
    return verb(sat(norm(x), 1.6), "small", 0.06, tail=0.05)


@sfx("mouse_death", "monster", "Cheese mouse death: wobbly descending squeak and a tiny flop.", level=-18,
     jitter=0.05, dist=(0.8, 15))
def _mouse_death(rng, v):
    dur = 0.5
    n = ns(dur)
    x = np.zeros(n)
    t = tl(ns(0.42))
    sq = squeak(0.42, rng, [(0, 3600), (0.05, 3800), (0.42, 1500)], [(0, 0), (0.01, 1), (0.3, 0.6), (0.42, 0)])
    sq *= 1 - 0.35 * (0.5 + 0.5 * np.sin(TAU * curve(0.42, [(0, 14), (0.42, 22)]) * t)) * np.clip(t / 0.2, 0, 1)
    put(x, sq, 0.0)
    put(x, punch(0.08, rng, f=900, decay=0.01) * 0.5 + 0.3 * thump(0.08, 240, 160, 0.015), 0.43, 0.6)
    return verb(x, "small", 0.06, tail=0.05)


# ---------------------------------------------------------------------------
# Harpy: black bird monster, raspy screech + wing beats
# ---------------------------------------------------------------------------


def harpy_voice(dur, rng, f0, amp, vowel, growl=0.6, sub=0.3, scale=1.25):
    a = voice(dur, rng, f0, amp, vowel, scale=scale, oq=0.4, jitter=0.04, shimmer=0.2, sub=sub, breath=0.25,
              growl=growl, growl_rate=85, nasal=0.35, tilt=7000)
    b = voice(dur, rng, scale_pts(f0, 1.5), amp, vowel, scale=scale * 1.1, oq=0.4, jitter=0.05, shimmer=0.2,
              sub=0.2, breath=0.3, growl=0.8, growl_rate=110, tilt=7000)
    hiss = voice(dur, rng, 100.0, 0.0, vowel, scale=scale, aspir=amp)
    return norm(a) + 0.35 * norm(b) + 0.45 * norm(hiss)


def harpy_fx(x, drive=3.0, wet=0.12):
    return beast_fx(x, drive=drive, low=320, presence=(2500, 4), room="arena", wet=wet, tail=0.3)


@sfx("harpy_screech", "monster", "Harpy screech: raspy, shrieking bird cry (alert / attack).", variants=2,
     level=-13, jitter=0.05, dist=(3.0, 55))
def _harpy_screech(rng, v):
    if v == 0:
        x = harpy_voice(0.85, rng, [(0, 640), (0.08, 905), (0.5, 830), (0.85, 600)],
                        [(0, 0), (0.03, 1), (0.6, 0.85), (0.84, 0)], [(0, "ae"), (0.5, "a"), (0.8, "A")])
    else:
        x = np.zeros(ns(0.85))
        put(x, harpy_voice(0.2, rng, [(0, 700), (0.04, 820), (0.2, 720)], [(0, 0), (0.01, 0.9), (0.16, 0.8), (0.2, 0)],
                           "a"), 0.0, 0.8)
        put(x, harpy_voice(0.58, rng, [(0, 760), (0.06, 960), (0.35, 900), (0.58, 640)],
                           [(0, 0), (0.015, 1), (0.4, 0.85), (0.57, 0)], [(0, "ae"), (0.4, "a")]), 0.24)
    return harpy_fx(x)


@sfx("harpy_hurt", "monster", "Harpy hurt: short squawk.", level=-13, jitter=0.06, dist=(3.0, 45))
def _harpy_hurt(rng, v):
    x = harpy_voice(0.36, rng, [(0, 940), (0.03, 1120), (0.15, 1000), (0.35, 700)],
                    [(0, 0), (0.008, 1), (0.16, 0.8), (0.35, 0)], [(0, "ae"), (0.2, "A")], growl=0.8, sub=0.35)
    return harpy_fx(x, drive=3.2, wet=0.1)


def _flap(rng, size=1.0, feathers=0.35):
    dur = 0.25
    n = ns(dur)
    t = tl(n)
    x = punch(dur, rng, f=430 / size, decay=0.035 * size, attack=0.004)
    x += 0.8 * norm(whoosh(dur, rng, 140 / size, 650 / size, peak=0.18, bw=1.6, sharp=2.0, tail=1.5, slope=-3.0))
    x += feathers * norm(rustle(dur, rng, density=700, band=(1800, 7000), grain=(0.002, 0.008),
                                env=bump(n, 0.15, 2.0, tail=1.5)))
    put(x, bp(white(ns(0.03), rng), 700, 2200, 2) * np.exp(-tl(ns(0.03)) / 0.004), 0.055 * size, 0.5)
    return x * (1 - np.exp(-t / 0.002))


@sfx("harpy_death", "monster", "Harpy death: screech collapsing into a gurgle with failing wing beats.",
     level=-12, jitter=0.04, dist=(3.0, 55))
def _harpy_death(rng, v):
    dur = 1.05
    x = np.zeros(ns(dur))
    put(x, harpy_voice(0.62, rng, [(0, 880), (0.05, 960), (0.4, 600), (0.62, 380)],
                       [(0, 0), (0.015, 1), (0.4, 0.7), (0.62, 0)], [(0, "ae"), (0.3, "a"), (0.6, "o")], growl=0.9,
                       sub=curve(0.62, [(0, 0.2), (0.62, 0.7)])), 0.0)
    for tt, g, sz in [(0.55, 0.5, 1.0), (0.74, 0.35, 0.95), (0.9, 0.2, 0.9)]:
        put(x, _flap(rng, sz), tt, g)
    return harpy_fx(x, drive=2.8)


@sfx("wing_flap", "monster", "Wing beat (harpy, imp): leathery/feathered whump. Not vocal.", variants=3, level=-19,
     jitter=0.08, dist=(1.5, 25))
def _wing_flap(rng, v):
    return verb(_flap(rng, size=[1.0, 0.85, 1.15][v], feathers=[0.35, 0.5, 0.25][v]), "small", 0.05, tail=0.05)


# ---------------------------------------------------------------------------
# Bomb toad
# ---------------------------------------------------------------------------


def croak(dur, rng, rate_pts, amp_pts, modes, jitter=0.08):
    return creak(dur, rng, curve(dur, rate_pts, "exp"), modes, jitter=jitter, pulse_len=0.004,
                 amp=curve(dur, amp_pts, "cos"), irregular=0.15)


@sfx("toad_croak", "monster", "Bomb toad croak (idle / alert).", variants=2, level=-15, jitter=0.07, dist=(1.5, 30))
def _toad_croak(rng, v):
    if v == 0:
        x = np.zeros(ns(0.48))
        put(x, croak(0.27, rng, [(0, 48), (0.27, 58)], [(0, 0), (0.02, 1), (0.22, 0.8), (0.27, 0)],
                     [(420, 5, 1.0), (1100, 4, 0.5), (2100, 5, 0.15)]), 0.0)
        put(x, croak(0.1, rng, [(0, 80), (0.1, 90)], [(0, 0), (0.01, 1), (0.1, 0)],
                     [(520, 5, 1.0), (1300, 4, 0.5)]), 0.32, 0.8)
    else:
        x = np.zeros(ns(0.55))
        put(x, croak(0.4, rng, [(0, 42), (0.3, 50), (0.4, 40)], [(0, 0), (0.03, 1), (0.3, 0.9), (0.4, 0)],
                     [(300, 5, 1.0), (800, 4, 0.5), (1600, 5, 0.12)]), 0.0)
        put(x, bubble(260, 0.08, rise=1.2), 0.42, 0.25)
    x = sat(norm(x), 1.6)
    return verb(x, "small", 0.07, tail=0.08)


@sfx("toad_inflate", "monster", "Bomb toad swelling up before it explodes: stretchy rising rubber squeal.",
     level=-14, jitter=0.0, dist=(1.5, 30))
def _toad_inflate(rng, v):
    dur = 1.2
    n = ns(dur)
    rate = curve(dur, [(0, 24), (0.5, 55), (1.0, 170), (1.15, 240)], "exp")
    rate = rate * (1 + 0.06 * smooth_noise(n, rng, 12))
    src = saw(rate, ph0=rng.uniform(0, 1)) + 0.4 * square(rate * 1.003, ph0=rng.uniform(0, 1), duty=0.3)
    src *= 1 + 0.25 * smooth_noise(n, rng, 40)  # stick-slip unevenness
    r1 = tv_biquad(src, "bp", curve(dur, [(0, 300), (1.2, 950)], "exp"), 5.0)
    r2 = tv_biquad(src, "bp", curve(dur, [(0, 850), (1.2, 2400)], "exp"), 6.0)
    r3 = tv_biquad(src, "bp", curve(dur, [(0, 180), (1.2, 420)], "exp"), 3.0)
    rub = r1 + 0.7 * r2 + 0.5 * r3
    rub /= np.sqrt(np.mean(rub ** 2)) + 1e-9
    hiss = bp(white(n, rng), 900, 3500, 2)
    hiss /= np.sqrt(np.mean(hiss ** 2))
    gul = np.zeros(n)
    for tt in impulses(0.4, rng, 25):
        put(gul, bubble(rng.uniform(150, 350), 0.06, rise=0.8), tt, rng.uniform(0.3, 1))
    gul /= np.max(np.abs(gul)) + 1e-9
    env = curve(dur, [(0, 0), (0.12, 0.3), (0.8, 0.7), (1.13, 1.0), (1.2, 0)], "cos")
    x = (rub + 0.18 * hiss * curve(dur, [(0, 0.3), (1.2, 1.0)])) * env + 0.6 * gul
    return verb(sat(norm(x), 1.5), "small", 0.07, tail=0.06)


@sfx("toad_hurt", "monster", "Bomb toad hurt: squelchy 'gurk!'.", level=-14, jitter=0.07, dist=(1.5, 28), limit=True)
def _toad_hurt(rng, v):
    x = np.zeros(ns(0.35))
    put(x, croak(0.13, rng, [(0, 70), (0.13, 95)], [(0, 0), (0.008, 1), (0.13, 0)], [(480, 4, 1.0), (1250, 4, 0.6)]),
        0.0)
    put(x, splat(0.25, rng, 1700, 300, 2.5, 0.05), 0.02, 0.7)
    return verb(sat(norm(x), 1.8), "small", 0.07, tail=0.06)


@sfx("toad_hop", "monster", "Bomb toad hop landing: wet squelch.", variants=2, level=-21, jitter=0.08, dist=(1.0, 18))
def _toad_hop(rng, v):
    dur = 0.3
    x = splat(dur, rng, [1500, 1300][v], [280, 240][v], 2.2, 0.045)
    x += 0.6 * punch(dur, rng, f=500, decay=0.015)
    put(x, bubble([520, 440][v], 0.05, rise=1.0), 0.0, 0.25)
    return verb(x, "small", 0.06, tail=0.04)


# ---------------------------------------------------------------------------
# Plague nurse: raspy and muffled behind a beaked mask
# ---------------------------------------------------------------------------


def rasp(dur, rng, f0=45.0, vowel="a", amp=1.0):
    """Vocal-fry rasp: slow irregular pulses through a vowel."""
    return voice(dur, rng, f0, amp, vowel, scale=1.05, oq=0.3, jitter=0.25, shimmer=0.5, sub=0.5, breath=0.3,
                 growl=0.6, growl_rate=20)


@sfx("nurse_hiss", "monster", "Plague nurse hiss: raspy breath through the beak mask (alert / threat).",
     variants=2, level=-16, jitter=0.06, dist=(1.5, 25))
def _nurse_hiss(rng, v):
    if v == 0:
        dur = 0.75
        n = ns(dur)
        x = voice(dur, rng, 120.0, 0.0, [(0, "a"), (0.4, "e"), (0.7, "i")], scale=1.05,
                  aspir=[(0, 0), (0.06, 1.0), (0.45, 0.8), (0.7, 0)])
        x = norm(x) + 0.45 * norm(rasp(dur, rng, 42, "a", curve(dur, [(0, 0), (0.08, 1), (0.5, 0.6), (0.7, 0)], "cos")))
        s = bp(white(n, rng), 3200, 6500, 2) * curve(dur, [(0, 0), (0.4, 0), (0.55, 0.6), (0.74, 0)], "cos")
        x += 0.35 * norm(s)
    else:
        dur = 0.8
        n = ns(dur)
        w = espeak("h'ESk@s:", "en-us+whisper", pitch=40, speed=170)
        w = resample_speed(w, 0.92)
        x = np.zeros(n)
        put(x, norm(w), 0.0, 0.9)
        x += 0.35 * norm(rasp(dur, rng, 38, "e", curve(dur, [(0, 0), (0.1, 1), (0.6, 0.7), (0.8, 0)], "cos")))
    x = mask_fx(x)
    return verb(sat(norm(x), 1.6), "small", 0.08, tail=0.1)


@sfx("nurse_hurt", "monster", "Plague nurse hurt: muffled strained cry behind the mask.", level=-15, jitter=0.06,
     dist=(1.5, 25))
def _nurse_hurt(rng, v):
    dur = 0.42
    x = voice(dur, rng, [(0, 255), (0.04, 335), (0.2, 300), (0.4, 215)], [(0, 0), (0.012, 1), (0.25, 0.75), (0.4, 0)],
              [(0, "a"), (0.25, "A")], scale=1.1, oq=0.45, jitter=0.03, shimmer=0.15, sub=0.25, breath=0.3,
              growl=0.45, aspir=[(0, 0.6), (0.03, 0.2), (0.35, 0.4), (0.42, 0)])
    x = mask_fx(x)
    return verb(sat(norm(x), 2.2), "small", 0.08, tail=0.08)


@sfx("nurse_death", "monster", "Plague nurse death: rasping wail that dies into a bubbling exhale.", level=-14,
     jitter=0.03, dist=(1.5, 28))
def _nurse_death(rng, v):
    dur = 1.05
    n = ns(dur)
    x = voice(0.7, rng, [(0, 300), (0.06, 330), (0.4, 230), (0.7, 150)], [(0, 0), (0.02, 1), (0.4, 0.7), (0.68, 0)],
              [(0, "a"), (0.3, "o"), (0.65, "u")], scale=1.1, oq=0.45, jitter=0.04, shimmer=0.2,
              sub=curve(0.7, [(0, 0.2), (0.7, 0.7)]), breath=0.35, growl=0.6)
    y = np.zeros(n)
    put(y, x, 0.0)
    ex = voice(0.5, rng, 100.0, 0.0, [(0, "a"), (0.5, "U")], aspir=[(0, 0), (0.1, 0.9), (0.5, 0)])
    put(y, ex, 0.5, 0.6)
    gl = np.zeros(n)
    for tt in impulses(0.4, rng, 35):
        put(gl, bubble(rng.uniform(250, 600), 0.04, rise=0.7), 0.55 + tt, rng.uniform(0.2, 0.7))
    y = norm(y) + 0.25 * norm(gl)
    return verb(sat(norm(mask_fx(y)), 1.8), "small", 0.09, tail=0.1)


# ---------------------------------------------------------------------------
# Gargoyle: living stone
# ---------------------------------------------------------------------------


@sfx("gargoyle_grind", "monster", "Gargoyle moving while unwatched: heavy stone grinding.", level=-16,
     jitter=0.06, dist=(2.0, 30))
def _gargoyle_grind(rng, v):
    dur = 0.8
    x = grind(dur, rng, rate=42, grit=0.4, rumble=0.5)
    x *= curve(dur, [(0, 0), (0.1, 0.8), (0.35, 1.0), (0.6, 0.85), (0.8, 0)], "cos")
    return verb(sat(norm(x), 1.4), "stone", 0.1, tail=0.25)


@sfx("gargoyle_hurt", "monster", "Gargoyle hurt: stone chips breaking off.", level=-15, jitter=0.07, dist=(2.0, 30), limit=True)
def _gargoyle_hurt(rng, v):
    dur = 0.45
    n = ns(dur)
    x = np.zeros(n)
    put(x, material("stone", 0.2, rng, f0=1300, hard=1.3, damp=1.0) + 0.6 * punch(0.2, rng, f=1200, decay=0.008), 0.0)
    x += 0.5 * debris(dur, rng, 0.02, rate=80, decay_rate=0.08, kinds=("stone",), f_range=(1800, 5000), size=0.5)
    g = grind(0.3, rng, rate=55, grit=0.3, rumble=0.2) * bump(ns(0.3), 0.3, 2.0)
    put(x, norm(g), 0.04, 0.35)
    return verb(sat(norm(x), 1.4), "stone", 0.1, tail=0.2)


@sfx("gargoyle_death", "monster", "Gargoyle death: the statue cracks and crumbles into rubble.", level=-11,
     jitter=0.03, dist=(2.0, 40), limit=True)
def _gargoyle_death(rng, v):
    dur = 1.2
    n = ns(dur)
    x = np.zeros(n)
    put(x, material("stone", 0.3, rng, f0=700, hard=1.2) + punch(0.3, rng, f=900, decay=0.012), 0.0)
    put(x, thump(0.4, 90, 48, 0.08, 0.02, rng, noise=0.7, noise_lp=600), 0.05, 0.8)
    x += 0.7 * debris(dur, rng, 0.05, rate=140, decay_rate=0.35, kinds=("stone",), f_range=(500, 3500), size=0.8,
                      amp_decay=0.5)
    for tt, g in [(0.25, 0.7), (0.4, 0.5), (0.62, 0.4)]:
        put(x, thump(0.3, 120, 60, 0.05, 0.015, rng, noise=0.8, noise_lp=700) + 0.5 * material("stone", 0.3, rng, f0=500),
            tt, g)
    dust = lp(colored(n, rng, -2), 2500, 2) * curve(dur, [(0, 0), (0.1, 0.3), (0.5, 0.2), (1.2, 0)], "cos")
    x += 0.3 * norm(dust) * np.max(np.abs(x)) * 0.5
    return fit(verb(sat(norm(x), 1.5), "stone", 0.14), 1.45, 0.25)


# ---------------------------------------------------------------------------
# Mimic: treasure chest with teeth
# ---------------------------------------------------------------------------


def chest_growl(dur, rng, f0, amp, vowel="o"):
    x = voice(dur, rng, f0, amp, vowel, scale=0.78, oq=0.55, jitter=0.03, shimmer=0.2, sub=0.45, breath=0.2,
              growl=0.6, growl_rate=28, tilt=3000)
    return comb(x, 0.0042, 0.55, 0.6)  # hollow chest box colouration


def coin(rng, f0=None):
    f0 = f0 or rng.uniform(2600, 3600)
    return material("metal", 0.3, rng, f0=f0, damp=0.25, hard=0.6) * 0.6


@sfx("mimic_chomp", "monster", "Mimic chomp: wooden jaws slamming shut with a wet bite.", variants=2, level=-13,
     jitter=0.06, dist=(1.5, 28), limit=True)
def _mimic_chomp(rng, v):
    dur = 0.38
    n = ns(dur)
    x = np.zeros(n)
    bites = [0.0] if v == 0 else [0.0, 0.13]
    for i, tt in enumerate(bites):
        g = 1.0 if i == len(bites) - 1 else 0.7
        jaw = material("box", 0.3, rng, f0=[175, 160][v] * (1 + 0.1 * i), hard=1.2, damp=0.7)
        jaw += 0.6 * material("wood", 0.3, rng, f0=720, damp=0.6) + 0.8 * punch(0.3, rng, f=700, decay=0.02)
        teeth = hp(white(ns(0.02), rng), 2200, 2) * np.exp(-tl(ns(0.02)) / 0.0018)
        put(x, jaw, tt, g)
        put(x, teeth, tt, 0.7 * g)
        put(x, splat(0.25, rng, 2000, 380, 2.8, 0.045), tt + 0.008, 0.55 * g)
    put(x, chest_growl(0.22, rng, [(0, 90), (0.1, 105), (0.22, 80)], [(0, 0), (0.03, 0.6), (0.2, 0)]),
        bites[-1] + 0.02, 0.35)
    return verb(sat(norm(x), 1.7), "small", 0.08, tail=0.08)


@sfx("mimic_lid_open", "monster", "Mimic lid creaking open (reveal / about to bite).", level=-16, jitter=0.05,
     dist=(1.5, 25))
def _mimic_lid_open(rng, v):
    dur = 0.6
    cr = creak(dur, rng, curve(dur, [(0, 22), (0.15, 70), (0.35, 95), (0.55, 55)], "exp"),
               [(340, 4, 1.0), (780, 5, 0.7), (1650, 6, 0.4), (2900, 7, 0.15)], jitter=0.3, irregular=0.4)
    cr *= curve(dur, [(0, 0), (0.05, 0.7), (0.3, 1.0), (0.55, 0.6), (0.6, 0)], "cos")
    x = norm(cr)
    put(x, material("metal", 0.15, rng, f0=1900, damp=0.12), 0.0, 0.3)
    br = voice(0.45, rng, 90.0, 0.0, [(0, "o"), (0.45, "a")], scale=0.8,
               aspir=[(0, 0), (0.2, 0.7), (0.45, 0)])
    put(x, norm(comb(br, 0.0042, 0.5, 0.6)), 0.12, 0.25)
    return verb(x, "small", 0.08, tail=0.08)


@sfx("mimic_lid_close", "monster", "Mimic lid shutting: wooden thunk with a latch clink.", level=-16, jitter=0.06,
     dist=(1.5, 25), limit=True)
def _mimic_lid_close(rng, v):
    dur = 0.4
    x = material("box", dur, rng, f0=145, hard=1.0, damp=0.8) + punch(dur, rng, f=600, decay=0.025)
    put(x, material("metal", 0.3, rng, f0=1800, damp=0.15) * 0.35, 0.012)
    put(x, material("metal", 0.2, rng, f0=2450, damp=0.1) * 0.2, 0.045)
    return verb(sat(norm(x), 1.4), "small", 0.08, tail=0.08)


@sfx("mimic_hurt", "monster", "Mimic hurt: wood cracks and a hollow, creaky groan.", level=-14, jitter=0.06,
     dist=(1.5, 25), limit=True)
def _mimic_hurt(rng, v):
    dur = 0.48
    n = ns(dur)
    x = np.zeros(n)
    put(x, material("wood", 0.2, rng, f0=650, hard=1.3) + 0.8 * hp(white(ns(0.2), rng), 1500, 2) * np.exp(-tl(ns(0.2)) / 0.003),
        0.0)
    put(x, crackle(0.12, rng, rate=150, band=(1200, 5000), decay_rate=0.05), 0.005, 0.4)
    put(x, chest_growl(0.4, rng, [(0, 130), (0.06, 175), (0.4, 105)], [(0, 0), (0.02, 1), (0.25, 0.7), (0.4, 0)],
                       [(0, "o"), (0.3, "u")]), 0.05, 0.75)
    return verb(sat(norm(x), 1.8), "small", 0.08, tail=0.08)


@sfx("mimic_death", "monster", "Mimic death: wood splintering, a dying groan and spilled coins.", level=-12,
     jitter=0.03, dist=(1.5, 30), limit=True)
def _mimic_death(rng, v):
    dur = 1.25
    n = ns(dur)
    x = np.zeros(n)
    put(x, material("box", 0.3, rng, f0=160, hard=1.3) + punch(0.3, rng, f=800, decay=0.02), 0.0)
    x += 0.6 * crackle(dur, rng, rate=110, band=(900, 5000), alpha=1.4, decay_rate=0.25)
    x += 0.4 * debris(dur, rng, 0.03, rate=40, decay_rate=0.25, kinds=("wood",), f_range=(400, 1500))
    put(x, chest_growl(0.75, rng, [(0, 150), (0.08, 165), (0.75, 60)], [(0, 0), (0.04, 1), (0.5, 0.5), (0.74, 0)],
                       [(0, "o"), (0.6, "u")]), 0.06, 0.7)
    for i in range(9):
        put(x, coin(rng), 0.35 + 0.65 * rng.random() ** 1.3, rng.uniform(0.15, 0.4))
    put(x, material("box", 0.3, rng, f0=140, damp=0.8) * 0.5, 0.95)
    return fit(verb(sat(norm(x), 1.5), "small", 0.1), 1.4, 0.15)


# ---------------------------------------------------------------------------
# Creature footsteps
# ---------------------------------------------------------------------------


@sfx("footstep_light", "monster", "Small claws/paws patter (mouse, imp on foot).", variants=4, level=-28, jitter=0.08,
     dist=(0.6, 10))
def _footstep_light(rng, v):
    dur = 0.12
    n = ns(dur)
    x = np.zeros(n)
    for k, tt in enumerate([0.0, rng.uniform(0.012, 0.03)]):
        tap = 0.5 * punch(0.04, rng, f=900, decay=0.005)
        tap += bp(white(ns(0.04), rng), 2500, 6500, 2) * np.exp(-tl(ns(0.04)) / 0.0009)
        tap += 0.25 * material("stone", 0.04, rng, f0=rng.uniform(2600, 3600), damp=0.4)
        put(x, tap, tt, 1.0 if k == 0 else rng.uniform(0.5, 0.8))
    return verb(x, "small", 0.06, tail=0.03)


@sfx("footstep_medium", "monster", "Goblin-sized bare feet / small boots on stone.", variants=4, level=-26,
     jitter=0.07, dist=(0.8, 15))
def _footstep_medium(rng, v):
    dur = 0.15
    n = ns(dur)
    t = tl(n)
    x = punch(dur, rng, f=rng.uniform(380, 480), decay=0.013)
    x += 0.8 * bp(white(n, rng), 700, 3000, 2) * np.exp(-t / 0.008) * (1 - np.exp(-t / 0.0006))
    x += 0.3 * thump(dur, 170, 110, 0.012, 0.006)
    if v % 2 == 1:
        put(x, hp(white(ns(0.02), rng), 2800, 2) * np.exp(-tl(ns(0.02)) / 0.001), rng.uniform(0.01, 0.025), 0.4)
    return verb(x, "small", 0.07, tail=0.03)


@sfx("footstep_heavy", "monster", "Ogre/golem stomp with a low thump and grit.", variants=4, level=-18, jitter=0.06,
     dist=(3.0, 45), limit=True)
def _footstep_heavy(rng, v):
    dur = 0.42
    n = ns(dur)
    x = thump(dur, rng.uniform(62, 72), rng.uniform(34, 40), 0.085, 0.022, rng, noise=0.7, noise_lp=350, click=0.05)
    x += 0.6 * punch(dur, rng, f=300, decay=0.04)
    x += 0.25 * debris(dur, rng, 0.01, rate=60, decay_rate=0.12, kinds=("stone",), f_range=(700, 3000), size=0.5)
    x += 0.15 * lp(colored(n, rng, -6), 120, 2) * np.exp(-tl(n) / 0.15)
    x = sat(norm(x), 1.5)
    return verb(x, "stone", 0.1, tail=0.25)


@sfx("footstep_stone", "monster", "Stone feet on stone (gargoyle walking).", variants=3, level=-23, jitter=0.07,
     dist=(1.5, 25))
def _footstep_stone(rng, v):
    dur = 0.26
    n = ns(dur)
    t = tl(n)
    x = material("stone", dur, rng, f0=rng.uniform(520, 780), hard=1.1, damp=1.4)
    x += 0.6 * punch(dur, rng, f=500, decay=0.02) + 0.4 * thump(dur, 110, 70, 0.03, 0.01)
    sc = bp(white(n, rng), 900, 4000, 2) * np.clip(t / 0.01, 0, 1) * np.exp(-t / 0.035)
    put(x, sc, rng.uniform(0.015, 0.03), 0.3)
    x += 0.2 * crackle(dur, rng, rate=90, band=(2000, 6000), decay_rate=0.05, cap=3)
    return verb(x, "stone", 0.08, tail=0.15)


# ---------------------------------------------------------------------------
# World: explosions, fire, ice, impacts, doors, mechanisms
# ---------------------------------------------------------------------------


def flame_roar(dur, rng, lp_f=700.0, flutter=0.5):
    """Turbulent flame body: low noise with fast random flutter."""
    n = ns(dur)
    x = lp(colored(n, rng, -4.0), lp_f, 2)
    fl = np.clip(1 + flutter * smooth_noise(n, rng, 22.0), 0.1, None)
    return x * fl


@sfx("explosion", "world", "Big explosion (bomb toad, explosive barrel): boom, crack and a falling debris tail.",
     variants=2, level=-8, jitter=0.05, dist=(5.0, 90), limit=True)
def _explosion(rng, v):
    dur = 1.6
    n = ns(dur)
    t = tl(n)
    boom = np.sin(TAU * phase_of(42 + [70, 85][v] * np.exp(-t / 0.06))) * np.exp(-t / 0.22) * (1 - np.exp(-t / 0.003))
    body = lp(white(n, rng), 650, 4) * np.exp(-t / 0.28) * (1 - np.exp(-t / 0.004))
    crack = bp(white(n, rng), 700, 6500, 2) * np.exp(-t / 0.014)
    mid = bp(white(n, rng), 250, 2500, 2) * np.exp(-t / 0.15) * (1 - np.exp(-t / 0.003))
    deb = debris(dur, rng, 0.12, rate=70, decay_rate=0.5, kinds=("stone",) if v == 0 else ("wood", "box"),
                 f_range=(400, 2800) if v == 0 else (250, 1500), size=0.8, amp_decay=0.5)
    tail = lp(colored(n, rng, -6), 220, 2) * np.exp(-t / 0.6) * (1 - np.exp(-t / 0.05))
    fire = flame_roar(dur, rng, 1100) * np.exp(-t / 0.45) * (1 - np.exp(-t / 0.03))
    x = 0.8 * norm(boom) + 1.0 * rms_n(body) * 0.25 + 0.5 * rms_n(mid) * 0.25 + 0.5 * norm(crack)
    x += 0.3 * norm(deb) + 0.5 * rms_n(tail) * 0.2 + 0.35 * rms_n(fire) * 0.2
    x = sat(norm(x), 3.0)
    x = compress(x, -16, 3.0, 0.002, 0.2)
    return fit(verb(x, "arena", 0.18), 1.8, 0.3)


@sfx("fire_ignite", "world", "Flames catching: a 'whoomp' as fire ignites (fire trench, torches).", level=-14,
     jitter=0.05, dist=(2.0, 40))
def _fire_ignite(rng, v):
    dur = 0.7
    n = ns(dur)
    nz = colored(n, rng, -3.0)
    wh = tv_biquad(nz, "lp", curve(dur, [(0, 150), (0.12, 2600), (0.7, 900)], "exp"), 1.2)
    wh *= curve(dur, [(0, 0), (0.08, 1.0), (0.2, 0.75), (0.7, 0)], "cos")
    low = np.sin(TAU * phase_of(curve(dur, [(0, 45), (0.1, 75), (0.4, 50)], "exp"))) * bump(n, 0.14, 2.0)
    cr = crackle(dur, rng, rate=70, band=(1200, 6000), t0=0.08, decay_rate=0.4)
    roar = flame_roar(dur, rng, 650) * curve(dur, [(0, 0), (0.1, 1.0), (0.7, 0)], "cos")
    x = norm(wh) + 0.6 * low + 0.35 * norm(cr) + 0.5 * norm(roar)
    return verb(sat(norm(x), 1.6), "room", 0.1, tail=0.15)


@sfx("fire_loop", "world", "Crackling fire bed (fire trench, braziers); loop=true.", loop=True, level=-24, jitter=0.0,
     dist=(1.5, 25))
def _fire_loop(rng, v):
    T = 4.0
    n = ns(T)
    roar = periodic(colored(n, rng, -5.0), lambda s: lp(s, 650, 2))
    roar *= np.clip(1 + 0.45 * smooth_noise(n, rng, 14.0), 0.1, None)
    hiss = periodic(colored(n, rng, -1.0), lambda s: bp(s, 2200, 6000, 2))
    cr = crackle(T, rng, rate=42, band=(1000, 6000), alpha=1.9, wrap=True, cap=4.0)
    pops = np.zeros(n)
    for tt in rng.uniform(0, T, 7):
        put_wrap(pops, bubble(rng.uniform(250, 600), 0.03, rise=0.5) + 0.5 * kburst(rng, 800, 4000, 0.03)[:ns(0.03)],
                 tt, rng.uniform(0.4, 1.0))
    x = 0.55 * norm(roar) + 0.6 * norm(cr) + 0.35 * norm(pops) + 0.06 * norm(hiss)
    return verb_loop(x, "small", 0.12)


@sfx("fireball_cast", "world", "Imp throws a fireball: fiery whoosh/roar.", level=-14, jitter=0.06, dist=(2.0, 40))
def _fireball_cast(rng, v):
    dur = 0.6
    n = ns(dur)
    w = whoosh(dur, rng, 180, 1900, peak=0.35, bw=1.6, sharp=2.2, slope=-2.5)
    roar = flame_roar(dur, rng, 900, 0.7) * bump(n, 0.3, 2.0)
    cr = crackle(dur, rng, rate=90, band=(1500, 6000), decay_rate=0.3)
    low = np.sin(TAU * phase_of(curve(dur, [(0, 60), (0.15, 90), (0.6, 55)], "exp"))) * bump(n, 0.2, 2.5)
    x = norm(w) + 0.7 * norm(roar) + 0.3 * norm(cr) + 0.4 * low
    return verb(sat(norm(x), 1.5), "small", 0.08, tail=0.1)


@sfx("fireball_impact", "world", "Fireball hits: fiery burst with crackle and sizzle.", level=-12, jitter=0.06,
     dist=(2.0, 45), limit=True)
def _fireball_impact(rng, v):
    dur = 0.7
    n = ns(dur)
    t = tl(n)
    hit = thump(dur, 110, 50, 0.06, 0.02, rng, noise=0.8, noise_lp=900, click=0.2)
    burst = lp(white(n, rng), 3000, 2) * np.exp(-t / 0.06) * (1 - np.exp(-t / 0.002))
    roar = flame_roar(dur, rng, 1000, 0.7) * np.exp(-t / 0.18) * (1 - np.exp(-t / 0.01))
    cr = crackle(dur, rng, rate=120, band=(1200, 6000), decay_rate=0.25, t0=0.01)
    hs = bp(white(n, rng), 2500, 6500, 2) * np.exp(-t / 0.25) * (1 - np.exp(-t / 0.05))
    x = norm(hit) + 0.7 * norm(burst) + 0.7 * norm(roar) + 0.35 * norm(cr) + 0.12 * norm(hs)
    return verb(sat(norm(x), 1.8), "room", 0.1, tail=0.15)


@sfx("ice_bolt_cast", "world", "Frost golem ice bolt: crystalline whoosh.", level=-15, jitter=0.06, dist=(2.0, 40))
def _ice_bolt_cast(rng, v):
    dur = 0.5
    n = ns(dur)
    w = whoosh(dur, rng, 900, 5000, peak=0.35, bw=0.9, sharp=2.4, slope=-1.2)
    zing = np.sin(TAU * phase_of(curve(dur, [(0, 1500), (0.3, 3200), (0.5, 2600)], "exp")))
    zing = (zing + 0.4 * np.sin(TAU * phase_of(curve(dur, [(0, 2250), (0.3, 4800), (0.5, 3900)], "exp")))) * bump(n, 0.35, 2.0)
    sh = np.zeros(n)
    for i in range(8):
        put(sh, glass_ping(0.25, rng, rng.uniform(1800, 4200), decay=0.25, bright=0.4), 0.05 + 0.04 * i + rng.uniform(0, 0.02),
            rng.uniform(0.3, 0.8))
    frost = crackle(dur, rng, rate=300, band=(2500, 6500), alpha=3.0, cap=2.0) * bump(n, 0.4, 1.5)
    x = norm(w) + 0.3 * norm(zing) + 0.35 * norm(sh) + 0.2 * norm(frost)
    return verb(lp(x, 8000, 2), "magic", 0.15, tail=0.15)


@sfx("ice_impact", "world", "Ice bolt hits / ice block breaks: crack and shatter.", level=-13, jitter=0.06,
     dist=(2.0, 40), limit=True)
def _ice_impact(rng, v):
    dur = 0.6
    n = ns(dur)
    x = np.zeros(n)
    put(x, ice_crack(rng, 0.2, 6500, 500, 0.035), 0.0)
    put(x, thump(0.3, 140, 70, 0.04, 0.012, rng, noise=0.6, noise_lp=1200), 0.0, 0.6)
    x += 0.8 * ice_shards(dur, rng, 0.005, rate=200, decay_rate=0.12, amp_decay=0.2)
    return verb(sat(norm(x), 1.5), "room", 0.1, tail=0.15)


@sfx("spike_impale", "world", "Something impaled on a spike: sharp stab and a wet squish.", level=-13, jitter=0.06,
     dist=(1.5, 30), limit=True)
def _spike_impale(rng, v):
    dur = 0.45
    n = ns(dur)
    t = tl(n)
    stab = bp(white(n, rng), 1200, 7000, 2) * np.exp(-t / 0.003) + 0.6 * material("wood", dur, rng, f0=900, hard=1.4, damp=0.4)
    thud = punch(dur, rng, f=600, decay=0.025) + 0.5 * thump(dur, 120, 60, 0.04, 0.015)
    x = norm(stab) + 0.8 * norm(thud)
    put(x, splat(0.35, rng, 1600, 260, 2.6, 0.07), 0.012, 0.8)
    put(x, splat(0.25, rng, 900, 200, 2.0, 0.05), 0.09, 0.4)
    return verb(sat(norm(x), 1.6), "small", 0.07, tail=0.08)


@sfx("fall_whistle", "world", "Something falling into the abyss: descending whistle receding into wind.",
     level=-16, jitter=0.04, dist=(3.0, 60))
def _fall_whistle(rng, v):
    dur = 1.3
    n = ns(dur)
    t = tl(n)
    f = curve(dur, [(0, 1650), (1.3, 330)], "exp") * (1 + 0.006 * np.sin(TAU * 6 * t))
    p = phase_of(f)
    wh = np.sin(TAU * p) + 0.18 * np.sin(TAU * 2 * p + 0.3)
    wh *= curve(dur, [(0, 0), (0.06, 1.0), (0.7, 0.6), (1.3, 0)], "cos")
    wh = tv_biquad(wh, "lp", curve(dur, [(0, 7000), (1.3, 900)], "exp"), 0.7)
    wind = whoosh(dur, rng, 300, 1500, peak=0.15, bw=1.6, sharp=1.5, tail=3.0, slope=-3.0)
    x = norm(wh) + 0.35 * norm(wind)
    return verb(x, "chasm", 0.18, tail=0.2)


@sfx("body_impact", "world", "Body slamming the floor, a wall or another body.", variants=3, level=-14, jitter=0.07,
     dist=(1.5, 30), limit=True)
def _body_impact(rng, v):
    dur = 0.35
    n = ns(dur)
    t = tl(n)
    x = punch(dur, rng, f=[420, 650, 520][v], decay=0.03)
    x += [0.9, 0.6, 0.7][v] * thump(dur, [85, 110, 95][v], [44, 55, 50][v], 0.05, 0.02)
    x += 0.5 * bp(white(n, rng), 800, 3000, 2) * np.exp(-t / 0.008) * (1 - np.exp(-t / 0.0005))
    x += 0.25 * rustle(dur, rng, density=240, band=(1200, 5000), env=np.exp(-t / 0.06))
    if v == 0:
        x += 0.15 * debris(dur, rng, 0.01, rate=40, decay_rate=0.08, kinds=("stone",), f_range=(1500, 3500), size=0.4)
    if v == 1:
        x += 0.4 * material("stone", dur, rng, f0=320, damp=1.5)
    if v == 2:
        put(x, punch(0.2, rng, f=600, decay=0.02) + 0.5 * bp(white(ns(0.2), rng), 900, 3000, 2) * np.exp(-tl(ns(0.2)) / 0.007),
            0.045, 0.7)
    x = sat(norm(x), 1.6)
    return verb(x, ["small", "room", "small"][v], [0.07, 0.14, 0.07][v], tail=0.1)


@sfx("wood_impact", "world", "Crate or barrel knocked / bumped.", variants=2, level=-16, jitter=0.07, dist=(1.5, 30), limit=True)
def _wood_impact(rng, v):
    dur = 0.3
    x = material("box", dur, rng, f0=[190, 150][v], hard=1.1, damp=0.8)
    x += 0.5 * material("wood", dur, rng, f0=[760, 620][v], damp=0.6) + 0.6 * punch(dur, rng, f=650, decay=0.015)
    return verb(sat(norm(x), 1.3), "room", 0.1, tail=0.12)


@sfx("wood_break", "world", "Crate smashing apart.", level=-12, jitter=0.05, dist=(2.0, 40), limit=True)
def _wood_break(rng, v):
    dur = 0.7
    n = ns(dur)
    x = material("box", dur, rng, f0=170, hard=1.3) + 0.8 * punch(dur, rng, f=800, decay=0.02)
    x += 0.7 * crackle(dur, rng, rate=220, band=(700, 5000), alpha=1.3, decay_rate=0.12, cap=6)
    x += 0.5 * debris(dur, rng, 0.03, rate=60, decay_rate=0.25, kinds=("wood",), f_range=(350, 1400), size=0.8,
                      amp_decay=0.35)
    x += 0.2 * lp(white(n, rng), 2000, 2) * np.exp(-tl(n) / 0.15)
    return verb(sat(norm(x), 1.6), "room", 0.12, tail=0.15)


@sfx("door_open", "world", "Heavy wooden door creaking open.", level=-15, jitter=0.04, dist=(2.0, 35))
def _door_open(rng, v):
    dur = 1.0
    n = ns(dur)
    x = np.zeros(n)
    put(x, material("metal", 0.2, rng, f0=1350, damp=0.15) + 0.5 * punch(0.2, rng, f=1500, decay=0.006), 0.0, 0.5)
    cr = creak(0.85, rng, curve(0.85, [(0, 26), (0.2, 55), (0.5, 72), (0.8, 40)], "exp"),
               [(210, 4, 1.0), (470, 5, 0.8), (980, 6, 0.5), (1900, 7, 0.25)], jitter=0.3, irregular=0.4)
    cr *= curve(0.85, [(0, 0), (0.08, 0.8), (0.4, 1.0), (0.85, 0)], "cos")
    put(x, norm(cr), 0.08)
    groan = bp(white(n, rng), 90, 260, 2) * curve(dur, [(0, 0), (0.2, 1), (0.85, 0.5), (1.0, 0)], "cos")
    x += 0.15 * norm(groan)
    return verb(x, "room", 0.14, tail=0.25)


@sfx("door_close", "world", "Heavy wooden door shutting: deep thud and latch.", level=-14, jitter=0.05,
     dist=(2.0, 40), limit=True)
def _door_close(rng, v):
    dur = 0.4
    x = material("box", dur, rng, f0=105, hard=0.9, damp=1.0) + punch(dur, rng, f=350, decay=0.03)
    x += 0.7 * thump(dur, 80, 52, 0.05, 0.02)
    put(x, material("metal", 0.3, rng, f0=1250, damp=0.18) + 0.4 * punch(0.3, rng, f=2000, decay=0.004), 0.03, 0.35)
    return verb(sat(norm(x), 1.4), "room", 0.16, tail=0.3)


@sfx("door_locked", "world", "Rattling a locked door handle.", level=-16, jitter=0.05, dist=(1.5, 25), limit=True)
def _door_locked(rng, v):
    dur = 0.5
    n = ns(dur)
    x = np.zeros(n)
    tt = 0.0
    for i in range(5):
        hit = material("metal", 0.15, rng, f0=rng.uniform(850, 1150), damp=0.12, hard=0.8)
        hit += 0.5 * material("box", 0.15, rng, f0=140, damp=0.4) + 0.4 * punch(0.15, rng, f=1200, decay=0.004)
        put(x, hit, tt, [1.0, 0.7, 0.9, 0.6, 0.8][i])
        tt += rng.uniform(0.065, 0.095)
    return verb(x, "room", 0.12, tail=0.12)


@sfx("door_smash", "world", "Door bursting open / smashed apart.", level=-11, jitter=0.04, dist=(3.0, 50), limit=True)
def _door_smash(rng, v):
    dur = 1.0
    x = material("box", dur, rng, f0=95, hard=1.4) + punch(dur, rng, f=600, decay=0.04)
    x += 0.8 * thump(dur, 90, 45, 0.08, 0.02, rng, noise=0.5, noise_lp=800)
    x += 0.8 * crackle(dur, rng, rate=240, band=(600, 5000), alpha=1.2, decay_rate=0.15, cap=6)
    x += 0.5 * debris(dur, rng, 0.05, rate=70, decay_rate=0.35, kinds=("wood", "box"), f_range=(200, 1200),
                      size=0.9, amp_decay=0.45)
    put(x, material("metal", 0.6, rng, f0=900, damp=0.4), 0.06, 0.25)
    return fit(verb(sat(norm(x), 1.8), "room", 0.16), 1.2, 0.2)


@sfx("lever_pull", "world", "Iron lever: ratchet clicks then a heavy clunk.", level=-15, jitter=0.03, dist=(2.0, 35), limit=True)
def _lever_pull(rng, v):
    dur = 0.6
    n = ns(dur)
    x = np.zeros(n)
    k = 0
    tt = 0.0
    while tt < 0.36:
        click = material("metal", 0.06, rng, f0=rng.uniform(2000, 2500), damp=0.05, hard=1.0)
        click += 0.5 * punch(0.06, rng, f=2500, decay=0.002)
        put(x, click, tt, 0.35 + 0.2 * rng.random())
        tt += 0.022 + 0.0035 * k
        k += 1
    clunk = material("metal", 0.25, rng, f0=420, damp=0.25, hard=1.2) + punch(0.25, rng, f=500, decay=0.025)
    clunk += 0.8 * thump(0.25, 110, 60, 0.04, 0.012)
    put(x, clunk, 0.42, 1.0)
    groan = creak(0.38, rng, curve(0.38, [(0, 40), (0.38, 60)]), [(500, 8, 1.0), (1150, 9, 0.5)], jitter=0.4)
    put(x, norm(groan) * bump(ns(0.38), 0.5, 2.0), 0.0, 0.15)
    return verb(x, "room", 0.12, tail=0.2)


@sfx("cage_open", "world", "Iron cage gate: squealing hinge creak then a clank.", level=-14, jitter=0.03,
     dist=(2.0, 40), limit=True)
def _cage_open(rng, v):
    dur = 1.2
    n = ns(dur)
    x = np.zeros(n)
    sq = creak(0.85, rng, curve(0.85, [(0, 60), (0.2, 150), (0.5, 185), (0.85, 110)], "exp"),
               [(640, 14, 1.0), (1430, 16, 0.7), (2600, 18, 0.45), (3900, 18, 0.25)], jitter=0.25, irregular=0.3)
    sq *= curve(0.85, [(0, 0), (0.08, 0.8), (0.45, 1.0), (0.85, 0)], "cos")
    put(x, norm(sq), 0.0, 0.8)
    clank = material("metal", 0.6, rng, f0=310, damp=0.6, hard=1.1) + 0.7 * material("metal", 0.6, rng, f0=470, damp=0.5)
    clank += 0.6 * punch(0.6, rng, f=900, decay=0.015)
    put(x, clank, 0.88, 1.0)
    return fit(verb(x, "stone", 0.14), 1.45, 0.2)


@sfx("bridge_fall", "world", "Rope drawbridge: straining creak, then it crashes down.", level=-10, jitter=0.02,
     dist=(4.0, 70), limit=True)
def _bridge_fall(rng, v):
    dur = 1.6
    n = ns(dur)
    x = np.zeros(n)
    cr = creak(0.6, rng, curve(0.6, [(0, 14), (0.3, 30), (0.6, 46)], "exp"),
               [(150, 3, 1.0), (390, 4, 0.8), (880, 5, 0.5), (1700, 6, 0.2)], jitter=0.4, irregular=0.5)
    cr *= curve(0.6, [(0, 0), (0.1, 0.7), (0.5, 1.0), (0.6, 0.6)], "cos")
    put(x, norm(cr), 0.0, 0.7)
    put(x, whoosh(0.3, rng, 120, 600, peak=0.7, bw=1.6, sharp=2.0, slope=-3.5), 0.4, 0.4)
    crash = material("box", 1.0, rng, f0=88, hard=1.3) + punch(1.0, rng, f=500, decay=0.05)
    crash += thump(1.0, 75, 36, 0.12, 0.03, rng, noise=0.6, noise_lp=500)
    crash += 0.6 * debris(1.0, rng, 0.02, rate=60, decay_rate=0.3, kinds=("wood", "box"), f_range=(200, 1200), size=0.9)
    put(x, crash, 0.62, 1.0)
    return fit(verb(sat(norm(x), 1.7), "arena", 0.18), 1.85, 0.25)


@sfx("rope_burn", "world", "Burning rope fizzles, then snaps.", level=-14, jitter=0.04, dist=(2.0, 35), limit=True)
def _rope_burn(rng, v):
    dur = 0.8
    n = ns(dur)
    fizz = crackle(0.58, rng, rate=260, band=(1800, 6500), alpha=2.2, cap=4) * curve(0.58, [(0, 0.2), (0.5, 1.0), (0.58, 0.6)])
    hiss = bp(white(ns(0.58), rng), 2500, 6500, 2) * curve(0.58, [(0, 0), (0.4, 1.0), (0.58, 0.7)], "cos")
    x = np.zeros(n)
    put(x, norm(lp(fizz, 7500, 4)) * 0.6 + 0.25 * norm(lp(hiss, 7500, 4)), 0.0)
    snap = bp(white(ns(0.2), rng), 900, 7000, 2) * np.exp(-tl(ns(0.2)) / 0.004) + 0.8 * punch(0.2, rng, f=900, decay=0.01)
    put(x, snap, 0.58, 1.0)
    put(x, whoosh(0.2, rng, 400, 2500, peak=0.25, bw=1.2, sharp=2.5, slope=-2.0), 0.585, 0.5)
    return verb(x, "room", 0.1, tail=0.12)


@sfx("acid_sizzle", "world", "Acid pool hissing and bubbling (plague nurse acid).", level=-18, jitter=0.06,
     dist=(1.5, 20))
def _acid_sizzle(rng, v):
    dur = 1.0
    n = ns(dur)
    hiss = bp(white(n, rng), 2300, 6500, 2) * np.clip(1 + 0.7 * smooth_noise(n, rng, 30), 0, None)
    bub = np.zeros(n)
    for tt in impulses(dur, rng, 45):
        put(bub, bubble(rng.uniform(400, 1600), rng.uniform(0.02, 0.05), rise=rng.uniform(0.4, 1.0)), tt,
            rng.uniform(0.2, 1.0))
    cr = crackle(dur, rng, rate=150, band=(2500, 6500), alpha=2.5, cap=3)
    env = curve(dur, [(0, 0), (0.05, 1.0), (0.6, 0.8), (1.0, 0)], "cos")
    x = (0.6 * norm(lp(hiss, 7500, 4)) + 0.5 * norm(bub) + 0.3 * norm(lp(cr, 7500, 4))) * env
    return verb(x, "small", 0.07, tail=0.08)


# ---------------------------------------------------------------------------
# World: magic, music stingers, pickups, UI
# ---------------------------------------------------------------------------


@sfx("heal", "world", "Plague nurse heal: sparkly rising chime.", level=-16, jitter=0.03, dist=(1.5, 30))
def _heal(rng, v):
    dur = 0.8
    n = ns(dur)
    t = tl(n)
    x = np.zeros(n)
    for i, nm in enumerate(["D5", "F#5", "A5", "D6", "F#6"]):
        put(x, glass_ping(0.6, rng, hz(nm), decay=0.5, bright=0.6) + 0.3 * fm_bell(0.6, rng, hz(nm), 2.0, 1.2, 0.4, 0.05),
            i * 0.065, 0.7 + 0.06 * i)
    sp = np.zeros(n)
    for tt in impulses(dur * 0.8, rng, 30):
        put(sp, glass_ping(0.12, rng, rng.uniform(3000, 6000), decay=0.1, bright=0.2), tt, rng.uniform(0.1, 0.4))
    pad = (np.sin(TAU * hz("D4") * t) + 0.6 * np.sin(TAU * hz("F#4") * t) + 0.5 * np.sin(TAU * hz("A4") * t))
    pad *= curve(dur, [(0, 0), (0.25, 0.5), (0.8, 0)], "cos")
    x = norm(x) + 0.25 * norm(sp) + 0.2 * pad
    return fit(verb(lp(x, 7500, 2), "magic", 0.3), 1.05, 0.25)


@sfx("revive", "world", "Revive: rising choral swell that blooms into light.", level=-14, jitter=0.0, dist=(2.0, 40))
def _revive(rng, v):
    dur = 1.6
    n = ns(dur)
    rise = 2 ** (curve(dur, [(0, -2.0), (1.1, 0.0), (1.6, 0.0)], "cos") / 12)
    ch = choir(dur, rng, [hz("D3") * rise, hz("A3") * rise, hz("D4") * rise, hz("F#4") * rise], 1.0, "a", per_note=2)
    ch *= curve(dur, [(0, 0), (0.9, 0.7), (1.15, 1.0), (1.6, 0.0)], "cos")
    bl = np.zeros(n)
    for i, nm in enumerate(["D5", "A5", "D6"]):
        put(bl, glass_ping(0.5, rng, hz(nm), decay=0.5, bright=0.5), 1.1 + 0.05 * i, 0.6)
    sh = whoosh(dur, rng, 1500, 6000, peak=0.7, bw=0.8, sharp=2.0, slope=-1.0)
    x = norm(ch) + 0.3 * norm(bl) + 0.12 * norm(sh)
    return fit(verb(x, "hall", 0.35), 1.95, 0.3)


@sfx("alarm_bell", "world", "Vault alarm: church bell clanging repeatedly.", level=-11, jitter=0.0, dist=(5.0, 80))
def _alarm_bell(rng, v):
    dur = 2.2
    n = ns(dur)
    x = np.zeros(n + ns(1.0))
    for i, tt in enumerate([0.0, 0.5, 1.0, 1.5]):
        put(x, bell(2.2, rng, 470.0 * (1 + 0.003 * (i % 2)), strike=1.0, decay=0.7, bright=0.9), tt, [1.0, 0.85, 0.95, 0.8][i])
    x = lp(x, 7500, 2)
    return fit(verb(x, "stone", 0.2), 2.6, 0.4)


@sfx("wave_horn", "world", "Low war horn announcing an incoming wave.", level=-11, jitter=0.0, dist=(10.0, 120))
def _wave_horn(rng, v):
    dur = 1.6
    amp = [(0, 0), (0.14, 0.75), (0.32, 1.0), (1.15, 0.95), (1.5, 0)]
    h1 = brass(dur, rng, hz("A2"), amp, bright=0.75, vib=0.005, scoop=2.0, breath=0.04)
    h2 = brass(dur, rng, hz("D2"), amp, bright=0.6, vib=0.004, scoop=1.5, breath=0.03)
    t = tl(len(h1))
    x = norm(h1) + 0.6 * norm(h2)
    x *= 1 + 0.06 * np.sin(TAU * 27 * t)
    x = eq(x, "peak", 550, 1.0, 3)
    return fit(verb(sat(norm(x), 1.3), "arena", 0.3), 2.0, 0.35)


def _brass_notes(x, rng, notes, bright=1.0, gain=1.0):
    for st, ln, nm, vel in notes:
        a = [(0, 0), (0.025, vel), (ln * 0.6, vel * 0.85), (ln, vel * 0.7), (ln + 0.08, 0)]
        put(x, brass(ln + 0.1, rng, hz(nm), a, bright=bright, vib=0.004 if ln > 0.3 else 0.0, scoop=0.6), st, gain)


@sfx("victory", "world", "Short triumphant fanfare (medieval brass and strings) for winning a demo.", level=-10,
     jitter=0.0, dist=(5.0, 100))
def _victory(rng, v):
    dur = 2.2
    x = np.zeros(ns(dur + 0.8))
    lead = [(0.00, 0.11, "A4", 0.8), (0.12, 0.11, "A4", 0.8), (0.24, 0.11, "A4", 0.85), (0.36, 0.38, "D5", 1.0),
            (0.78, 0.1, "A4", 0.8), (0.90, 0.1, "D5", 0.85), (1.02, 1.05, "F#5", 1.0)]
    low = [(0.36, 0.38, "A4", 0.8), (0.78, 0.1, "F#4", 0.7), (0.90, 0.1, "A4", 0.7), (1.02, 1.05, "D5", 0.85)]
    bass = [(0.36, 0.38, "D3", 0.8), (1.02, 1.05, "D3", 0.85)]
    _brass_notes(x, rng, lead, bright=1.0, gain=1.0)
    _brass_notes(x, rng, low, bright=0.8, gain=0.6)
    _brass_notes(x, rng, bass, bright=0.6, gain=0.55)
    st = strings(1.2, rng, [hz("D4"), hz("F#4"), hz("A4"), hz("D5")], [(0, 0), (0.15, 1.0), (0.9, 0.8), (1.2, 0)],
                 voices=3, bright=3000)
    put(x, st, 1.02, 1.6)
    for tt, g in [(0.36, 0.9), (1.02, 1.0)]:
        put(x, drum(0.8, rng, 120, 73.4, 0.3, skin=0.5), tt, g * 0.9)
    return fit(verb(x, "hall", 0.25), 2.5, 0.35)


@sfx("defeat", "world", "Low doom chord for losing a demo.", level=-12, jitter=0.0, dist=(5.0, 100))
def _defeat(rng, v):
    dur = 2.0
    x = np.zeros(ns(dur + 0.8))
    c1 = [hz("Bb2"), hz("F3"), hz("Bb3"), hz("D4")]
    a1 = strings(0.55, rng, c1, [(0, 0), (0.04, 1), (0.45, 0.7), (0.55, 0)], voices=3, bright=1800)
    for f in c1[:2]:
        a1 += 0.4 * brass(0.55, rng, f, [(0, 0), (0.04, 0.8), (0.45, 0.6), (0.55, 0)], bright=0.5)
    put(x, a1, 0.0, 0.8)
    t = tl(ns(1.45))
    sag = 2 ** (-0.3 * (t / 1.45) / 12)
    c2 = [hz("D2"), hz("A2"), hz("D3"), hz("F3"), hz("A3")]
    a2 = strings(1.45, rng, [f * sag for f in c2], [(0, 0), (0.05, 1), (0.8, 0.6), (1.45, 0)], voices=3, bright=1500)
    for f in c2[:3]:
        a2 += 0.35 * brass(1.45, rng, f * sag, [(0, 0), (0.05, 0.8), (0.8, 0.5), (1.45, 0)], bright=0.4)
    put(x, a2, 0.55, 1.0)
    put(x, drum(1.0, rng, 95, 55, 0.5, skin=0.4, bright=800), 0.55, 0.9)
    return fit(verb(x, "hall", 0.28), 2.4, 0.4)


@sfx("rite_progress", "world", "Soft rising chime when the Rite of Return advances.", level=-18, jitter=0.0,
     dist=(2.0, 40))
def _rite_progress(rng, v):
    dur = 0.6
    n = ns(dur)
    x = np.zeros(n)
    put(x, glass_ping(0.6, rng, hz("D5"), decay=0.5, bright=0.5), 0.0, 0.8)
    put(x, glass_ping(0.5, rng, hz("A5"), decay=0.5, bright=0.5), 0.12, 0.9)
    pad = choir(dur, rng, [hz("A4")], [(0, 0), (0.25, 1.0), (0.6, 0)], "u", per_note=2)
    x = norm(x) + 0.25 * norm(pad)
    return fit(verb(x, "magic", 0.3), 0.85, 0.2)


@sfx("rite_complete", "world", "Rite of Return complete: big radiant chord.", level=-10, jitter=0.0, dist=(5.0, 100))
def _rite_complete(rng, v):
    dur = 2.0
    n = ns(dur)
    ch = choir(dur, rng, [hz("D3"), hz("A3"), hz("D4"), hz("F#4"), hz("A4")], [(0, 0), (0.12, 1.0), (1.3, 0.8), (2.0, 0)],
               "a", per_note=2)
    st = strings(dur, rng, [hz("D3"), hz("A3"), hz("F#4"), hz("D5")], [(0, 0), (0.1, 1.0), (1.4, 0.7), (2.0, 0)],
                 voices=3, bright=3500)
    bl = np.zeros(n)
    for i, nm in enumerate(["D5", "F#5", "A5", "D6", "E6", "A6"]):
        put(bl, glass_ping(0.9, rng, hz(nm), decay=0.7, bright=0.5), 0.03 + 0.06 * i, 0.6)
    boom = drum(1.2, rng, 110, 73.4, 0.45, skin=0.4)
    sh = whoosh(dur, rng, 1200, 5000, peak=0.15, bw=0.9, sharp=2.0, tail=3.0, slope=-1.0)
    x = norm(ch) + 0.6 * norm(st) + 0.35 * norm(bl) + 0.15 * norm(sh)
    put(x, boom, 0.0, 0.6)
    return fit(verb(x, "hall", 0.3), 2.4, 0.4)


@sfx("key_pickup", "world", "Picking up a key: small metal jingle.", level=-18, jitter=0.04, dist=(1.0, 15))
def _key_pickup(rng, v):
    dur = 0.4
    n = ns(dur)
    x = np.zeros(n)
    for i, tt in enumerate([0.0, 0.035, 0.07, 0.12, 0.16]):
        put(x, material("metal", 0.3, rng, f0=rng.uniform(1700, 2700), damp=rng.uniform(0.25, 0.4), hard=0.8), tt,
            [1.0, 0.7, 0.8, 0.5, 0.4][i])
    x += 0.25 * rustle(dur, rng, density=200, band=(1500, 5000), env=bump(n, 0.15, 2.0))
    return verb(lp(x, 8000, 4), "small", 0.08, tail=0.1)


@sfx("relic_pickup", "world", "Picking up a relic: magical shimmer.", level=-15, jitter=0.0, dist=(1.5, 25))
def _relic_pickup(rng, v):
    dur = 0.8
    n = ns(dur)
    sh = whoosh(dur, rng, 900, 5500, peak=0.45, bw=0.9, sharp=2.0, slope=-1.0)
    arp = np.zeros(n)
    for i, nm in enumerate(["A5", "D6", "E6", "A6"]):
        put(arp, glass_ping(0.6, rng, hz(nm), decay=0.45, bright=0.5), 0.08 + 0.06 * i, 0.8)
    ch = choir(dur, rng, [hz("D5"), hz("A4")], [(0, 0), (0.3, 1.0), (0.8, 0)], "a", per_note=2)
    x = 0.3 * norm(sh) + norm(arp) + 0.3 * norm(ch)
    return fit(verb(lp(x, 7500, 2), "magic", 0.3), 1.05, 0.25)


@sfx("pebble_land", "world", "Thrown pebble lands: small stone clack and a short roll (distraction).", variants=2,
     level=-20, jitter=0.08, dist=(1.5, 30))
def _pebble_land(rng, v):
    dur = 0.4
    n = ns(dur)
    x = np.zeros(n)
    bounces = [(0.0, 1.0), (0.07, 0.45), (0.115, 0.25)] if v == 0 else [(0.0, 1.0), (0.055, 0.5), (0.095, 0.3), (0.12, 0.15)]
    for tt, g in bounces:
        put(x, material("stone", 0.08, rng, f0=rng.uniform(1800, 2600), hard=1.0, damp=0.8), tt, g)
    roll = crackle(0.25, rng, rate=180, band=(1500, 5000), alpha=3.0, cap=2.0) * curve(0.25, [(0, 1), (0.25, 0)])
    put(x, roll, 0.13, 0.12)
    return verb(x, "room", 0.1, tail=0.1)


@sfx("ui_click", "ui", "Soft UI click (menus, buttons).", level=-22, jitter=0.03, dist=(1.0, 10))
def _ui_click(rng, v):
    dur = 0.08
    n = ns(dur)
    t = tl(n)
    x = material("wood", dur, rng, f0=1250, hard=0.8, damp=0.25)
    x += 0.4 * np.sin(TAU * 1900 * t) * np.exp(-t / 0.01)
    return lp(x, 6000, 2)


# ---------------------------------------------------------------------------
# Loop helpers: everything below is periodic by construction (integer cycles
# per loop, FFT-periodic noise, wrap-around events, circular reverb).
# ---------------------------------------------------------------------------


def vowel_gain(f, vowel, scale=1.0, bw=1.0):
    """Magnitude response of the Klatt formant cascade at frequencies f."""
    F, B = VOWELS[vowel]
    z = np.exp(-1j * TAU * np.asarray(f, float) / SR)
    H = np.ones(np.shape(f), dtype=complex)
    for Fk, Bk in zip(F, B):
        Fk = Fk * scale
        Bk = Bk * bw * (0.6 + 0.4 * scale)
        r = math.exp(-math.pi * Bk / SR)
        Bc = 2 * r * math.cos(TAU * Fk / SR)
        Cc = -r * r
        H = H * (1 - Bc - Cc) / (1 - Bc * z - Cc * z * z)
    return np.abs(H)


def loop_choir(T, rng, notes, vowel="o", voices=3, vib_rate=5.0, vib_depth=0.006, breath=0.12, tilt=-1.6,
               swell=0.18, scale=1.0, maxf=6000.0):
    """Seamless additive choir pad: every partial, vibrato and swell has whole cycles per loop."""
    n = ns(T)
    t = tl(n)
    y = np.zeros(n)
    for f in notes:
        sc = scale * (1.12 if f > 330 else 1.0)
        for _ in range(voices):
            f0 = snapf(f * (1 + rng.uniform(-0.004, 0.004)), T)
            vr = snapf(vib_rate * rng.uniform(0.88, 1.12), T)
            dep = vib_depth * rng.uniform(0.7, 1.3)
            pv = rng.uniform(0, TAU)
            phase = f0 * t - f0 * dep * (np.cos(TAU * vr * t + pv) - math.cos(pv)) / (TAU * vr)
            hs = np.arange(1, int(maxf // f0) + 1)
            amps = vowel_gain(hs * f0, vowel, sc) * hs.astype(float) ** tilt
            v = np.zeros(n)
            for h, a in zip(hs, amps):
                v += a * np.sin(TAU * h * phase + rng.uniform(0, TAU))
            k = int(rng.integers(1, 3))
            v *= 1 + swell * np.sin(TAU * (k / T) * t + rng.uniform(0, TAU))
            y += v / (np.sqrt(np.mean(v ** 2)) + 1e-12)
    X = np.fft.rfft(white(n, rng))
    fr = np.fft.rfftfreq(n, 1.0 / SR)
    X *= vowel_gain(fr, vowel, scale) * np.where(fr > 300, (np.maximum(fr, 1.0) / 300.0) ** -0.5, 1.0) * (fr > 120)
    br = np.fft.irfft(X, n)
    br /= np.sqrt(np.mean(br ** 2)) + 1e-12
    y = y / (len(notes) * voices) ** 0.5 + breath * br
    return y


def gust(T, rng, ks=(1, 2, 3, 5), lo=0.3, hi=1.0):
    """Periodic gust envelope (whole cycles per loop) scaled to [lo, hi]."""
    t = tl(ns(T))
    e = np.zeros(len(t))
    for k in ks:
        e += rng.uniform(0.4, 1.0) / k ** 0.5 * np.sin(TAU * k * t / T + rng.uniform(0, TAU))
    e = (e - e.min()) / (e.max() - e.min() + 1e-12)
    return lo + (hi - lo) * e ** 1.3


def tv_loop(x, kind, f, q=0.707, reps=3):
    """Time-varying biquad on a loop with a periodic control curve (seamless)."""
    n = len(x)
    y = tv_biquad(np.tile(x, reps), kind, np.tile(np.broadcast_to(f, (n,)), reps),
                  np.tile(np.broadcast_to(q, (n,)), reps))
    return y[(reps - 1) * n:]


def loop_noise(T, rng, slope, lo=None, hi=None):
    x = colored(ns(T), rng, slope)
    if lo and hi:
        x = periodic(x, lambda s: bp(s, lo, hi, 2))
    elif hi:
        x = periodic(x, lambda s: lp(s, hi, 2))
    elif lo:
        x = periodic(x, lambda s: hp(s, lo, 2))
    return x / (np.sqrt(np.mean(x ** 2)) + 1e-12)


@sfx("chant_pad", "world", "Soft choir pad held while the player chants in the Rite of Return; loop=true. "
     "Kept soft and low so it does not mask the player's voice.", loop=True, level=-28, jitter=0.0, dist=(2.0, 40))
def _chant_pad(rng, v):
    T = 4.0
    x = loop_choir(T, rng, [hz("D3"), hz("A3"), hz("D4")], "o", voices=3, vib_rate=4.8, breath=0.1, maxf=4500)
    x = periodic(x, lambda s: lp(s, 2500, 2))
    return verb_loop(x, "chapel", 0.35)


# ---------------------------------------------------------------------------
# Ambience beds (16–24 s seamless loops, quiet)
# ---------------------------------------------------------------------------


def distant_creak(rng, dur=0.7, wood=True):
    if wood:
        modes = [(180, 4, 1.0), (420, 5, 0.7), (900, 6, 0.4)]
        rate = curve(dur, [(0, 20), (dur * 0.5, 45), (dur, 28)], "exp")
    else:
        modes = [(700, 14, 1.0), (1500, 16, 0.6), (2700, 18, 0.3)]
        rate = curve(dur, [(0, 60), (dur * 0.5, 140), (dur, 90)], "exp")
    c = creak(dur, rng, rate, modes, jitter=0.35, irregular=0.4) * bump(ns(dur), 0.45, 1.8)
    return lp(rms_n(c), 2200 if wood else 3500, 2)


@sfx("amb_pit", "ambience", "Pit Fighter arena bed: open ruined arena, wind gusts, distant fire crackle; loop=true.",
     loop=True, level=-31, jitter=0.0, dist=(1.0, 500))
def _amb_pit(rng, v):
    T = 20.0
    n = ns(T)
    g = gust(T, rng, (1, 2, 3, 5), 0.25, 1.0)
    low = loop_noise(T, rng, -3, 120, 600) * g
    high = loop_noise(T, rng, -2, 500, 1800) * g ** 2.5
    whistle = tv_loop(loop_noise(T, rng, -2), "bp", 300 + 350 * g, 9.0) * g ** 2
    fire = crackle(T, rng, rate=16, band=(900, 3200), alpha=2.6, wrap=True, cap=3)
    fire = periodic(fire, lambda s_: lp(s_, 3000, 4))
    roar = loop_noise(T, rng, -5, None, 280) * np.clip(1 + 0.4 * smooth_noise(n, rng, 10.0), 0.2, None)
    x = rms_n(low) + 0.35 * rms_n(high) + 0.12 * rms_n(whistle) + 0.3 * rms_n(roar)
    x += 4.5 * fire / (np.max(np.abs(fire)) + 1e-12)
    return verb_loop(x, "arena", 0.3)


@sfx("amb_courtyard", "ambience", "Puppet War courtyard bed: light outdoor wind, rustling, distant creaks; loop=true.",
     loop=True, level=-32, jitter=0.0, dist=(1.0, 500))
def _amb_courtyard(rng, v):
    T = 20.0
    n = ns(T)
    g = gust(T, rng, (1, 2, 4), 0.35, 1.0)
    wind = loop_noise(T, rng, -3, 180, 900) * g
    leaves = loop_noise(T, rng, -1, 2000, 6000) * g ** 3 * np.clip(1 + 0.6 * smooth_noise(n, rng, 8.0), 0, None)
    x = rms_n(wind) + 0.18 * rms_n(leaves)
    ev = np.zeros(n)
    for tt, wood in [(rng.uniform(1, 5), True), (rng.uniform(8, 12), True), (rng.uniform(14, 18), False)]:
        put_wrap(ev, distant_creak(rng, rng.uniform(0.6, 0.9), wood), tt, rng.uniform(0.25, 0.4))
    put_wrap(ev, lp(material("box", 0.3, rng, f0=150, damp=0.8), 1200, 2), rng.uniform(5, 8), 0.2)
    x = x + ev
    return verb_loop(x, "outdoor", 0.3)


@sfx("amb_chasm", "ambience", "Ledge Key chasm bed: deep, howling wind rising from the abyss; loop=true.", loop=True,
     level=-30, jitter=0.0, dist=(1.0, 500))
def _amb_chasm(rng, v):
    T = 24.0
    n = ns(T)
    t = tl(n)
    g = gust(T, rng, (1, 2, 3), 0.3, 1.0)
    howl = np.zeros(n)
    for k, (base, span, q) in enumerate([(190, 110, 12), (290, 160, 14), (420, 200, 10)]):
        drift = 0.5 + 0.5 * np.sin(TAU * (k + 1) * t / T + rng.uniform(0, TAU))
        howl += tv_loop(loop_noise(T, rng, -2), "bp", base + span * drift * g, q) * (0.5 + 0.5 * drift)
    wind = loop_noise(T, rng, -3, 90, 700) * g
    rum = loop_noise(T, rng, -6, None, 70)
    x = 0.5 * rms_n(howl * g ** 1.5) + rms_n(wind) + 0.45 * rms_n(rum)
    ev = np.zeros(n)
    for tt in rng.uniform(0, T, 2):
        for j, dt in enumerate([0.0, 0.18, 0.31]):
            put_wrap(ev, lp(material("stone", 0.1, rng, f0=rng.uniform(1500, 2200)), 2500, 2), tt + dt, 0.12 / (j + 1))
    return verb_loop(x + ev, "chasm", 0.3)


@sfx("amb_chapel", "ambience", "Rite of Return chapel bed: low distant choir drone and dripping water; loop=true.",
     loop=True, level=-32, jitter=0.0, dist=(1.0, 500))
def _amb_chapel(rng, v):
    T = 24.0
    n = ns(T)
    ch = loop_choir(T, rng, [hz("D3"), hz("A3")], "u", voices=3, vib_rate=4.5, breath=0.15, swell=0.35, maxf=3000)
    ch = periodic(ch, lambda s: lp(s, 1400, 2))
    room = loop_noise(T, rng, -4, 60, 600)
    drips = np.zeros(n)
    for tt in rng.uniform(0, T, 6):
        put_wrap(drips, drip(rng.uniform(1100, 1900), rng), tt, rng.uniform(0.4, 1.0))
    x = rms_n(ch) + 0.25 * rms_n(room)
    x = verb_loop(x, "chapel", 0.4)
    d = verb_loop(norm(drips), "chapel", 0.5, dry=0.6)
    return x + 0.55 * d


@sfx("amb_vault", "ambience", "Eye Contact Heist vault bed: tense low drone, drips, faint metal creaks; loop=true.",
     loop=True, level=-31, jitter=0.0, dist=(1.0, 500))
def _amb_vault(rng, v):
    T = 20.0
    n = ns(T)
    t = tl(n)
    sw = 0.6 + 0.4 * np.sin(TAU * 2 * t / T - np.pi / 2)
    dr = np.sin(TAU * snapf(73.42, T) * t) + 0.8 * np.sin(TAU * snapf(77.78, T) * t + 1.0)
    dr += 0.5 * np.sin(TAU * snapf(55.0, T) * t + 2.0) * (0.6 + 0.4 * np.sin(TAU * 4 * t / T))
    dr += 0.25 * np.sin(TAU * snapf(146.83, T) * t) * (0.5 + 0.5 * np.sin(TAU * 3 * t / T))
    hi = (np.sin(TAU * snapf(1174.7, T) * t) + np.sin(TAU * snapf(1244.5, T) * t)) * (0.5 + 0.5 * np.sin(TAU * 1 * t / T))
    dr = np.tanh(0.8 * dr) * sw
    room = loop_noise(T, rng, -4, 50, 700)
    ev = np.zeros(n)
    for tt in rng.uniform(0, T, 4):
        put_wrap(ev, drip(rng.uniform(1200, 2000), rng), tt, rng.uniform(0.4, 0.8))
    for tt in (rng.uniform(3, 7), rng.uniform(12, 16)):
        put_wrap(ev, distant_creak(rng, rng.uniform(0.5, 0.8), wood=False), tt, 0.35)
    x = rms_n(dr) + 0.02 * rms_n(hi) + 0.3 * rms_n(room)
    return verb_loop(x, "stone", 0.25) + 0.6 * verb_loop(norm(ev), "stone", 0.5, dry=0.6)


@sfx("amb_room", "ambience", "Neutral stone room tone (any indoor space); loop=true.", loop=True, level=-35,
     jitter=0.0, dist=(1.0, 500))
def _amb_room(rng, v):
    T = 16.0
    g = gust(T, rng, (1, 2, 3), 0.85, 1.0)
    x = (loop_noise(T, rng, -5, 40, 900) + 0.25 * loop_noise(T, rng, -2, 300, 2500)) * g
    return verb_loop(x, "stone", 0.25)



# ===========================================================================
# Rendering, encoding and the manifest
# ===========================================================================


def trim_tail(x, floor_db=-66.0, keep=0.01):
    a = np.abs(x)
    thr = a.max() * db(floor_db)
    idx = np.nonzero(a > thr)[0]
    if len(idx) == 0:
        return x
    end = min(len(x), idx[-1] + ns(keep))
    return x[:end]


def peak_limit(x, reduce_db, release=0.025, hold=0.004):
    """Look-ahead peak limiter: pull the highest peaks down by reduce_db with a smooth release."""
    if reduce_db <= 0.05:
        return x
    a = np.abs(x)
    c = a.max() * db(-reduce_db)
    req = np.minimum(1.0, c / np.maximum(a, 1e-12))
    g = minimum_filter1d(req, size=max(1, ns(hold)))
    k = 1.0 - math.exp(-1.0 / (release * SR))
    out = np.empty_like(g)
    y = 1.0
    for i, gi in enumerate(g.tolist()):
        y = y + (1.0 - y) * k
        if gi < y:
            y = gi
        out[i] = y
    out = signal.filtfilt([0.5, 0.5], [1.0], out)  # remove the sharpest gain corners
    return x * np.minimum(out, 1.0)


PLR_MAX = 13.0  # dB: transient sounds with a higher peak-to-loudness ratio get gently limited
MIX_OFFSET = -3.0  # dB applied to every target level (headroom so few sounds sit at volume 1.0)


def finish_oneshot(x, limit=False):
    x = np.nan_to_num(np.asarray(x, float))
    x = hp0(x, 18.0, 2)
    x = lp0(x, 13000.0, 2)
    x = trim_tail(x)
    x = fades(x, 0.003, 0.008)
    x = norm(x, db(-1.0))
    plr = -1.0 - loudness(x)
    if limit and plr > PLR_MAX:
        x = norm(peak_limit(x, min(8.0, plr - PLR_MAX)), db(-1.0))
    return x


def finish_loop(x, bake_lufs):
    """Loops stay periodic: no trimming/fades; level baked to a calm loudness."""
    x = np.nan_to_num(np.asarray(x, float))
    x = x - x.mean()
    x = periodic(x, lambda s: hp(s, 18.0, 2))
    x = periodic(x, lambda s: lp(s, 15000.0, 2))
    x = x - x.mean()
    lt = loudness(np.tile(x, 2))
    x = x * db(bake_lufs - lt)
    pk = np.max(np.abs(x))
    if pk > db(-1.0):
        x *= db(-1.0) / pk
    return x


def _band_frames(x, nfft=2048, hop=512):
    """Log energies in ~1/3-octave bands for overlapping frames."""
    edges = 40.0 * 2 ** (np.arange(0, 28) / 3.0)
    edges = edges[edges < SR / 2.2]
    f = np.fft.rfftfreq(nfft, 1.0 / SR)
    w = np.hanning(nfft)
    starts = np.arange(0, len(x) - nfft, hop)
    out = np.empty((len(starts), len(edges) - 1))
    for i, st in enumerate(starts):
        P = np.abs(np.fft.rfft(x[st:st + nfft] * w)) ** 2
        for b in range(len(edges) - 1):
            m = (f >= edges[b]) & (f < edges[b + 1])
            out[i, b] = 10 * np.log10(P[m].sum() + 1e-12)
    return starts, out


def loop_seam_report(x):
    """Seam check for a loop.

    Returns (jump, flux_ratio): jump = |x[0]-x[-1]| relative to the 99th percentile of
    sample-to-sample steps (<= ~1 means the wrap is as smooth as the signal itself);
    flux_ratio = band-spectrum change across the wrap relative to the 99th percentile of
    changes between frames inside the loop (<= ~1 means no spectral seam)."""
    d = np.abs(np.diff(x))
    jump = abs(x[0] - x[-1]) / (np.percentile(d, 99) + 1e-12)
    n = len(x)
    xx = np.concatenate([x, x])
    starts, B = _band_frames(xx)
    act = B.max(axis=0) > B.max() - 60  # ignore empty bands
    flux = np.sqrt(np.mean(np.diff(B[:, act], axis=0) ** 2, axis=1))
    nfft = 2048
    straddle = np.array([(st < n < st + nfft) or (st + 512 < n < st + 512 + nfft) for st in starts[:-1]])
    inner = flux[~straddle]
    ratio = flux[straddle].max() / (np.percentile(inner, 99) + 1e-9)
    return float(jump), float(ratio)


def encode_ogg(x, path, q):
    cmd = ["ffmpeg", "-v", "error", "-y", "-f", "f32le", "-ar", str(SR), "-ac", "1", "-i", "pipe:0",
           "-c:a", "libvorbis", "-q:a", str(q), "-fflags", "+bitexact", "-flags:a", "+bitexact",
           "-map_metadata", "-1", path]
    subprocess.run(cmd, input=x.astype(np.float32).tobytes(), check=True, capture_output=True)


def decode_ogg(path):
    try:  # libsndfile uses the reference libvorbis decoder (sample-exact length)
        import soundfile
        y, _ = soundfile.read(path, dtype="float64")
        return y if y.ndim == 1 else y.mean(axis=1)
    except ImportError:
        pass
    p = subprocess.run(["ffmpeg", "-v", "error", "-i", path, "-f", "f32le", "-ac", "1", "-ar", str(SR), "pipe:1"],
                       check=True, capture_output=True)
    return np.frombuffer(p.stdout, dtype=np.float32).astype(np.float64)


BAKE_LOOP = {"world": -20.0, "brainwash": -24.0, "ambience": -26.0}


def render_one(args):
    sid, v, out_dir = args
    e = _BY_ID[sid]
    name = file_names(e)[v]
    rng = np.random.default_rng(seed_for(name))
    x = e["fn"](rng, v)
    if e["loop"]:
        x = finish_loop(x, BAKE_LOOP.get(e["category"], -22.0))
    else:
        x = finish_oneshot(x, e["limit"])
    if not np.all(np.isfinite(x)):
        raise RuntimeError(f"{name}: non-finite samples")
    q = e["q"]
    path = os.path.join(out_dir, name + ".ogg")
    for _ in range(8):
        encode_ogg(x, path, q)
        y = decode_ogg(path)
        over = 20 * math.log10(np.max(np.abs(y)) + 1e-12) + 0.5
        if over > 0:  # lossy coding overshot the -1 dBFS peak: trim and re-encode
            x = x * db(-over - 0.1)
            continue
        if e["loop"] and len(y) == len(x) and q < 8:
            jump, flux = loop_seam_report(y)
            if jump > 1.2 or flux > 1.2:  # codec error at the wrap point: spend more bits
                q += 1
                continue
        break
    info = dict(name=name, id=sid, q=q, dur=len(x) / SR, peak_db=20 * math.log10(np.max(np.abs(x)) + 1e-12),
                lufs=loudness(np.tile(x, 2) if e["loop"] else x), dc=float(np.mean(x)), bytes=os.path.getsize(path))
    if e["loop"]:
        info["seam_jump"], info["seam_flux"] = loop_seam_report(x)
    info["decoded_len_diff"] = len(y) - len(x)
    info["decoded_peak_db"] = 20 * math.log10(np.max(np.abs(y)) + 1e-12)
    if e["loop"] and len(y) == len(x):
        info["decoded_seam_jump"], info["decoded_seam_flux"] = loop_seam_report(y)
    return info


def write_manifest(out_dir, infos):
    by_id = {}
    for inf in infos:
        by_id.setdefault(inf["id"], []).append(inf)
    sounds = []
    for e in REGISTRY:
        lv = [i["lufs"] for i in by_id[e["id"]]]
        meas = 10 * math.log10(np.mean([10 ** (v / 10) for v in lv]))
        vol = min(1.0, max(0.03, db(e["level"] + MIX_OFFSET - meas)))
        sounds.append({
            "id": e["id"],
            "files": file_names(e),
            "loop": bool(e["loop"]),
            "volume": round(vol, 2),
            "pitchJitter": round(float(e["jitter"]), 3),
            "minDistance": float(e["dist"][0]),
            "maxDistance": float(e["dist"][1]),
            "category": e["category"],
            "description": e["description"],
        })
    man = {"version": 1, "sounds": sounds}
    with open(os.path.join(out_dir, "sfx.json"), "w", encoding="utf-8", newline="\n") as fh:
        json.dump(man, fh, indent=2, ensure_ascii=False)
        fh.write("\n")
    return man


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=DEFAULT_OUT, help="output folder (default: Unity Resources/Audio)")
    ap.add_argument("--only", default="", help="comma-separated sound ids to regenerate (manifest not rewritten)")
    ap.add_argument("--jobs", type=int, default=os.cpu_count() or 1)
    ap.add_argument("--list", action="store_true", help="list sound ids and exit")
    ap.add_argument("--report", default="", help="optional path for a JSON stats report")
    a = ap.parse_args(argv)
    if a.list:
        for e in REGISTRY:
            print(f"{e['id']:<22} {e['category']:<10} x{e['variants']}  {'loop' if e['loop'] else ''}")
        return 0
    ids = [s.strip() for s in a.only.split(",") if s.strip()] or [e["id"] for e in REGISTRY]
    for s in ids:
        if s not in _BY_ID:
            sys.exit(f"unknown sound id: {s}")
    os.makedirs(a.out, exist_ok=True)
    jobs = [(s, v, a.out) for s in ids for v in range(_BY_ID[s]["variants"])]
    if a.jobs > 1:
        with ProcessPoolExecutor(max_workers=a.jobs) as ex:
            infos = list(ex.map(render_one, jobs))
    else:
        infos = [render_one(j) for j in jobs]
    bad = []
    for inf in infos:
        flag = ""
        if abs(inf["decoded_len_diff"]) > 0 and _BY_ID[inf["id"]]["loop"]:
            flag += " LEN"
        if inf["decoded_peak_db"] > -0.1:
            flag += " CLIP?"
        if "seam_jump" in inf and max(inf["seam_jump"], inf["seam_flux"], inf.get("decoded_seam_jump", 0),
                                      inf.get("decoded_seam_flux", 0)) > 1.25:
            flag += " SEAM?"
        if flag:
            bad.append(inf["name"] + flag)
        print(f"{inf['name']:<24} {inf['dur']:6.2f}s pk {inf['peak_db']:6.1f} dec {inf['decoded_peak_db']:6.1f} "
              f"L {inf['lufs']:6.1f} q{inf['q']} {inf['bytes'] / 1024:7.1f} KB"
              + (f" seam jump {inf.get('decoded_seam_jump', inf['seam_jump']):.2f} "
                 f"flux {inf.get('decoded_seam_flux', inf['seam_flux']):.2f}" if "seam_jump" in inf else "")
              + flag)
    if a.report:
        with open(a.report, "w") as fh:
            json.dump(infos, fh, indent=1)
    if not a.only:
        write_manifest(a.out, infos)
        total = sum(i["bytes"] for i in infos)
        print(f"{len(infos)} files, {total / 1048576:.2f} MB, manifest written")
        known = {i["name"] + ".ogg" for i in infos}
        orphans = sorted(f for f in os.listdir(a.out) if f.endswith(".ogg") and f not in known)
        if orphans:
            print("Not produced by this generator (delete them and their .meta files if obsolete):", ", ".join(orphans))
    if bad:
        print("WARNINGS:", ", ".join(bad))
    return 0


if __name__ == "__main__":
    sys.exit(main())
