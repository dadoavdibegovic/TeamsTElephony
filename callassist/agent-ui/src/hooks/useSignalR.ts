import { useEffect, useRef, useState, useCallback } from "react";
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import type { CallEventName, CallEventPayload } from "../types/callTypes";
import { ConnectionState } from "../types/callTypes";

type Handler<N extends CallEventName> = (payload: CallEventPayload<N>) => void;

function mapState(state: HubConnectionState): ConnectionState {
  switch (state) {
    case HubConnectionState.Connected:    return ConnectionState.Connected;
    case HubConnectionState.Connecting:   return ConnectionState.Connecting;
    case HubConnectionState.Reconnecting: return ConnectionState.Reconnecting;
    default:                              return ConnectionState.Disconnected;
  }
}

export interface UseSignalR {
  on:              <N extends CallEventName>(event: N, handler: Handler<N>) => void;
  off:             <N extends CallEventName>(event: N, handler: Handler<N>) => void;
  connectionState: ConnectionState;
}

export function useSignalR(): UseSignalR {
  const [connectionState, setConnectionState] = useState<ConnectionState>(
    ConnectionState.Disconnected,
  );
  const connectionRef = useRef<HubConnection | null>(null);

  useEffect(() => {
    const backendUrl = import.meta.env.VITE_BACKEND_URL;
    const hubName    = import.meta.env.VITE_SIGNALR_HUB;
    if (!backendUrl || !hubName) {
      console.error("VITE_BACKEND_URL or VITE_SIGNALR_HUB is not set");
      return;
    }

    const url = `${String(backendUrl).replace(/\/+$/, "")}/${hubName}`;

    const conn = new HubConnectionBuilder()
      .withUrl(url)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    conn.onreconnecting(() => setConnectionState(ConnectionState.Reconnecting));
    conn.onreconnected(()  => setConnectionState(ConnectionState.Connected));
    conn.onclose(()        => setConnectionState(ConnectionState.Disconnected));

    setConnectionState(ConnectionState.Connecting);
    conn
      .start()
      .then(() => setConnectionState(mapState(conn.state)))
      .catch((err) => {
        console.error("SignalR connect failed", err);
        setConnectionState(ConnectionState.Disconnected);
      });

    connectionRef.current = conn;

    return () => {
      connectionRef.current = null;
      conn.stop().catch(() => {});
    };
  }, []);

  const on = useCallback(
    <N extends CallEventName>(event: N, handler: Handler<N>) => {
      connectionRef.current?.on(event, handler as (...args: unknown[]) => void);
    },
    [],
  );

  const off = useCallback(
    <N extends CallEventName>(event: N, handler: Handler<N>) => {
      connectionRef.current?.off(event, handler as (...args: unknown[]) => void);
    },
    [],
  );

  return { on, off, connectionState };
}
