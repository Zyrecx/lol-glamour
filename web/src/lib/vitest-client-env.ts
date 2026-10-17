import type { Environment } from 'vitest/runtime'

/** Node, but modules are transformed for the browser, so Svelte runes ($effect) run in tests as they do in the app. */
export default <Environment>{
  name: 'client-node',
  viteEnvironment: 'client',
  setup() { return { teardown() {} } },
}
