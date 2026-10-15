import { describe, expect, it } from 'vitest';
import { buildTrack } from './track';

const series = (name: string, t: number[], v: (number | null)[]) => ({ name, unit: 'deg', samplesInRange: t.length, downsampled: false, timestampsUs: t, values: v });

describe('buildTrack', () => {
  it('pairs samples at the same timestamps', () => {
    const track = buildTrack(series('gps.lat', [1, 2], [47, 47.1]), series('gps.lon', [1, 2], [8, 8.1]));
    expect(track).toEqual([
      { t: 1, lat: 47, lon: 8 },
      { t: 2, lat: 47.1, lon: 8.1 },
    ]);
  });

  it('interpolates longitude between its own samples', () => {
    const track = buildTrack(series('gps.lat', [15], [47]), series('gps.lon', [10, 20], [8, 9]));
    expect(track[0]!.lon).toBeCloseTo(8.5);
  });

  it('drops positions before a fix and outside the longitude samples', () => {
    const track = buildTrack(series('gps.lat', [0, 5, 30], [0, 47, 47]), series('gps.lon', [0, 5, 20], [0, 8, 8]));
    expect(track.map((p) => p.t)).toEqual([5]);
  });

  it('returns nothing when a signal is missing', () => {
    expect(buildTrack(series('gps.lat', [1], [47]), undefined)).toEqual([]);
  });
});
