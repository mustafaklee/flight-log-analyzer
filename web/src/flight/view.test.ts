import { pan, windowAround, zoom } from './view';

describe('time window helpers', () => {
  it('pads an instant to the minimum span and clamps to the log', () => {
    expect(windowAround(5_000_000, 5_000_000, 0, 100_000_000)).toEqual({ fromUs: 0, toUs: 15_000_000 });
    expect(windowAround(50_000_000, 50_000_000, 0, 100_000_000)).toEqual({ fromUs: 40_000_000, toUs: 60_000_000 });
  });

  it('keeps context around a long range', () => {
    expect(windowAround(40_000_000, 80_000_000, 0, 200_000_000)).toEqual({ fromUs: 20_000_000, toUs: 100_000_000 });
  });

  it('zooms around the center and returns null when the whole log would be shown', () => {
    expect(zoom({ fromUs: 40, toUs: 60 }, 0.5, 0, 100)).toEqual({ fromUs: 45, toUs: 55 });
    expect(zoom({ fromUs: 20, toUs: 80 }, 2, 0, 100)).toBeNull();
  });

  it('pans without leaving the log', () => {
    expect(pan({ fromUs: 10, toUs: 30 }, -1, 0, 100)).toEqual({ fromUs: 0, toUs: 20 });
    expect(pan({ fromUs: 70, toUs: 90 }, 1, 0, 100)).toEqual({ fromUs: 80, toUs: 100 });
  });
});
