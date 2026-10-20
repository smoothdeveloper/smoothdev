import { defineConfig } from 'vite'
import fable from 'vite-plugin-fable'

export default defineConfig({
  base: './',
  plugins: [fable({ fsproj: './gallery.fsproj' })],
  // the components' sources sit outside this folder: resolve their imports (lit) from here
  resolve: { dedupe: ['lit', 'lit-html', 'lit-element', '@lit/reactive-element'] },
  server: { port: 5410 },
  build: { outDir: 'dist', emptyOutDir: true }
})
