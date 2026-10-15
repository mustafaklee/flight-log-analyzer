/** The part of the log shown in the charts, in µs on the log clock; null means the whole log. */
export interface TimeWindow {
  fromUs: number;
  toUs: number;
}

/** A window around an instant or range, with some context on both sides. */
export function windowAround(fromUs: number, toUs: number, logFromUs: number, logToUs: number, minSpanUs = 20_000_000): TimeWindow {
  const span = Math.max(toUs - fromUs, 0);
  const padding = Math.max(span * 0.5, (minSpanUs - span) / 2);
  return {
    fromUs: Math.max(logFromUs, Math.floor(fromUs - padding)),
    toUs: Math.min(logToUs, Math.ceil(toUs + padding)),
  };
}

export function zoom(window: TimeWindow, factor: number, logFromUs: number, logToUs: number): TimeWindow | null {
  const center = (window.fromUs + window.toUs) / 2;
  const half = ((window.toUs - window.fromUs) * factor) / 2;
  if (center - half <= logFromUs && center + half >= logToUs) {
    return null;
  }

  return { fromUs: Math.max(logFromUs, Math.floor(center - half)), toUs: Math.min(logToUs, Math.ceil(center + half)) };
}

export function pan(window: TimeWindow, fraction: number, logFromUs: number, logToUs: number): TimeWindow {
  const span = window.toUs - window.fromUs;
  let from = window.fromUs + span * fraction;
  from = Math.min(Math.max(from, logFromUs), logToUs - span);
  return { fromUs: Math.floor(from), toUs: Math.floor(from + span) };
}
