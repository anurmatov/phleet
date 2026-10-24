import { UpgradeWebSocket, WSEvents } from "../../helper/websocket/index.js";
//#region src/adapter/cloudflare-workers/websocket.d.ts
/**
 * @deprecated `hono/cloudflare-workers` will be removed in v5. Install `@hono/cloudflare-workers` and import from there instead.
 */
export declare const upgradeWebSocket: UpgradeWebSocket<WebSocket, any, Omit<WSEvents<WebSocket>, 'onOpen'>>;
//#endregion