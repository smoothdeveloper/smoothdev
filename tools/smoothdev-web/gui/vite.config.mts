import { defineConfig } from 'vite'
import fable from 'vite-plugin-fable'

// The console's API is the smoothdev-web process that serves it (`smoothdev-web scan gui`).
// In development the page asks `<anything>/api/...` relative to itself, so it works under a site's base too.
const consoleUrl = process.env.SMOOTHDEV_WEB_CONSOLE_URL ?? 'http://127.0.0.1:5399'

export default defineConfig({
  base: './',
  plugins: [fable({ fsproj: './SmoothDev.Web.Gui.fsproj' })],
  // the shared controls' sources sit outside this folder (../../../ui): resolve their imports (lit) from here
  resolve: { dedupe: ['lit', 'lit-html', 'lit-element', '@lit/reactive-element'] },
  server: {
    port: 5401,
    proxy: {
      '^.*/api/': { target: consoleUrl, rewrite: path => path.replace(/^.*\/api\//, '/api/') }
    }
  },
  build: { outDir: 'dist', emptyOutDir: true }
})
