import { UpgradeWebSocket } from "../../helper/websocket/index.js";
//#region src/adapter/deno/websocket.d.ts
/**
 * @deprecated `hono/deno` will be removed in v5. Install `@hono/deno` and import from there instead.
 */
export declare const upgradeWebSocket: UpgradeWebSocket<WebSocket, Deno.UpgradeWebSocketOptions>;
//#endregion