//#region src/jsx/intrinsic-element/common.ts
const deDupeKeyMap = {
	title: [],
	script: ["src"],
	style: ["data-href"],
	link: ["href"],
	meta: [
		"name",
		"httpEquiv",
		"charset",
		"itemProp"
	]
};
const domRenderers = {};
const dataPrecedenceAttr = "data-precedence";
const isStylesheetLinkWithPrecedence = (props) => props.rel === "stylesheet" && "precedence" in props;
const shouldDeDupeByKey = (tagName, supportSort) => {
	if (tagName === "link") return supportSort;
	return deDupeKeyMap[tagName].length > 0;
};
//#endregion
export { dataPrecedenceAttr, deDupeKeyMap, domRenderers, isStylesheetLinkWithPrecedence, shouldDeDupeByKey };
