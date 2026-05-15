import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include:    ["src/**/*.test.ts", "../shared/**/*.test.ts"],
    environment: "node",
    globals:     false,
  },
});
