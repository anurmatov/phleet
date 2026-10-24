Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_adapter_cloudflare_pages_conninfo = require("./conninfo.js");
const require_adapter_cloudflare_pages_handler = require("./handler.js");
exports.getConnInfo = require_adapter_cloudflare_pages_conninfo.getConnInfo;
exports.handle = require_adapter_cloudflare_pages_handler.handle;
exports.handleMiddleware = require_adapter_cloudflare_pages_handler.handleMiddleware;
exports.serveStatic = require_adapter_cloudflare_pages_handler.serveStatic;
