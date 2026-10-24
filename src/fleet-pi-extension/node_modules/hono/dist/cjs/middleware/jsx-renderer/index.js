Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_html = require("../../utils/html.js");
const require_helper_html_index = require("../../helper/html/index.js");
const require_jsx_context = require("../../jsx/context.js");
const require_jsx_base = require("../../jsx/base.js");
const require_jsx_streaming = require("../../jsx/streaming.js");
require("../../jsx/index.js");
//#region src/middleware/jsx-renderer/index.ts
const RequestContext = require_jsx_context.createContext(null);
const createRenderer = (c, Layout, component, options) => (children, props) => {
	options = typeof options === "function" ? options(c) : options;
	const docType = typeof options?.docType === "string" ? options.docType : options?.docType === false ? "" : "<!DOCTYPE html>";
	const currentLayout = component ? require_jsx_base.jsx((props) => component(props, c), {
		Layout,
		...props
	}, children) : children;
	const body = require_helper_html_index.html`${require_utils_html.raw(docType)}${require_jsx_base.jsx(RequestContext.Provider, { value: c }, currentLayout)}`;
	if (options?.stream) {
		if (options.stream === true) {
			c.header("Transfer-Encoding", "chunked");
			c.header("Content-Type", "text/html; charset=UTF-8");
			c.header("Content-Encoding", "Identity");
		} else for (const [key, value] of Object.entries(options.stream)) c.header(key, value);
		return c.body(require_jsx_streaming.renderToReadableStream(body));
	} else return c.html(body);
};
/**
* JSX Renderer Middleware for hono.
*
* @see {@link https://hono.dev/docs/middleware/builtin/jsx-renderer}
*
* @param {ComponentWithChildren} [component] - The component to render, which can accept children and props.
* @param {RendererOptions} [options] - The options for the JSX renderer middleware.
* @param {boolean | string} [options.docType=true] - The DOCTYPE to be added at the beginning of the HTML. If set to false, no DOCTYPE will be added.
* @param {boolean | Record<string, string>} [options.stream=false] - If set to true, enables streaming response with default headers. If a record is provided, custom headers will be used.
* @returns {MiddlewareHandler} The middleware handler function.
*
* @example
* ```ts
* const app = new Hono()
*
* app.get(
*   '/page/*',
*   jsxRenderer(({ children }) => {
*     return (
*       <html>
*         <body>
*           <header>Menu</header>
*           <div>{children}</div>
*         </body>
*       </html>
*     )
*   })
* )
*
* app.get('/page/about', (c) => {
*   return c.render(<h1>About me!</h1>)
* })
* ```
*/
const jsxRenderer = (component, options) => function jsxRenderer(c, next) {
	const Layout = c.getLayout() ?? require_jsx_base.Fragment;
	if (component) c.setLayout((props) => {
		return component({
			...props,
			Layout
		}, c);
	});
	c.setRenderer(createRenderer(c, Layout, component, options));
	return next();
};
/**
* useRequestContext for Hono.
*
* @template E - The environment type.
* @template P - The parameter type.
* @template I - The input type.
* @returns {Context<E, P, I>} An instance of Context.
*
* @example
* ```ts
* const RequestUrlBadge: FC = () => {
*   const c = useRequestContext()
*   return <b>{c.req.url}</b>
* }
*
* app.get('/page/info', (c) => {
*   return c.render(
*     <div>
*       You are accessing: <RequestUrlBadge />
*     </div>
*   )
* })
* ```
*/
const useRequestContext = () => {
	const c = require_jsx_context.useContext(RequestContext);
	if (!c) throw new Error("RequestContext is not provided.");
	return c;
};
//#endregion
exports.RequestContext = RequestContext;
exports.jsxRenderer = jsxRenderer;
exports.useRequestContext = useRequestContext;
