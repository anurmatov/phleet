import { UpgradeWebSocket, WSContext, WSEvents } from "../../helper/websocket/index.js";
//#region src/adapter/bun/websocket.d.ts
/**
 * @internal
 * @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
 */
export interface BunServerWebSocket<T> {
  send(data: string | ArrayBuffer | Uint8Array, compress?: boolean): void;
  close(code?: number, reason?: string): void;
  data: T;
  readyState: 0 | 1 | 2 | 3;
}
/**
 * @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
 */
export interface BunWebSocketHandler<T> {
  open(ws: BunServerWebSocket<T>): void;
  close(ws: BunServerWebSocket<T>, code?: number, reason?: string): void;
  message(ws: BunServerWebSocket<T>, message: string | {
    buffer: ArrayBufferLike;
  }): void;
}
interface CreateWebSocket<T> {
  upgradeWebSocket: UpgradeWebSocket<T>;
  websocket: BunWebSocketHandler<BunWebSocketData>;
}
/**
 * @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
 */
export interface BunWebSocketData {
  events: WSEvents;
  url: URL;
  protocol: string;
}
/**
 * @internal
 * @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
 */
export declare const createWSContext: (ws: BunServerWebSocket<BunWebSocketData>) => WSContext;
/**
 * @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
 */
export declare const upgradeWebSocket: UpgradeWebSocket<any>;
/**
 * @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
 */
export declare const websocket: BunWebSocketHandler<BunWebSocketData>;
/**
 * @deprecated Import `upgradeWebSocket` and `websocket` directly from `hono/bun` instead.
 * @returns A function to create a Bun WebSocket handler.
 */
export declare const createBunWebSocket: <T>() => CreateWebSocket<T>;
//#endregion
export {};
