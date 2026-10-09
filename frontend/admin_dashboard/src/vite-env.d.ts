/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** The API gateway as the browser sees it, set at build time. */
  readonly VITE_API_BASE_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
