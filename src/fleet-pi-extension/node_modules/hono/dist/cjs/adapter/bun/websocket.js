Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_adapter_bun_server = require("./server.js");
const require_helper_websocket_index = require("../../helper/websocket/index.js");
//#region src/adapter/bun/websocket.ts
/**
* @internal
* @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
*/
const createWSContext = (ws) => {
	return new require_helper_websocket_index.WSContext({
		send: (source, options) => {
			ws.send(source, options?.compress);
		},
		raw: ws,
		readyState: ws.readyState,
		url: ws.data.url,
		protocol: ws.data.protocol,
		close(code, reason) {
			ws.close(code, reason);
		}
	});
};
/**
* @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
*/
const upgradeWebSocket = require_helper_websocket_index.defineWebSocketHelper((c, events) => {
	const server = require_adapter_bun_server.getBunServer(c);
	if (!server) throw new TypeError("env has to include the 2nd argument of fetch.");
	if (server.upgrade(c.req.raw, { data: {
		events,
		url: new URL(c.req.url),
		protocol: c.req.header("sec-websocket-protocol")?.split(",")[0]?.trim() ?? ""
	} })) return new Response(null);
});
/**
* @deprecated `hono/bun` will be removed in v5. Install `@hono/bun` and import from there instead.
*/
const websocket = {
	open(ws) {
		const websocketListeners = ws.data.events;
		if (websocketListeners.onOpen) websocketListeners.onOpen(new Event("open"), createWSContext(ws));
	},
	close(ws, code, reason) {
		const websocketListeners = ws.data.events;
		if (websocketListeners.onClose) websocketListeners.onClose(new CloseEvent("close", {
			code,
			reason
		}), createWSContext(ws));
	},
	message(ws, message) {
		const websocketListeners = ws.data.events;
		if (websocketListeners.onMessage) {
			const normalizedReceiveData = typeof message === "string" ? message : message.buffer;
			websocketListeners.onMessage(require_helper_websocket_index.createWSMessageEvent(normalizedReceiveData), createWSContext(ws));
		}
	}
};
/**
* @deprecated Import `upgradeWebSocket` and `websocket` directly from `hono/bun` instead.
* @returns A function to create a Bun WebSocket handler.
*/
const createBunWebSocket = () => ({
	upgradeWebSocket,
	websocket
});
//#endregion
exports.createBunWebSocket = createBunWebSocket;
exports.createWSContext = createWSContext;
exports.upgradeWebSocket = upgradeWebSocket;
exports.websocket = websocket;
