Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_adapter_cloudflare_workers_conninfo = require("./conninfo.js");
const require_adapter_cloudflare_workers_serve_static_module = require("./serve-static-module.js");
const require_adapter_cloudflare_workers_websocket = require("./websocket.js");
exports.getConnInfo = require_adapter_cloudflare_workers_conninfo.getConnInfo;
exports.serveStatic = require_adapter_cloudflare_workers_serve_static_module.serveStatic;
exports.upgradeWebSocket = require_adapter_cloudflare_workers_websocket.upgradeWebSocket;
