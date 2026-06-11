/// <reference types="vite/client" />
/// <reference types="vite-plugin-pwa/vue" />

interface ViteTypeOptions {
  strictImportMetaEnv: unknown;
}

// strictImportMetaEnv (above) makes any VITE_ variable NOT declared here a
// type error at the import.meta.env access site — declare every variable you
// introduce. See docs/CONFIG.md for the full environment-config workflow.
interface ImportMetaEnv {
  /**
   * App name used as the document-title suffix (src/router/index.ts);
   * set in .env / .env.local (see .env.example).
   */
  readonly VITE_APP_TITLE?: string;
}
