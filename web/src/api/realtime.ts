import { useEffect, useState } from 'react';
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { session } from './client';
import { keys } from './queries';
import type { StatusNotification } from './types';

export type RealtimeState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

/**
 * Keeps one connection to the processing hub while signed in (ADR-0011) and refreshes the affected queries when
 * a file or analysis changes status. The notification only says what changed; details are re-read from the API.
 */
export function useRealtime(enabled: boolean): RealtimeState {
  const client = useQueryClient();
  const [state, setState] = useState<RealtimeState>('disconnected');

  useEffect(() => {
    if (!enabled) {
      return;
    }

    const connection = new HubConnectionBuilder()
      .withUrl(new URL('/hubs/processing', window.location.href).toString(), { accessTokenFactory: async () => (await session.getAccessToken()) ?? '' })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('StatusChanged', (n: StatusNotification) => {
      client.invalidateQueries({ queryKey: keys.flight(n.flightId), exact: true });
      client.invalidateQueries({ queryKey: ['flights'] });
      client.invalidateQueries({ queryKey: keys.dashboard(n.teamId) });
      if (n.kind === 'logFile') {
        client.invalidateQueries({ queryKey: keys.logFile(n.id) });
        client.invalidateQueries({ queryKey: keys.jobs(n.id) });
        if (n.status === 'AnalysisPending' || n.status === 'Completed') {
          // New processing output: signals, events and parameters were replaced.
          client.invalidateQueries({ queryKey: ['flight', n.flightId] });
        }
      } else if (n.kind === 'aiAnalysis') {
        client.invalidateQueries({ queryKey: keys.aiAnalyses(n.flightId) });
        client.invalidateQueries({ queryKey: keys.aiAnalysis(n.id) });
      } else {
        client.invalidateQueries({ queryKey: keys.runs(n.flightId) });
        client.invalidateQueries({ queryKey: keys.run(n.id) });
      }
    });
    connection.onreconnecting(() => setState('reconnecting'));
    connection.onreconnected(() => {
      setState('connected');
      // Notifications sent while disconnected are lost: refresh what may have changed.
      client.invalidateQueries({ queryKey: ['flights'] });
      client.invalidateQueries({ queryKey: ['flight'] });
      client.invalidateQueries({ queryKey: ['dashboard'] });
    });
    connection.onclose(() => setState('disconnected'));

    let stopped = false;
    let retry: ReturnType<typeof setTimeout> | undefined;
    const start = async () => {
      setState('connecting');
      try {
        await connection.start();
        if (!stopped) {
          setState('connected');
        }
      } catch {
        if (!stopped && connection.state === HubConnectionState.Disconnected) {
          setState('disconnected');
          retry = setTimeout(start, 15_000);
        }
      }
    };
    void start();

    return () => {
      stopped = true;
      clearTimeout(retry);
      void connection.stop();
    };
  }, [enabled, client]);

  return state;
}
