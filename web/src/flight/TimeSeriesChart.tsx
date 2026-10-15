import { Box, useComputedColorScheme } from '@mantine/core';
import { useElementSize } from '@mantine/hooks';
import { useEffect, useRef } from 'react';
import uPlot, { type AlignedData, type Options } from 'uplot';
import 'uplot/dist/uPlot.min.css';

export interface ChartSeries {
  name: string;
  color: string;
  /** Seconds since log start, ascending. */
  t: number[];
  v: (number | null)[];
}

/** A vertical line at an instant (an event). */
export interface ChartMarker {
  t: number;
  color: string;
  label: string;
  emphasized?: boolean;
}

/** A shaded time range (a finding, or a selected window). */
export interface ChartBand {
  from: number;
  to: number;
  color: string;
  label: string;
}

interface Props {
  series: ChartSeries[];
  unit: string | null;
  xRange: [number, number];
  markers?: ChartMarker[];
  bands?: ChartBand[];
  height?: number;
  /** Charts with the same key share the cursor. */
  syncKey?: string;
  onZoom?: (from: number, to: number) => void;
  /** Called with the time under the cursor when the user clicks without dragging. */
  onPick?: (t: number) => void;
}

/** Converts a CSS color (also var(--mantine-color-…)) to something canvas accepts. */
function resolveColor(el: HTMLElement, color: string): string {
  if (!color.startsWith('var(')) {
    return color;
  }

  const name = color.slice(4, -1).trim();
  return getComputedStyle(el).getPropertyValue(name).trim() || '#888';
}

function withAlpha(color: string, alpha: number): string {
  const m = /^#([0-9a-f]{6})$/i.exec(color);
  if (!m) {
    return color;
  }

  const n = parseInt(m[1]!, 16);
  return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`;
}

/**
 * Canvas time-series chart (uPlot). Several series on one y axis (one unit); drag to zoom on the time axis.
 * Null values are drawn as gaps: missing data is not interpolated.
 */
export function TimeSeriesChart({ series, unit, xRange, markers = [], bands = [], height = 220, syncKey, onZoom, onPick }: Props) {
  const { ref: sizeRef, width } = useElementSize();
  const host = useRef<HTMLDivElement | null>(null);
  const plot = useRef<uPlot | null>(null);
  const scheme = useComputedColorScheme('light');
  const callbacks = useRef({ onZoom, onPick });
  useEffect(() => {
    callbacks.current = { onZoom, onPick };
  });

  useEffect(() => {
    const el = host.current;
    if (!el || width === 0) {
      return;
    }

    const axis = scheme === 'dark' ? '#c1c2c5' : '#495057';
    const grid = scheme === 'dark' ? 'rgba(255,255,255,0.08)' : 'rgba(0,0,0,0.07)';
    const data = (series.length > 0 ? uPlot.join(series.map((s) => [s.t, s.v] as [number[], (number | null)[]])) : [[]]) as AlignedData;
    const resolvedMarkers = markers.map((m) => ({ ...m, color: resolveColor(el, m.color) }));
    const resolvedBands = bands.map((b) => ({ ...b, color: resolveColor(el, b.color) }));

    let downX: number | null = null;
    const options: Options = {
      width,
      height,
      scales: { x: { time: false, range: () => xRange } },
      legend: { live: true },
      cursor: {
        drag: { x: true, y: false, setScale: false },
        sync: syncKey ? { key: syncKey } : undefined,
      },
      axes: [
        { stroke: axis, grid: { stroke: grid }, ticks: { stroke: grid }, label: 'Zaman (s, log başlangıcından)', labelSize: 18, font: '11px sans-serif', labelFont: '11px sans-serif' },
        { stroke: axis, grid: { stroke: grid }, ticks: { stroke: grid }, label: unit ?? 'birimsiz', labelSize: 18, font: '11px sans-serif', labelFont: '11px sans-serif', size: 60 },
      ],
      series: [
        { label: 't (s)', value: (_u, v) => (v == null ? '—' : v.toFixed(3)) },
        ...series.map((s) => ({
          label: unit ? `${s.name} [${unit}]` : s.name,
          stroke: resolveColor(el, s.color),
          width: 1.25,
          spanGaps: false,
          points: { show: false },
          value: (_u: uPlot, v: number | null) => (v == null ? '—' : Math.abs(v) >= 1000 ? v.toFixed(0) : v.toPrecision(5)),
        })),
      ],
      hooks: {
        drawClear: [
          (u) => {
            const { ctx } = u;
            const top = u.bbox.top;
            const h = u.bbox.height;
            ctx.save();
            for (const b of resolvedBands) {
              const x0 = u.valToPos(Math.max(b.from, xRange[0]), 'x', true);
              const x1 = u.valToPos(Math.min(b.to, xRange[1]), 'x', true);
              if (x1 >= x0) {
                ctx.fillStyle = withAlpha(b.color, 0.16);
                ctx.fillRect(x0, top, Math.max(x1 - x0, 2), h);
              }
            }

            ctx.restore();
          },
        ],
        draw: [
          (u) => {
            const { ctx } = u;
            ctx.save();
            for (const m of resolvedMarkers) {
              if (m.t < xRange[0] || m.t > xRange[1]) {
                continue;
              }

              const x = Math.round(u.valToPos(m.t, 'x', true)) + 0.5;
              ctx.strokeStyle = m.color;
              ctx.lineWidth = m.emphasized ? 2.5 : 1;
              ctx.setLineDash(m.emphasized ? [] : [4, 3]);
              ctx.beginPath();
              ctx.moveTo(x, u.bbox.top);
              ctx.lineTo(x, u.bbox.top + u.bbox.height);
              ctx.stroke();
            }

            ctx.restore();
          },
        ],
        setSelect: [
          (u) => {
            if (u.select.width > 4) {
              const from = u.posToVal(u.select.left, 'x');
              const to = u.posToVal(u.select.left + u.select.width, 'x');
              u.setSelect({ left: 0, top: 0, width: 0, height: 0 }, false);
              callbacks.current.onZoom?.(from, to);
            }
          },
        ],
        ready: [
          (u) => {
            u.over.addEventListener('mousedown', (e) => {
              downX = e.clientX;
            });
            u.over.addEventListener('mouseup', (e) => {
              if (downX !== null && Math.abs(e.clientX - downX) < 3 && u.cursor.left != null && u.cursor.left >= 0) {
                callbacks.current.onPick?.(u.posToVal(u.cursor.left, 'x'));
              }

              downX = null;
            });
          },
        ],
      },
    };

    plot.current?.destroy();
    plot.current = new uPlot(options, data, el);
    return () => {
      plot.current?.destroy();
      plot.current = null;
    };
    // Width changes are handled below without rebuilding.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [series, unit, xRange[0], xRange[1], markers, bands, height, syncKey, scheme, width === 0]);

  useEffect(() => {
    if (plot.current && width > 0) {
      plot.current.setSize({ width, height });
    }
  }, [width, height]);

  return (
    <Box ref={sizeRef} w="100%">
      <div ref={host} role="img" aria-label={`Grafik: ${series.map((s) => s.name).join(', ')}${unit ? ` (${unit})` : ''}`} />
    </Box>
  );
}
