import type { SignalSeriesData } from '../api/types';

export interface TrackPoint {
  t: number;
  lat: number;
  lon: number;
}

function valueAt(t: number, ts: number[], vs: (number | null)[]): number | null {
  // Binary search for the last sample at or before t, then interpolate to the next one.
  let lo = 0;
  let hi = ts.length - 1;
  if (hi < 0 || t < ts[0]! || t > ts[hi]!) {
    return null;
  }

  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (ts[mid]! <= t) {
      lo = mid;
    } else {
      hi = mid - 1;
    }
  }

  const v0 = vs[lo];
  if (ts[lo] === t || lo === ts.length - 1) {
    return v0 ?? null;
  }

  const v1 = vs[lo + 1];
  if (v0 == null || v1 == null) {
    return null;
  }

  const f = (t - ts[lo]!) / (ts[lo + 1]! - ts[lo]!);
  return v0 + (v1 - v0) * f;
}

/**
 * Pairs latitude and longitude samples into a track. The API may return the two signals at different timestamps
 * (min/max downsampling picks samples per signal), so longitude is interpolated at each latitude timestamp.
 * Points without a usable position (missing, 0/0 before a fix, out of range) are dropped, not invented.
 */
export function buildTrack(lat: SignalSeriesData | undefined, lon: SignalSeriesData | undefined): TrackPoint[] {
  if (!lat || !lon) {
    return [];
  }

  const points: TrackPoint[] = [];
  lat.timestampsUs.forEach((t, i) => {
    const la = lat.values[i];
    const lo = valueAt(t, lon.timestampsUs, lon.values);
    if (la == null || lo == null || !Number.isFinite(la) || !Number.isFinite(lo)) {
      return;
    }

    if (Math.abs(la) > 90 || Math.abs(lo) > 180 || (Math.abs(la) < 1e-6 && Math.abs(lo) < 1e-6)) {
      return;
    }

    points.push({ t, lat: la, lon: lo });
  });
  return points;
}
