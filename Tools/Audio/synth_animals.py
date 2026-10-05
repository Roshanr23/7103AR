"""
Synthesise every sound the AR scenes use: one ambience loop per scene, idle
calls per animal, and a gesture reaction per animal. Nothing is downloaded --
all of it is built here from harmonic voices pushed through moving formant
filters, and shaped noise.

Needs numpy only. Blender ships it, so run with Blender's Python:

    /Applications/Blender.app/Contents/Resources/4.5/python/bin/python3.11 \
        Tools/Audio/synth_animals.py Assets/Audio

Writes 16-bit mono 44.1 kHz WAVs. Re-running regenerates the same files (fixed seeds).
"""
import os
import sys
import wave

import numpy as np

SR = 44100
OUT = sys.argv[1] if len(sys.argv) > 1 else "Assets/Audio"


# ----------------------------------------------------------------------------- basics
def t_axis(dur):
    return np.arange(int(dur * SR)) / SR


def env_adsr(n, a=0.01, d=0.05, s=0.7, r=0.1):
    """Attack/decay/sustain/release envelope over n samples (times in seconds)."""
    a, d, r = int(a * SR), int(d * SR), int(r * SR)
    a, d, r = max(a, 1), max(d, 1), max(r, 1)
    sus = max(n - a - d - r, 0)
    e = np.concatenate([np.linspace(0, 1, a), np.linspace(1, s, d), np.full(sus, s), np.linspace(s, 0, r)])
    return np.pad(e, (0, max(0, n - len(e))))[:n]


def env_perc(n, attack=0.004, decay=0.08):
    t = np.arange(n) / SR
    e = np.minimum(t / attack, 1.0) * np.exp(-np.maximum(t - attack, 0) / decay)
    return e


def smooth_noise(n, rate_hz, rng):
    """Slowly wandering 0..1 control signal (for gusts, jitter)."""
    k = max(2, int(n / SR * rate_hz) + 2)
    pts = rng.random(k)
    x = np.linspace(0, k - 1, n)
    i = np.floor(x).astype(int)
    f = x - i
    f = f * f * (3 - 2 * f)
    i2 = np.minimum(i + 1, k - 1)
    return pts[i] * (1 - f) + pts[i2] * f


def fft_filter(x, response):
    """Static filter: response(freqs) -> gain."""
    X = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / SR)
    return np.fft.irfft(X * response(f), len(x))


def stft_filter(x, response_at, frame=2048, hop=512):
    """Time-varying filter: response_at(time_s, freqs) -> gain, overlap-add."""
    n = len(x)
    win = np.hanning(frame)
    pad = np.pad(x, (frame, frame))
    out = np.zeros_like(pad)
    norm = np.zeros_like(pad)
    f = np.fft.rfftfreq(frame, 1 / SR)
    for start in range(0, len(pad) - frame, hop):
        seg = pad[start:start + frame] * win
        tc = (start + frame / 2 - frame) / SR
        S = np.fft.rfft(seg) * response_at(tc, f)
        out[start:start + frame] += np.fft.irfft(S, frame) * win
        norm[start:start + frame] += win * win
    out = out / np.maximum(norm, 1e-6)
    return out[frame:frame + n]


def bandpass(lo, hi, slope=2.0):
    def r(f):
        f = np.maximum(f, 1.0)
        return 1.0 / (1 + (lo / f) ** (2 * slope)) / (1 + (f / hi) ** (2 * slope))
    return r


def lowpass(fc, slope=2.0):
    return lambda f: 1.0 / (1 + (np.maximum(f, 1e-3) / fc) ** (2 * slope))


def formants(f, peaks):
    """Sum of resonant peaks [(centre, bandwidth, gain)]."""
    g = np.zeros_like(f)
    for c, bw, a in peaks:
        g += a / (1 + ((f - c) / (bw / 2)) ** 2)
    return g + 0.004


def pink(n, rng):
    w = rng.standard_normal(n)
    return fft_filter(w, lambda f: 1 / np.sqrt(np.maximum(f, 20.0)))


def harmonic_voice(f0, n_harm=30, tilt=1.0, jitter=0.0, rng=None):
    """Harmonic source following the f0 contour (array, Hz), spectral tilt ~ 1/k^tilt."""
    if jitter and rng is not None:
        f0 = f0 * (1 + jitter * (smooth_noise(len(f0), 40, rng) - 0.5))
    phase = 2 * np.pi * np.cumsum(f0) / SR
    y = np.zeros(len(f0))
    for k in range(1, n_harm + 1):
        alias = (k * f0) < SR / 2 * 0.95
        y += alias * np.sin(k * phase) / k ** tilt
    return y


def voiced_call(f0, peaks_at, breath=0.15, tilt=1.0, jitter=0.01, rng=None, env=None, top=6000):
    """A voiced call: harmonic source + breath noise through moving formants, rolled off above `top` Hz."""
    n = len(f0)
    src = harmonic_voice(f0, tilt=tilt, jitter=jitter, rng=rng)
    src = src / (np.max(np.abs(src)) + 1e-9)
    if breath:
        src += breath * rng.standard_normal(n)
    y = stft_filter(src, lambda tc, f: formants(f, peaks_at(tc)) * lowpass(top, 2.0)(f), frame=1024, hop=256)
    if env is not None:
        y *= env
    return y


def place(buf, clip, at_s, gain=1.0):
    i = int(at_s * SR)
    j = min(len(buf), i + len(clip))
    if i < len(buf):
        buf[i:j] += gain * clip[:j - i]


def normalise(x, peak_db=-1.0):
    m = np.max(np.abs(x)) + 1e-9
    return x / m * 10 ** (peak_db / 20)


def fade(x, fin=0.005, fout=0.02):
    a, b = int(fin * SR), int(fout * SR)
    x = x.copy()
    if a: x[:a] *= np.linspace(0, 1, a)
    if b: x[-b:] *= np.linspace(1, 0, b)
    return x


def loopify(x, xfade_s=4.0):
    """Make a seamless loop: crossfade the tail into the head (equal power)."""
    k = int(xfade_s * SR)
    head, tail = x[:k], x[-k:]
    w = np.linspace(0, np.pi / 2, k)
    mixed = head * np.sin(w) + tail * np.cos(w)
    return np.concatenate([mixed, x[k:-k]])


def write(name, x):
    os.makedirs(OUT, exist_ok=True)
    x = np.clip(x, -1, 1)
    pcm = (x * 32767).astype("<i2")
    path = os.path.join(OUT, name + ".wav")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())
    print(f"{path}: {len(x) / SR:.2f}s")


# ----------------------------------------------------------------------------- ambience
def wind(dur, rng, lo=120, hi=900, gust_rate=0.18, gust_depth=0.75, whistle=0.0):
    n = int(dur * SR)
    base = pink(n, rng)
    g = smooth_noise(n, gust_rate, rng)
    g2 = smooth_noise(n, gust_rate * 3.1, rng)
    gust = (1 - gust_depth) + gust_depth * (0.65 * g + 0.35 * g2)
    # the band opens up as a gust builds, like air moving faster through branches
    def resp(tc, f):
        k = min(int(tc * SR), n - 1)
        open_ = gust[max(k, 0)]
        return bandpass(lo * (0.8 + 0.4 * open_), hi * (0.6 + 0.9 * open_), 1.5)(f)
    y = stft_filter(base, resp) * gust
    if whistle:
        # faint airy whistle riding the gusts
        wn = rng.standard_normal(n)
        def wr(tc, f):
            k = min(max(int(tc * SR), 0), n - 1)
            c = 1400 + 900 * gust[k]
            return 1 / (1 + ((f - c) / 60) ** 2)
        y += whistle * stft_filter(wn, wr) * gust ** 2
    return y


def chickadee(rng):
    """'fee-bee': two clean whistles, the second a step lower."""
    parts = []
    for f_start, f_end, d in ((3950, 3900, 0.32), (3350, 3300, 0.28)):
        t = t_axis(d)
        f = np.linspace(f_start, f_end, len(t))
        ph = 2 * np.pi * np.cumsum(f) / SR
        y = np.sin(ph) * env_adsr(len(t), 0.02, 0.05, 0.85, 0.08)
        parts.append(y)
        parts.append(np.zeros(int(0.06 * SR)))
    return np.concatenate(parts)


def tree_creak(rng, dur=1.2):
    """Slow groaning wood: a low, irregular pulse train through a woody resonance."""
    t = t_axis(dur)
    rate = 18 + 10 * smooth_noise(len(t), 3, rng)
    ph = np.cumsum(rate) / SR
    pulses = (np.diff(np.floor(ph), prepend=0) > 0).astype(float)
    pulses *= 0.5 + rng.random(len(t))
    y = fft_filter(pulses, lambda f: formants(f, [(380, 120, 1.0), (900, 300, 0.5), (1800, 500, 0.2)]))
    return y * env_adsr(len(t), 0.2, 0.2, 0.8, 0.4)


def ambience_forest(seed=1):
    rng = np.random.default_rng(seed)
    dur = 34.0
    y = 0.9 * wind(dur, rng, 100, 800, 0.15, 0.7)
    y = y / np.max(np.abs(y)) * 0.5
    for at in (3.0, 14.5, 25.0):
        c = chickadee(rng)
        place(y, fft_filter(c, lowpass(5000)), at + rng.random(), 0.05)   # distant: quiet
    place(y, tree_creak(rng), 9.0, 0.06)
    place(y, tree_creak(rng, 1.6), 21.0, 0.05)
    return normalise(loopify(y), -9)


def ambience_snowfield(seed=2):
    rng = np.random.default_rng(seed)
    dur = 34.0
    y = wind(dur, rng, 180, 2200, 0.22, 0.85, whistle=0.05)
    y += 0.4 * wind(dur, rng, 60, 300, 0.1, 0.5)        # low rumble underneath
    return normalise(loopify(y), -8)


def ambience_night(seed=3):
    rng = np.random.default_rng(seed)
    dur = 34.0
    y = 0.6 * wind(dur, rng, 80, 500, 0.08, 0.6)
    y = y / np.max(np.abs(y)) * 0.5
    place(y, tree_creak(rng, 2.0), 7.0, 0.07)
    place(y, tree_creak(rng, 1.4), 22.0, 0.05)
    # snow sliding off a branch: a soft hiss with a little thump
    for at in (16.0,):
        n = int(0.9 * SR)
        hiss = fft_filter(rng.standard_normal(n), bandpass(800, 5000)) * env_adsr(n, 0.05, 0.2, 0.5, 0.5)
        place(y, hiss, at, 0.05)
    return normalise(loopify(y), -11)


# ----------------------------------------------------------------------------- squirrel
def squirrel_chip(rng, f_hi=5200, f_lo=3000, d=0.028, noisy=0.6):
    t = t_axis(d)
    f = np.geomspace(f_hi, f_lo, len(t))
    ph = 2 * np.pi * np.cumsum(f) / SR
    tone = np.sin(ph) + 0.35 * np.sin(2 * ph)
    nz = fft_filter(rng.standard_normal(len(t)), bandpass(2500, 8000))
    y = (1 - noisy) * tone + noisy * nz / (np.max(np.abs(nz)) + 1e-9)
    return y * env_perc(len(t), 0.002, d * 0.35)


def squirrel_chatter(seed, length=1.1, rate=16.0, end_trill=True):
    """Red squirrel chatter: a fast run of chips, then a buzzy 'tcherrr'."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int((length + 0.8) * SR))
    t = 0.02
    while t < length:
        hi = 4800 + 900 * rng.random()
        place(y, squirrel_chip(rng, hi, hi * 0.6), t, 0.6 + 0.4 * rng.random())
        t += 1 / (rate * (0.85 + 0.3 * rng.random()))
    if end_trill:
        d = 0.45
        tt = t_axis(d)
        am = 0.5 + 0.5 * np.sign(np.sin(2 * np.pi * 38 * tt))      # rattling buzz
        nz = fft_filter(rng.standard_normal(len(tt)), bandpass(2200, 5500, 2.5))
        tr = nz / np.max(np.abs(nz)) * am * env_adsr(len(tt), 0.01, 0.1, 0.6, 0.2)
        place(y, tr, t + 0.05, 0.7)
    return normalise(fade(y[: int((t + 0.6) * SR)]), -1)


def squirrel_excited(seed=21):
    """Palm up: quick 'chuk-chuk-chuk' getting faster, then a squeaky trill."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int(2.0 * SR))
    t, gap = 0.02, 0.16
    for i in range(7):
        chuk = squirrel_chip(rng, 2600 + 300 * rng.random(), 1500, d=0.045, noisy=0.35)
        place(y, chuk, t, 0.9)
        t += gap
        gap *= 0.82
    # squeaky rising trill
    d = 0.35
    tt = t_axis(d)
    f = 4200 + 1200 * tt / d + 300 * np.sin(2 * np.pi * 22 * tt)
    sq = np.sin(2 * np.pi * np.cumsum(f) / SR) * env_adsr(len(tt), 0.01, 0.05, 0.8, 0.12)
    place(y, sq, t + 0.05, 0.45)
    return normalise(fade(y[: int((t + 0.5) * SR)]), -1)


# ----------------------------------------------------------------------------- fox
def fox_bark_one(rng, d=0.26, f_peak=560):
    """One harsh 'wow': voice rises then falls, rough, with a noisy onset."""
    t = t_axis(d)
    u = t / d
    f0 = f_peak * (0.75 + 0.25 * np.sin(np.pi * np.minimum(u * 1.4, 1.0))) * (1 - 0.25 * u)
    env = env_adsr(len(t), 0.008, 0.06, 0.7, 0.09)
    peaks = lambda tc: [(800 + 500 * np.sin(np.pi * min(tc / d, 1)), 260, 1.0),
                        (2100, 400, 0.55), (3300, 600, 0.25)]
    y = voiced_call(f0, peaks, breath=0.12, tilt=0.8, jitter=0.06, rng=rng, env=env, top=4000)
    onset = fft_filter(rng.standard_normal(len(t)), bandpass(700, 3500)) * env_perc(len(t), 0.002, 0.02)
    return y / (np.max(np.abs(y)) + 1e-9) + 0.15 * onset / (np.max(np.abs(onset)) + 1e-9)


def fox_barks(seed, count=3):
    rng = np.random.default_rng(seed)
    y = np.zeros(int((0.6 * count + 0.6) * SR))
    t = 0.02
    for i in range(count):
        place(y, fox_bark_one(rng, 0.22 + 0.06 * rng.random(), 500 + 120 * rng.random()), t)
        t += 0.45 + 0.25 * rng.random()
    return normalise(fade(y[: int((t + 0.2) * SR)]), -1)


def fox_whine(seed=31):
    """Palm up: a soft, curious whine that rises and wavers, then a little huff."""
    rng = np.random.default_rng(seed)
    d = 0.85
    t = t_axis(d)
    u = t / d
    f0 = 720 + 420 * np.sin(np.pi * 0.6 * u) + 25 * np.sin(2 * np.pi * 7 * t)
    env = env_adsr(len(t), 0.06, 0.1, 0.8, 0.25)
    peaks = lambda tc: [(1300, 300, 1.0), (2600, 500, 0.5), (4000, 800, 0.2)]
    y = voiced_call(f0, peaks, breath=0.08, tilt=1.4, jitter=0.02, rng=rng, env=env, top=4500)
    y = y / np.max(np.abs(y))
    huff_n = int(0.18 * SR)
    huff = fft_filter(rng.standard_normal(huff_n), bandpass(300, 2500)) * env_perc(huff_n, 0.01, 0.06)
    out = np.zeros(int((d + 0.45) * SR))
    place(out, y, 0.0)
    place(out, huff / np.max(np.abs(huff)), d + 0.12, 0.35)
    return normalise(fade(out), -1)


# ----------------------------------------------------------------------------- deer
def deer_snort(seed=41):
    """A sharp exhaled blow through the nose."""
    rng = np.random.default_rng(seed)
    d = 0.55
    n = int(d * SR)
    nz = rng.standard_normal(n)
    # a flutter of the nostrils in the first part
    flutter = 1 + 0.5 * np.sin(2 * np.pi * 34 * t_axis(d)) * np.exp(-t_axis(d) / 0.15)
    y = fft_filter(nz * flutter, lambda f: bandpass(150, 1800, 1.2)(f) * formants(f, [(450, 300, 1.0), (1100, 500, 0.6)]))
    y *= env_perc(n, 0.006, 0.17)
    return normalise(fade(y), -2)


def deer_bleat(seed=42):
    """Palm up: a soft nasal 'maa' -- the friendly contact call."""
    rng = np.random.default_rng(seed)
    d = 0.7
    t = t_axis(d)
    u = t / d
    f0 = 330 * (1 + 0.12 * np.sin(np.pi * u)) * (1 - 0.1 * u) * (1 + 0.012 * np.sin(2 * np.pi * 6 * t))
    env = env_adsr(len(t), 0.05, 0.1, 0.8, 0.25)
    peaks = lambda tc: [(650 + 150 * np.sin(np.pi * min(tc / d, 1)), 180, 1.0),
                        (1750, 300, 0.7), (2700, 500, 0.35), (250, 120, 0.4)]   # low nasal resonance
    y = voiced_call(f0, peaks, breath=0.06, tilt=1.1, jitter=0.015, rng=rng, env=env, top=4500)
    return normalise(fade(y), -1)


def sniffs(seed, count=3, lo=900, hi=4500, d=0.09, gap=0.14, gain_up=True):
    """Quick inhaled sniffs."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int((count * gap + 0.3) * SR))
    for i in range(count):
        n = int(d * SR)
        s = fft_filter(rng.standard_normal(n), bandpass(lo, hi, 1.3)) * env_adsr(n, 0.02, 0.02, 0.8, 0.04)
        place(y, s / (np.max(np.abs(s)) + 1e-9), 0.02 + i * gap * (0.9 + 0.2 * rng.random()),
              (0.6 + 0.4 * i / max(count - 1, 1)) if gain_up else 1.0)
    return normalise(fade(y), -3)


# ----------------------------------------------------------------------------- hare
def thump(rng, d=0.22):
    t = t_axis(d)
    f = 95 * np.exp(-t / 0.05) + 55
    body = np.sin(2 * np.pi * np.cumsum(f) / SR) * env_perc(len(t), 0.002, 0.06)
    click = fft_filter(rng.standard_normal(len(t)), bandpass(400, 3000)) * env_perc(len(t), 0.001, 0.008)
    return body + 0.25 * click / (np.max(np.abs(click)) + 1e-9)


def hare_thumps(seed=51):
    """Palm up: a warning thump of the hind feet (two), then a couple of sniffs."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int(1.4 * SR))
    place(y, thump(rng), 0.02, 1.0)
    place(y, thump(rng), 0.2, 0.8)
    place(y, sniffs(seed + 1, 2, 1500, 6000, 0.06, 0.12, False), 0.75, 0.25)
    return normalise(fade(y), -1)


# ----------------------------------------------------------------------------- owl
def owl_hoots(seed, count=3, f=240):
    """Snowy owl: deep, booming 'hoo' hoots, the first strongest."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int((count * 0.75 + 0.6) * SR))
    t0 = 0.02
    for i in range(count):
        d = 0.5 if i == 0 else 0.42
        tt = t_axis(d)
        f0 = f * (1.04 - 0.08 * tt / d) * (1 - 0.02 * i)
        env = env_adsr(len(tt), 0.07, 0.1, 0.85, 0.22)
        peaks = lambda tc: [(f * 1.9, 160, 1.0), (f * 4.2, 300, 0.25)]
        h = voiced_call(f0, peaks, breath=0.05, tilt=1.8, jitter=0.005, rng=rng, env=env)
        place(y, h / np.max(np.abs(h)), t0, 1.0 - 0.15 * i)
        t0 += 0.7 + 0.1 * rng.random()
    return normalise(fade(y[: int((t0 + 0.2) * SR)]), -1)


def wing_whoosh(rng, d=0.32):
    n = int(d * SR)
    e = np.sin(np.pi * np.linspace(0, 1, n)) ** 1.5
    nz = rng.standard_normal(n)
    def r(tc, f):
        c = 180 + 350 * np.sin(np.pi * min(max(tc / d, 0), 1))
        return bandpass(c * 0.5, c * 2.5, 1.5)(f) * lowpass(1800, 2.0)(f)
    return stft_filter(nz, r, 1024, 256) * e


def owl_come(seed=61):
    """Palm up: two big wing beats coming closer, then a short barking 'krek'."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int(1.8 * SR))
    place(y, wing_whoosh(rng), 0.0, 0.3)
    place(y, wing_whoosh(rng), 0.38, 0.45)
    d = 0.2
    tt = t_axis(d)
    f0 = 820 * (1 + 0.15 * np.sin(np.pi * tt / d))
    env = env_adsr(len(tt), 0.005, 0.04, 0.7, 0.08)
    peaks = lambda tc: [(1400, 400, 1.0), (2800, 700, 0.5)]
    krek = voiced_call(f0, peaks, breath=0.15, tilt=0.7, jitter=0.08, rng=rng, env=env, top=4500)
    place(y, krek / np.max(np.abs(krek)), 0.95, 0.6)
    place(y, krek / np.max(np.abs(krek)), 1.22, 0.45)
    return normalise(fade(y), -1)


# ----------------------------------------------------------------------------- footsteps
def crunch_grains(rng, d, count, lo=900, hi=5000, rise=0.25):
    """Snow compressing: a cloud of tiny noise grains under a rise-and-decay envelope."""
    n = int(d * SR)
    y = np.zeros(n)
    for _ in range(count):
        g = int(SR * rng.uniform(0.0015, 0.005))
        grain = rng.standard_normal(g) * np.hanning(g)
        at = int(n * rng.beta(1.6, 3.0))
        j = min(n, at + g)
        y[at:j] += grain[: j - at] * rng.uniform(0.3, 1.0)
    y = fft_filter(y, bandpass(lo, hi, 1.4))
    t = np.linspace(0, 1, n)
    env = np.minimum(t / rise, 1.0) * np.exp(-np.maximum(t - rise, 0) / 0.35)
    return y * env


def snow_step(seed):
    """A paw on snow: a short crunch over a soft low compression thud."""
    rng = np.random.default_rng(seed)
    d = rng.uniform(0.09, 0.13)
    c = crunch_grains(rng, d, rng.integers(28, 44), 500, 3000)
    n = len(c)
    thud = fft_filter(rng.standard_normal(n), lowpass(260, 2.0)) * env_perc(n, 0.004, 0.025)
    y = c / (np.max(np.abs(c)) + 1e-9) + 0.7 * thud / (np.max(np.abs(thud)) + 1e-9)
    return normalise(fade(np.pad(y, (0, int(0.03 * SR)))), -3)


def hare_land(seed):
    """Both hind feet coming down: a soft thud with a little snow crunch."""
    rng = np.random.default_rng(seed)
    n = int(0.16 * SR)
    t = t_axis(0.16)
    f = 85 * np.exp(-t / 0.04) + 50
    thud = np.sin(2 * np.pi * np.cumsum(f) / SR) * env_perc(n, 0.003, 0.035)
    c = crunch_grains(rng, 0.16, 26, 700, 4000, rise=0.1)
    y = thud + 0.45 * c / (np.max(np.abs(c)) + 1e-9)
    return normalise(fade(y), -3)


def squirrel_patter(seed):
    """Little paws: two or three light taps a few milliseconds apart."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int(0.12 * SR))
    t = 0.004
    for _ in range(rng.integers(2, 4)):
        g = int(0.004 * SR)
        tap = fft_filter(rng.standard_normal(g * 3), bandpass(1200, 6000))[:g] * env_perc(g, 0.0005, 0.0015)
        place(y, tap, t, rng.uniform(0.6, 1.0))
        t += rng.uniform(0.018, 0.035)
    c = crunch_grains(rng, 0.08, 10, 1500, 6000, rise=0.15)
    place(y, c / (np.max(np.abs(c)) + 1e-9), 0.0, 0.25)
    return normalise(fade(y), -4)


def snow_puff(seed=81):
    """An animal arriving: a soft 'whump' of disturbed snow with a sparkle of grains."""
    rng = np.random.default_rng(seed)
    d = 0.6
    n = int(d * SR)
    body = fft_filter(rng.standard_normal(n), lowpass(500, 1.5)) * env_adsr(n, 0.03, 0.12, 0.35, 0.4)
    sparkle = crunch_grains(rng, d, 40, 3000, 9000, rise=0.15)
    y = body / np.max(np.abs(body)) + 0.18 * sparkle / (np.max(np.abs(sparkle)) + 1e-9)
    return normalise(fade(y), -3)


# ----------------------------------------------------------------------------- ui
def camera_shutter(seed=121):
    """A soft mechanical shutter: a sharp click, the curtain's quick 'shk', a second click."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int(0.22 * SR))
    for at, gain, lo, hi, d in ((0.0, 1.0, 1500, 9000, 0.006), (0.012, 0.55, 2500, 8000, 0.035), (0.075, 0.75, 1200, 7000, 0.008)):
        n = int(d * SR)
        burst = fft_filter(rng.standard_normal(n * 2), bandpass(lo, hi, 1.6))[:n] * env_perc(n, 0.0005, d * 0.4)
        place(y, burst / (np.max(np.abs(burst)) + 1e-9), at, gain)
    n = int(0.05 * SR)
    t = t_axis(0.05)
    thunk = np.sin(2 * np.pi * 180 * t) * env_perc(n, 0.001, 0.012)
    place(y, thunk, 0.0, 0.35)
    return normalise(fade(y), -3)


# ----------------------------------------------------------------------------- gesture reactions
def tone(f0, d, attack=0.005, decay=None, harm=((1, 1.0),), vibrato=0.0):
    """A plain pitched note: harmonics of f0 (scalar or contour) under an attack/decay envelope."""
    t = t_axis(d)
    f = np.full(len(t), f0) if np.isscalar(f0) else f0
    if vibrato:
        f = f * (1 + vibrato * np.sin(2 * np.pi * 6 * t))
    ph = 2 * np.pi * np.cumsum(f) / SR
    y = sum(a * np.sin(k * ph) for k, a in harm)
    return y * env_perc(len(t), attack, decay if decay else d * 0.35)


def squirrel_alarm(seed=131):
    """Startled: a harsher, faster chatter than the idle one, ending in a buzz."""
    return squirrel_chatter(seed, length=0.75, rate=24.0, end_trill=True)


def squirrel_squeak(seed=132):
    d = 0.2
    t = t_axis(d)
    f = 3600 + 1800 * np.sin(np.pi * t / d)
    return normalise(fade(tone(f, d, 0.01, 0.09, ((1, 1), (2, 0.25)))), -2)


def fox_yelp(seed=133):
    """Startled: one short, sharp yelp."""
    rng = np.random.default_rng(seed)
    d = 0.24
    t = t_axis(d)
    u = t / d
    f0 = 880 + 520 * np.sin(np.pi * np.minimum(u * 1.3, 1.0)) - 200 * u
    env = env_adsr(len(t), 0.006, 0.04, 0.7, 0.08)
    peaks = lambda tc: [(1500, 400, 1.0), (2800, 600, 0.45)]
    y = voiced_call(f0, peaks, breath=0.12, tilt=0.9, jitter=0.05, rng=rng, env=env, top=5000)
    return normalise(fade(y), -1)


def snap_munch(seed=134):
    """The fox snapping up a treat: a jaw click, then two munches."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int(0.7 * SR))
    n = int(0.01 * SR)
    click = fft_filter(rng.standard_normal(n * 2), bandpass(1500, 6000))[:n] * env_perc(n, 0.0005, 0.003)
    place(y, click / np.max(np.abs(click)), 0.0, 1.0)
    for at in (0.18, 0.42):
        c = crunch_grains(rng, 0.12, 22, 800, 4500, rise=0.2)
        place(y, c / (np.max(np.abs(c)) + 1e-9), at, 0.6)
    return normalise(fade(y), -2)


def nibble(seed):
    """Quick small bites: four little crunches."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int(0.6 * SR))
    for i in range(4):
        c = crunch_grains(rng, 0.06, 14, 1500, 6500, rise=0.15)
        place(y, c / (np.max(np.abs(c)) + 1e-9), 0.02 + i * 0.13 + rng.uniform(-0.01, 0.01), rng.uniform(0.6, 1.0))
    return normalise(fade(y), -3)


def owl_gulp(seed=135):
    rng = np.random.default_rng(seed)
    d = 0.22
    t = t_axis(d)
    f0 = 320 - 140 * t / d
    env = env_adsr(len(t), 0.01, 0.05, 0.6, 0.08)
    peaks = lambda tc: [(500, 200, 1.0), (1100, 300, 0.3)]
    y = voiced_call(f0, peaks, breath=0.05, tilt=1.6, rng=rng, env=env, top=2500)
    return normalise(fade(y), -2)


def owl_clack(seed=136):
    """Bill clacking: a run of sharp wooden clicks."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int(0.5 * SR))
    for i in range(6):
        n = int(0.006 * SR)
        k = fft_filter(rng.standard_normal(n * 3), lambda f: formants(f, [(2200, 600, 1.0), (3800, 900, 0.5)]))[:n]
        place(y, k * env_perc(n, 0.0004, 0.0018) / (np.max(np.abs(k)) + 1e-9), 0.01 + i * 0.055 + rng.uniform(0, 0.01), rng.uniform(0.7, 1.0))
    return normalise(fade(y), -2)


def whoosh(seed, d=0.42, lo=250, hi=2400, gain_crunch=0.0):
    rng = np.random.default_rng(seed)
    n = int(d * SR)
    nz = rng.standard_normal(n)
    def r(tc, f):
        k = np.sin(np.pi * min(max(tc / d, 0), 1))
        return bandpass(lo * (0.7 + 0.6 * k), hi * (0.5 + 0.8 * k), 1.3)(f)
    y = stft_filter(nz, r, 1024, 256) * np.sin(np.pi * np.linspace(0, 1, n)) ** 1.6
    out = np.zeros(int((d + 0.25) * SR))
    place(out, y / (np.max(np.abs(y)) + 1e-9), 0.0, 0.7)
    if gain_crunch:
        c = crunch_grains(rng, 0.16, 36, 500, 3500, rise=0.1)
        place(out, c / (np.max(np.abs(c)) + 1e-9), d - 0.04, gain_crunch)
    return normalise(fade(out), -2)


def deer_alarm(seed=137):
    """Startled deer: an alarm snort, then a hoof stamp."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int(1.0 * SR))
    place(y, deer_snort(seed), 0.0, 1.0)
    place(y, hare_land(seed + 1), 0.5, 0.9)
    return normalise(fade(y), -1)


# ----------------------------------------------------------------------------- games
NOTE = lambda n: 440.0 * 2 ** ((n - 69) / 12)        # MIDI note -> Hz

def chime(notes, step=0.09, d=0.5, bright=0.35):
    y = np.zeros(int((step * len(notes) + d) * SR))
    for i, n in enumerate(notes):
        place(y, tone(NOTE(n), d, 0.004, d * 0.4, ((1, 1.0), (2, bright), (3, bright * 0.3))), i * step, 1.0 - 0.08 * i)
    return normalise(fade(y), -3)


def blip(n, d=0.09):
    return normalise(fade(tone(NOTE(n), d, 0.003, 0.03, ((1, 1.0), (2, 0.2)))), -6)


def miss_bonk():
    d = 0.3
    t = t_axis(d)
    return normalise(fade(tone(240 * np.exp(-t / 0.4), d, 0.004, 0.09, ((1, 1.0), (2, 0.3)))), -4)


def mouse_squeak(seed):
    """A mouse under snow: two or three tiny, very high chirps."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int(0.5 * SR))
    t0 = 0.01
    for _ in range(rng.integers(2, 4)):
        d = rng.uniform(0.04, 0.07)
        t = t_axis(d)
        f = rng.uniform(6200, 7600) + 900 * np.sin(np.pi * t / d)
        place(y, tone(f, d, 0.004, d * 0.4), t0, rng.uniform(0.6, 1.0))
        t0 += d + rng.uniform(0.04, 0.08)
    return normalise(fade(y[: int((t0 + 0.05) * SR)]), -3)


def acorn_drop(seed=141):
    """An acorn landing: a small woody knock and a little snow."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int(0.25 * SR))
    n = int(0.04 * SR)
    knock = fft_filter(rng.standard_normal(n), lambda f: formants(f, [(900, 200, 1.0), (2100, 400, 0.5)])) * env_perc(n, 0.001, 0.01)
    place(y, knock / np.max(np.abs(knock)), 0.0, 1.0)
    c = crunch_grains(rng, 0.1, 14, 1000, 5000, rise=0.1)
    place(y, c / (np.max(np.abs(c)) + 1e-9), 0.01, 0.35)
    return normalise(fade(y), -3)


def dig(seed=142):
    """Paws scrabbling snow: a quick burst of scratchy crunches."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int(0.5 * SR))
    for i in range(6):
        c = crunch_grains(rng, 0.05, 12, 600, 4000, rise=0.2)
        place(y, c / (np.max(np.abs(c)) + 1e-9), 0.02 + i * 0.065, rng.uniform(0.5, 1.0))
    return normalise(fade(y), -3)


def sink(seed=143):
    """An acorn sinking out of reach: a soft settling hiss."""
    rng = np.random.default_rng(seed)
    n = int(0.35 * SR)
    y = fft_filter(rng.standard_normal(n), bandpass(1500, 6000)) * env_adsr(n, 0.02, 0.1, 0.4, 0.2)
    return normalise(fade(y), -8)


def hoot(seed, d, f=280):
    rng = np.random.default_rng(seed)
    t = t_axis(d)
    f0 = f * (1.03 - 0.06 * t / d)
    env = env_adsr(len(t), 0.05, 0.08, 0.85, min(0.18, d * 0.4))
    peaks = lambda tc: [(f * 1.9, 160, 1.0), (f * 4.2, 300, 0.25)]
    y = voiced_call(f0, peaks, breath=0.05, tilt=1.8, jitter=0.005, rng=rng, env=env, top=3000)
    return normalise(fade(y), -1)


def ui_tap():
    return normalise(fade(tone(NOTE(84), 0.05, 0.002, 0.012, ((1, 1.0), (2, 0.2)))), -10)


# ----------------------------------------------------------------------------- go
if __name__ == "__main__":
    write("amb_forest", ambience_forest())
    write("amb_snowfield", ambience_snowfield())
    write("amb_night", ambience_night())

    write("squirrel_chatter_1", squirrel_chatter(11))
    write("squirrel_chatter_2", squirrel_chatter(12, length=0.7, rate=19, end_trill=False))
    write("squirrel_gesture", squirrel_excited())

    write("fox_bark_1", fox_barks(21, 3))
    write("fox_bark_2", fox_barks(22, 2))
    write("fox_gesture", fox_whine())

    write("deer_snort", deer_snort())
    write("deer_gesture", deer_bleat())
    write("deer_sniff", sniffs(43, 3, 700, 3500, 0.11, 0.17))

    write("hare_sniff", sniffs(52, 4, 1500, 6500, 0.05, 0.1, False))
    write("hare_gesture", hare_thumps())

    write("owl_hoot_1", owl_hoots(71, 3))
    write("owl_hoot_2", owl_hoots(72, 2, 225))
    write("owl_gesture", owl_come())

    for i in range(4):
        write(f"snow_step_{i + 1}", snow_step(91 + i))
    for i in range(2):
        write(f"hare_land_{i + 1}", hare_land(101 + i))
    for i in range(3):
        write(f"squirrel_patter_{i + 1}", squirrel_patter(111 + i))
    write("snow_puff", snow_puff())
    write("ui_shutter", camera_shutter())

    # gesture reactions
    write("squirrel_alarm", squirrel_alarm())
    write("squirrel_squeak", squirrel_squeak())
    write("fox_yelp", fox_yelp())
    write("fox_snap", snap_munch())
    write("nibble_1", nibble(151))
    write("nibble_2", nibble(152))
    write("owl_gulp", owl_gulp())
    write("owl_clack", owl_clack())
    write("pounce", whoosh(153, 0.42, 250, 2400, gain_crunch=0.9))
    write("leap", whoosh(154, 0.5, 200, 1800, gain_crunch=0.7))
    write("spin", whoosh(155, 0.3, 600, 4000))
    write("deer_alarm", deer_alarm())

    # games
    write("game_tick", blip(81))
    write("game_go", blip(88, 0.16))
    write("game_point", chime([84, 88], 0.08, 0.35))
    write("game_great", chime([84, 88, 91], 0.07, 0.45))
    write("game_miss", miss_bonk())
    write("game_win", chime([72, 76, 79, 84], 0.11, 0.7))
    write("game_over", chime([79, 75, 72], 0.16, 0.6, bright=0.2))
    for i in range(3):
        write(f"mouse_squeak_{i + 1}", mouse_squeak(161 + i))
    write("acorn_drop", acorn_drop())
    write("acorn_dig", dig())
    write("acorn_sink", sink())
    write("hoot_short", hoot(171, 0.32))
    write("hoot_long", hoot(172, 0.85))
    write("ui_tap", ui_tap())
