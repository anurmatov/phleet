import { IntrinsicElements } from "../intrinsic-elements.js";
import { PropsWithChildren } from "../types.js";
import { FC } from "../base.js";
//#region src/jsx/intrinsic-element/components.d.ts
export declare const title: FC<PropsWithChildren>;
export declare const script: FC<PropsWithChildren<IntrinsicElements['script']>>;
export declare const style: FC<PropsWithChildren<IntrinsicElements['style']>>;
export declare const link: FC<PropsWithChildren<IntrinsicElements['link']>>;
export declare const meta: FC<PropsWithChildren>;
export declare const form: FC<PropsWithChildren<{
  action?: Function | string;
  method?: 'get' | 'post';
}>>;
export declare const input: (props: PropsWithChildren) => unknown;
export declare const button: (props: PropsWithChildren) => unknown;
//#endregion