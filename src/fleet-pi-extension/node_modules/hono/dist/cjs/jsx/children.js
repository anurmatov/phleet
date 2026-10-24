Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
//#region src/jsx/children.ts
const toArray = (children) => Array.isArray(children) ? children : [children];
const Children = {
	map: (children, fn) => toArray(children).map(fn),
	forEach: (children, fn) => {
		toArray(children).forEach(fn);
	},
	count: (children) => toArray(children).length,
	only: (_children) => {
		const children = toArray(_children);
		if (children.length !== 1) throw new Error("Children.only() expects only one child");
		return children[0];
	},
	toArray
};
//#endregion
exports.Children = Children;
exports.toArray = toArray;
