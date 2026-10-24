Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_adapter_aws_lambda_conninfo = require("./conninfo.js");
const require_adapter_aws_lambda_handler = require("./handler.js");
exports.defaultIsContentTypeBinary = require_adapter_aws_lambda_handler.defaultIsContentTypeBinary;
exports.getConnInfo = require_adapter_aws_lambda_conninfo.getConnInfo;
exports.handle = require_adapter_aws_lambda_handler.handle;
exports.streamHandle = require_adapter_aws_lambda_handler.streamHandle;
