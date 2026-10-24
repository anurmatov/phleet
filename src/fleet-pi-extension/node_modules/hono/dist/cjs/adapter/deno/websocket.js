Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_helper_websocket_index = require("../../helper/websocket/index.js");
//#region src/adapter/deno/websocket.ts
/**
* @deprecated `hono/deno` will be removed in v5. Install `@hono/deno` and import from there instead.
*/
const upgradeWebSocket = require_helper_websocket_index.defineWebSocketHelper(async (c, events, options) => {
	if (c.req.header("upgrade") !== "websocket") return;
	const subprotocol = c.req.header("sec-websocket-protocol")?.split(",")[0]?.trim();
	const { response, socket } = Deno.upgradeWebSocket(c.req.raw, {
		...subprotocol ? { protocol: subprotocol } : {},
		...options
	});
	const wsContext = new require_helper_websocket_index.WSContext({
		close: (code, reason) => socket.close(code, reason),
		get protocol() {
			return socket.protocol;
		},
		raw: socket,
		get readyState() {
			return socket.readyState;
		},
		url: socket.url ? new URL(socket.url) : null,
		send: (source) => socket.send(source)
	});
	socket.onopen = (evt) => events.onOpen?.(evt, wsContext);
	socket.onmessage = (evt) => events.onMessage?.(evt, wsContext);
	socket.onclose = (evt) => events.onClose?.(evt, wsContext);
	socket.onerror = (evt) => events.onError?.(evt, wsContext);
	return response;
});
//#endregion
exports.upgradeWebSocket = upgradeWebSocket;
