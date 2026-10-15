import { Card, Group, Text } from '@mantine/core';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import { useEffect, useMemo, useRef } from 'react';
import { useSignalData } from '../api/queries';
import { ErrorState, LoadingState } from '../components/States';
import { formatLogTime } from '../lib/format';
import { buildTrack } from './track';
import type { TimeWindow } from './view';

export const GPS_SIGNALS = ['gps.lat', 'gps.lon'];
const TILE_URL = import.meta.env.VITE_MAP_TILE_URL || 'https://tile.openstreetmap.org/{z}/{x}/{y}.png';
const TILE_ATTRIBUTION = import.meta.env.VITE_MAP_TILE_ATTRIBUTION || '&copy; OpenStreetMap katkıcıları';

/**
 * The GPS track as logged. Rendered only when the log has latitude and longitude samples (the page checks the
 * catalog first); the part inside the chart window is highlighted, the picked time is marked.
 */
export function FlightMap({
  flightId,
  logFromUs,
  window,
  pickedUs,
  height = 420,
}: {
  flightId: string;
  logFromUs: number;
  window: TimeWindow | null;
  pickedUs?: number;
  height?: number;
}) {
  const data = useSignalData(flightId, GPS_SIGNALS, {}, 20_000);
  const track = useMemo(
    () => buildTrack(data.data?.signals.find((s) => s.name === 'gps.lat'), data.data?.signals.find((s) => s.name === 'gps.lon')),
    [data.data],
  );

  const container = useRef<HTMLDivElement>(null);
  const map = useRef<L.Map | null>(null);
  const overlay = useRef<L.LayerGroup | null>(null);

  useEffect(() => {
    if (!container.current || track.length === 0) {
      return;
    }

    const m = L.map(container.current, { preferCanvas: true });
    L.tileLayer(TILE_URL, { attribution: TILE_ATTRIBUTION, maxZoom: 19 }).addTo(m);
    const full = L.polyline(
      track.map((p) => [p.lat, p.lon] as L.LatLngTuple),
      { color: '#868e96', weight: 3 },
    ).addTo(m);
    L.circleMarker([track[0]!.lat, track[0]!.lon], { radius: 6, color: '#2f9e44', fillOpacity: 1 }).bindTooltip('Log başı').addTo(m);
    const last = track[track.length - 1]!;
    L.circleMarker([last.lat, last.lon], { radius: 6, color: '#e03131', fillOpacity: 1 }).bindTooltip('Log sonu').addTo(m);
    m.fitBounds(full.getBounds(), { padding: [20, 20] });
    overlay.current = L.layerGroup().addTo(m);
    map.current = m;
    return () => {
      m.remove();
      map.current = null;
      overlay.current = null;
    };
  }, [track]);

  useEffect(() => {
    const layer = overlay.current;
    if (!layer) {
      return;
    }

    layer.clearLayers();
    if (window) {
      const inside = track.filter((p) => p.t >= window.fromUs && p.t <= window.toUs).map((p) => [p.lat, p.lon] as L.LatLngTuple);
      if (inside.length > 1) {
        L.polyline(inside, { color: '#1c7ed6', weight: 5 }).addTo(layer);
      }
    }

    if (pickedUs != null && track.length > 0) {
      const nearest = track.reduce((best, p) => (Math.abs(p.t - pickedUs) < Math.abs(best.t - pickedUs) ? p : best));
      L.circleMarker([nearest.lat, nearest.lon], { radius: 7, color: '#f08c00', fillOpacity: 0.9 })
        .bindTooltip(formatLogTime(nearest.t, logFromUs))
        .addTo(layer);
    }
  }, [track, window, pickedUs, logFromUs]);

  if (data.isPending) {
    return <LoadingState label="GPS izi yükleniyor…" />;
  }

  if (data.isError) {
    return <ErrorState error={data.error} onRetry={() => data.refetch()} />;
  }

  if (track.length === 0) {
    return (
      <Text size="sm" c="dimmed">
        Logda GPS konumu var ama geçerli fix içeren örnek yok; harita çizilmedi.
      </Text>
    );
  }

  const downsampled = data.data.signals.some((s) => s.downsampled);
  return (
    <Card withBorder padding={0}>
      <div ref={container} style={{ height }} role="region" aria-label="GPS izi haritası" />
      <Group px="sm" py={6} gap="md">
        <Text size="xs" c="dimmed">
          {track.length} konum{downsampled ? ' (çizim için azaltılmış)' : ''} · yeşil log başı, kırmızı log sonu, mavi grafikteki aralık
        </Text>
      </Group>
    </Card>
  );
}
