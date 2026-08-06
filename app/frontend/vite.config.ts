import path from "path";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// https://vitejs.dev/config/
export default defineConfig({
    plugins: [react()],
    build: {
        outDir: "../backend/static",
        emptyOutDir: true,
        sourcemap: false,
        chunkSizeWarningLimit: 1000,
        target: "es2020",
        minify: "esbuild",
        cssMinify: true,
        rollupOptions: {
            output: {
                // Function form (not the object form) so that subpath imports
                // like `react-dom/client` and the `react/jsx-runtime` used by
                // the automatic JSX transform are matched by resolved path.
                // The object form only matched the bare specifiers, so React
                // fell through into the entry chunk and react-vendor built empty.
                manualChunks(id) {
                    if (!id.includes("node_modules")) return;
                    if (/[\\/]node_modules[\\/](react|react-dom|scheduler)[\\/]/.test(id)) return "react-vendor";
                    if (/[\\/]node_modules[\\/]@radix-ui[\\/]/.test(id)) return "ui-vendor";
                    if (/[\\/]node_modules[\\/](i18next|react-i18next|i18next-browser-languagedetector|i18next-http-backend)[\\/]/.test(id)) return "i18n";
                    if (/[\\/]node_modules[\\/](framer-motion|motion-dom|motion-utils)[\\/]/.test(id)) return "motion";
                },
                assetFileNames: "assets/[name]-[hash][extname]",
                chunkFileNames: "js/[name]-[hash].js",
                entryFileNames: "js/[name]-[hash].js"
            }
        }
    },
    resolve: {
        preserveSymlinks: true,
        alias: {
            "@": path.resolve(__dirname, "./src")
        }
    },
    server: {
        proxy: {
            "/realtime": {
                target: "ws://localhost:8000",
                ws: true,
                rewriteWsOrigin: true
            }
        }
    },
    test: {
        globals: true,
        environment: "jsdom",
        setupFiles: "./src/test/setup.ts",
        css: true,
        coverage: {
            provider: "v8",
            reporter: ["text", "lcov"],
            include: ["src/components/ui/order-summary.tsx", "src/components/ui/status-message.tsx"]
        }
    }
});
