export default {
  // Keep the source mount read-only; dependencies live in the development image.
  cacheDir: "/tmp/goblin-website-vite",
  // This static website does not consume environment configuration.
  envDir: false,
  envPrefix: [],
  server: {
    host: "0.0.0.0",
    port: 5173,
    strictPort: true,
  },
};
