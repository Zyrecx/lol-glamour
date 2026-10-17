<script lang="ts">
  import ClientRepair from '../../lib/sections/ClientRepair.svelte'
  import DesktopApp from '../../lib/sections/DesktopApp.svelte'
  import SendLog from '../../lib/sections/SendLog.svelte'
  import AboutApp from '../../lib/sections/AboutApp.svelte'
  import Toggle from '../../lib/components/Toggle.svelte'
  import { motion } from '../../shell/motion.svelte'
</script>

<header class="page-head">
  <span class="eyebrow">This PC</span>
  <h1 class="display">Settings</h1>
</header>

<ClientRepair />
<DesktopApp />
<SendLog />
<AboutApp />
<section class="panel">
  <Toggle label="Reduce motion" hint="Stops animations." checked={motion.reduced} onchange={(v) => motion.set(v)} />
</section>
