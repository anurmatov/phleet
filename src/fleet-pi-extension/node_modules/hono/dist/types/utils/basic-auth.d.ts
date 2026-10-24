//#region src/utils/basic-auth.d.ts
export type Auth = (req: Request) => {
  username: string;
  password: string;
} | undefined;
export declare const auth: Auth;
//#endregion