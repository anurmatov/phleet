import { WSContext, defineWebSocketHelper } from "../../helper/websocket/index.js";
//#region src/adapter/deno/websocket.ts
/**
* @deprecated `hono/deno` will be removed in v5. Install `@hono/deno` and import from there instead.
*/
const upgradeWebSocket = defineWebSocketHelper(async (c, events, options) => {
	if (c.req.header("upgrade") !== "websocket") return;
	const subprotocol = c.req.header("sec-websocket-protocol")?.split(",")[0]?.trim();
	const { response, socket } = Deno.upgradeWebSocket(c.req.raw, {
		...subprotocol ? { protocol: subprotocol } : {},
		...options
	});
	const wsContext = new WSContext({
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
export { upgradeWebSocket };
